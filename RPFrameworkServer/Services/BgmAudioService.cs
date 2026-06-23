using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RPFrameworkServer.Services;

/// <summary>
/// Server-side BGM audio pipeline. The server (not the client) runs yt-dlp + ffmpeg to download a
/// YouTube track and transcode it to raw PCM WAV, caches the result, and serves it over HTTP. WAV is
/// deliberate: clients decode it with NAudio's WaveFileReader, which needs no OS codec at all, so the
/// same file plays on native Windows (incl. "N" editions with no AAC/MP3 decoder), Wine, etc.
///
/// yt-dlp and ffmpeg are expected on PATH (baked into the Docker image). Access is gated by short-lived
/// HMAC-signed URLs minted over the hub, so the endpoint can't be used as an open YouTube proxy.
/// </summary>
public sealed class BgmAudioService
{
    private readonly ILogger<BgmAudioService> _log;
    private readonly string _cacheDir;
    private readonly byte[] _signingKey;

    // YouTube IDs are ~11 url-safe chars. This also defends the cache path + yt-dlp argv: nothing else
    // is ever interpolated into a filename or command line.
    private static readonly Regex VideoIdPattern = new(@"^[A-Za-z0-9_-]{6,16}$", RegexOptions.Compiled);

    // One in-flight prepare per video id, so simultaneous room members never transcode the same track twice.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // yt-dlp + ffmpeg can be slow on a cold cache; cap so a wedged child can't hang a request forever.
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(5);

    // ── Cache-bleed defenses ──────────────────────────────────────────────────
    // The cache is uncompressed PCM WAV keyed by video id (shared across every room/campaign that plays
    // the track - dedup is deliberate). Without bounds, scripted distinct-id plays fill the disk. Three
    // guards: a global byte ceiling (LRU eviction), a last-access TTL (cold tracks expire), and a cap on
    // simultaneous transcodes (a burst of distinct ids can't fork-storm yt-dlp).
    private readonly long          _maxCacheBytes;       // hard ceiling on total cached WAV bytes
    private readonly TimeSpan      _staleAfter;          // evict files not served within this window
    private readonly SemaphoreSlim _prepareThrottle;     // bounds concurrent yt-dlp+ffmpeg children
    private readonly SemaphoreSlim _evictGate = new(1, 1); // serializes eviction passes

    public BgmAudioService(IConfiguration config, ILogger<BgmAudioService> log)
    {
        _log      = log;
        _cacheDir = config["Bgm:CacheDir"] ?? Environment.GetEnvironmentVariable("BGM_CACHE_DIR") ?? "/app/bgm_cache";
        Directory.CreateDirectory(_cacheDir);

        // Early-candidate defaults; expect to retune once telemetry shows real cache pressure.
        _maxCacheBytes = ReadLong(config, "Bgm:MaxCacheBytes", "BGM_MAX_CACHE_BYTES", 5L * 1024 * 1024 * 1024); // 5 GB
        int staleDays  = (int)ReadLong(config, "Bgm:StaleAfterDays", "BGM_STALE_AFTER_DAYS", 30);
        _staleAfter    = TimeSpan.FromDays(Math.Max(1, staleDays));
        int maxPrepare = (int)ReadLong(config, "Bgm:MaxConcurrentPrepares", "BGM_MAX_CONCURRENT_PREPARES", 3);
        maxPrepare     = Math.Clamp(maxPrepare, 1, 16);
        _prepareThrottle = new SemaphoreSlim(maxPrepare, maxPrepare);

        var key = config["Bgm:SigningKey"] ?? Environment.GetEnvironmentVariable("BGM_SIGNING_KEY");
        if (string.IsNullOrEmpty(key))
        {
            // No configured key: mint a random per-process one. Signed URLs simply stop validating after
            // a restart, which is harmless (clients re-resolve on demand).
            _signingKey = RandomNumberGenerator.GetBytes(32);
            _log.LogWarning("Bgm:SigningKey not set — using a random per-process key (signed audio URLs reset on restart).");
        }
        else _signingKey = Encoding.UTF8.GetBytes(key);
    }

    public static bool IsValidVideoId(string? id) => id != null && VideoIdPattern.IsMatch(id);

    /// <summary>Reads a numeric setting from config (Bgm:Key), env (ENV_VAR), or falls back to a default.</summary>
    private static long ReadLong(IConfiguration config, string key, string envVar, long fallback)
    {
        var raw = config[key] ?? Environment.GetEnvironmentVariable(envVar);
        return long.TryParse(raw, out var v) && v > 0 ? v : fallback;
    }

    // ── Signed-URL minting / verification ─────────────────────────────────────

    /// <summary>Builds a signed, time-limited relative URL (e.g. "/bgm/abc123?exp=…&sig=…") for a video id.</summary>
    public string BuildSignedPath(string videoId, TimeSpan validFor)
    {
        long exp = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        string sig = Sign(videoId, exp);
        return $"/bgm/{videoId}?exp={exp}&sig={sig}";
    }

    public bool Verify(string videoId, long exp, string? sig)
    {
        if (sig == null) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;
        string expected = Sign(videoId, exp);
        // Constant-time compare to avoid leaking the signature byte-by-byte.
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(expected));
    }

    private string Sign(string videoId, long exp)
    {
        using var hmac = new HMACSHA256(_signingKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{videoId}|{exp}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Prepare (download + transcode + cache) ────────────────────────────────

    /// <summary>
    /// Returns the path to a cached WAV for the given video id, downloading + transcoding it first if
    /// needed. Returns null if the id is invalid or yt-dlp/ffmpeg failed.
    /// </summary>
    public async Task<string?> GetWavPathAsync(string videoId, CancellationToken ct)
    {
        if (!IsValidVideoId(videoId)) return null;
        string wavPath = Path.Combine(_cacheDir, videoId + ".wav");
        if (File.Exists(wavPath)) { Touch(wavPath); return wavPath; }

        string? produced;
        var gate = _locks.GetOrAdd(videoId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(wavPath)) { Touch(wavPath); return wavPath; } // produced while we waited

            // Global transcode throttle: many distinct ids requested at once still spawn only a bounded
            // number of yt-dlp+ffmpeg children; the rest queue here (cancellable via ct).
            await _prepareThrottle.WaitAsync(ct);
            try     { produced = await DownloadAndTranscodeAsync(videoId, wavPath, ct); }
            finally { _prepareThrottle.Release(); }
        }
        finally { gate.Release(); }

        // Enforce the cache ceiling after a fresh download (outside the per-id gate). The just-written
        // file has the newest mtime, so LRU eviction won't immediately reclaim it.
        if (produced != null) await EnforceCacheLimitsAsync();
        return produced;
    }

    /// <summary>Marks a cache file as just-used so last-access (mtime) drives TTL + LRU eviction. We set
    /// it explicitly rather than rely on filesystem atime, which is often disabled (noatime mounts).</summary>
    private void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    /// <summary>
    /// Bounds the cache: first drops files not served within the TTL (cold tracks), then, if still over
    /// the global byte ceiling, evicts least-recently-used files until under it. Deleting only removes the
    /// cached WAV - the playlist row is separate, so a later play simply re-downloads on demand. Safe to
    /// call concurrently (serialized internally); cheap enough to run after each download and on a timer.
    /// </summary>
    public async Task EnforceCacheLimitsAsync()
    {
        await _evictGate.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var files = new DirectoryInfo(_cacheDir).GetFiles("*.wav");

            foreach (var f in files)
                if (now - f.LastWriteTimeUtc > _staleAfter)
                    TryDeleteFile(f.FullName);

            // Re-stat after the stale pass, then LRU-evict down to the ceiling if needed.
            files = new DirectoryInfo(_cacheDir).GetFiles("*.wav");
            long total = files.Sum(f => f.Length);
            if (total <= _maxCacheBytes) return;

            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= _maxCacheBytes) break;
                if (TryDeleteFile(f.FullName)) total -= f.Length;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "BGM cache eviction pass failed"); }
        finally { _evictGate.Release(); }
    }

    private bool TryDeleteFile(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) { _log.LogDebug(ex, "BGM cache: could not delete {Path}", path); return false; }
    }

    private async Task<string?> DownloadAndTranscodeAsync(string videoId, string wavPath, CancellationToken ct)
    {
        // yt-dlp grabs the best audio stream and (via ffmpeg on PATH) transcodes it to WAV. "--" stops
        // option parsing so the validated id can never be read as a flag.
        string outTemplate = Path.Combine(_cacheDir, videoId + ".%(ext)s");
        var psi = new ProcessStartInfo
        {
            FileName               = "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--max-filesize"); psi.ArgumentList.Add("200M");
        psi.ArgumentList.Add("-f");             psi.ArgumentList.Add("bestaudio/best");
        psi.ArgumentList.Add("-x");
        psi.ArgumentList.Add("--audio-format");  psi.ArgumentList.Add("wav");
        psi.ArgumentList.Add("--no-part");
        psi.ArgumentList.Add("-o");             psi.ArgumentList.Add(outTemplate);
        psi.ArgumentList.Add("--");             psi.ArgumentList.Add(videoId);

        try
        {
            using var proc = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            proc.Start();
            // Drain both streams so neither buffer can fill and deadlock the child.
            var errTask = Task.Run(async () => stderr.Append(await proc.StandardError.ReadToEndAsync(ct)), ct);
            _ = Task.Run(async () => await proc.StandardOutput.ReadToEndAsync(ct), ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PrepareTimeout);
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                _log.LogWarning("yt-dlp timed out for {VideoId}", videoId);
                return null;
            }
            await errTask;

            if (proc.ExitCode != 0)
            {
                string err = stderr.ToString();
                _log.LogWarning("yt-dlp exited {Code} for {VideoId}: {Err}", proc.ExitCode, videoId,
                                err.Length > 600 ? err[^600..] : err);
                TryCleanup(videoId, wavPath);
                return null;
            }

            if (File.Exists(wavPath)) return wavPath;
            _log.LogWarning("yt-dlp succeeded but no WAV produced for {VideoId}", videoId);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BGM prepare failed for {VideoId}", videoId);
            TryCleanup(videoId, wavPath);
            return null;
        }
    }

    // Remove a half-written WAV plus any leftover intermediate yt-dlp downloaded for this id.
    private void TryCleanup(string videoId, string wavPath)
    {
        try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
        try
        {
            foreach (var f in Directory.EnumerateFiles(_cacheDir, videoId + ".*"))
                if (!f.EndsWith(".wav")) File.Delete(f);
        }
        catch { }
    }
}
