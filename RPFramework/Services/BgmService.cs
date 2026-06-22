using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Generic;
using RPFramework.Contracts;
using YoutubeExplode;
using YoutubeExplode.Videos;

namespace RPFramework.Services;

/// <summary>
/// Handles audio playback for RPBGM. The CLIENT no longer touches yt-dlp, ffmpeg, or any OS codec:
/// the server prepares a PCM WAV for a video id and serves it over a short-lived HMAC-signed URL
/// (resolved via <see cref="ResolveAudioUrl"/>). The client just HTTP-downloads that WAV into a local
/// cache and plays it with NAudio's <see cref="WaveFileReader"/> (no codec dependency at all).
/// YoutubeExplode is kept only for lightweight title/metadata fetching.
/// </summary>
public class BgmService : IDisposable
{
    // Title-only metadata client (less likely to be blocked than stream extraction)
    private static readonly YoutubeClient Youtube = new();

    // Shared HttpClient for downloading the server-prepared WAV
    private static readonly HttpClient Http = MakeHttp();
    private static HttpClient MakeHttp()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.Add("User-Agent", "RPFramework-DalamudPlugin/1.0");
        return c;
    }

    private readonly string cacheDir;

    /// <summary>
    /// Resolves a YouTube video id to a fully-qualified, server-signed WAV download URL (set by Plugin
    /// to <c>NetworkService.ResolveBgmAudio</c>). The server runs yt-dlp + ffmpeg and serves PCM WAV, so
    /// the client needs no yt-dlp, no ffmpeg, and no OS audio codec. Null until wired / when offline.
    /// </summary>
    public Func<string, Task<string?>>? ResolveAudioUrl { get; set; }

    private WaveOutEvent?         outputDevice;
    private WaveStream?           audioStream;
    private VolumeSampleProvider? volumeProvider;

    private string?         currentRoomCode;
    private List<RpSongDto> playlist = new();
    private LoopMode        loop   = LoopMode.None;
    private float           volume = 0.5f;
    private int             currentSongIndex = -1;

    private volatile bool  isLoading;
    private volatile bool  pendingAdvance;
    private volatile bool  pendingTrackEnded;
    private volatile bool  pendingStopBroadcast;
    private          bool  isMuted;
    private          string? loadError;
    private          float  downloadProgress;  // 0–1
    private          CancellationTokenSource? downloadCts;

    private readonly object playLock = new();

    // A WAV download (incl. server-side yt-dlp + ffmpeg transcode on a cold cache) gets at most this long.
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    // YouTube video IDs: URL-safe base64-ish, ~11 chars. Anything else never reaches
    // the filesystem path or the resolve call.
    private static readonly Regex VideoIdPattern = new(@"^[A-Za-z0-9_-]{6,16}$", RegexOptions.Compiled);

    /// <summary>
    /// Returns true when the URL is a youtube.com / youtu.be link (or a bare video ID).
    /// Used before anything is handed to yt-dlp or the metadata client.
    /// </summary>
    public static bool IsAllowedYoutubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 512) return false;
        if (VideoIdPattern.IsMatch(url)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        string host = uri.Host.ToLowerInvariant();
        return host is "youtube.com" or "www.youtube.com" or "m.youtube.com"
                    or "music.youtube.com" or "youtu.be" or "www.youtu.be";
    }

    /// <summary>
    /// Extracts and validates the video ID. Returns null when the URL is not an allowed
    /// YouTube link or the ID contains unexpected characters (path-traversal defense:
    /// the ID becomes part of the cache file name).
    /// </summary>
    private static string? TryGetVideoId(string url)
    {
        if (!IsAllowedYoutubeUrl(url)) return null;
        try
        {
            string id = VideoId.Parse(url).Value;
            return VideoIdPattern.IsMatch(id) ? id : null;
        }
        catch { return null; }
    }

    // ── Public state ──────────────────────────────────────────────────────────

    public bool    IsPlaying             => outputDevice?.PlaybackState == PlaybackState.Playing;
    public bool    IsPaused              => outputDevice?.PlaybackState == PlaybackState.Paused;
    public bool    IsLoading             => isLoading;
    public bool    IsMuted               => isMuted;
    public string? LoadError             => loadError;
    public int      CurrentSongIndex     => currentSongIndex;
    public string?  CurrentRoomCode      => currentRoomCode;
    public LoopMode Loop                 => loop;
    public float    Volume               => volume;
    public void     SetLoop(LoopMode l)  => loop = l;
    /// <summary>When false (listeners), a finished song does NOT auto-advance — the room drives playback.</summary>
    public bool     AutoAdvance          { get; set; } = true;
    public float   DownloadProgress      => downloadProgress;
    /// <summary>Set when the last song in the playlist ends with no loop. Cleared by the caller after broadcasting.</summary>
    public bool    PendingStopBroadcast  { get => pendingStopBroadcast; set => pendingStopBroadcast = value; }
    /// <summary>Set when a track finishes playing naturally (not a manual stop/song-change). The coordinator (controller) consumes it to drive the next gated song.</summary>
    public bool    PendingTrackEnded     { get => pendingTrackEnded; set => pendingTrackEnded = value; }

    public BgmService(string cacheDir)
    {
        this.cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    // ── Called from Framework.Update (game thread) ────────────────────────────

    public void Update()
    {
        if (!pendingAdvance) return;
        pendingAdvance = false;
        AdvanceToNext();
    }

    // ── Playback control ─────────────────────────────────────────────────────

    /// <summary>Loads a room's playlist and starts playing the given index.</summary>
    public void PlayRoom(string code, List<RpSongDto> pl, int songIndex)
    {
        currentRoomCode = code;
        playlist        = pl ?? new();
        PlayAt(songIndex);
    }

    public void PlayPause()
    {
        if (isLoading) return;
        if (outputDevice == null)
        {
            if (playlist.Count > 0) PlayAt(Math.Max(0, currentSongIndex));
            return;
        }
        if (IsPlaying) outputDevice.Pause(); else outputDevice.Play();
    }

    public void Next()
    {
        if (playlist.Count == 0) return;
        int next = currentSongIndex + 1;
        if (next >= playlist.Count)
        {
            if (loop == LoopMode.All) next = 0; else return;
        }
        PlayAt(next);
    }

    public void Previous()
    {
        if (playlist.Count == 0) return;
        int prev = currentSongIndex - 1;
        if (prev < 0) prev = loop == LoopMode.All ? playlist.Count - 1 : 0;
        PlayAt(prev);
    }

    public void Stop()
    {
        StopPlayback();
        currentSongIndex = -1;
    }

    // The slider runs 0–100%, but full-scale PCM is painfully loud, so cap the actual output gain here.
    // 100% on the slider => this gain. Halving it tamed the "that shit is LOUD" problem.
    private const float MaxGain = 0.5f;

    public void SetVolume(float vol)
    {
        volume = Math.Clamp(vol, 0f, 1f);
        if (volumeProvider != null) volumeProvider.Volume = isMuted ? 0f : volume * MaxGain;
    }

    public void ToggleMute()
    {
        isMuted = !isMuted;
        if (volumeProvider != null) volumeProvider.Volume = isMuted ? 0f : volume * MaxGain;
    }

    /// <summary>Current playback position in seconds. Used by the owner to report position to the server.</summary>
    public double CurrentPositionSeconds
    {
        get { lock (playLock) { return audioStream?.CurrentTime.TotalSeconds ?? 0.0; } }
    }

    /// <summary>Total duration of the currently loaded track in seconds. 0 if nothing is loaded.</summary>
    public double TotalDurationSeconds
    {
        get { lock (playLock) { return audioStream?.TotalTime.TotalSeconds ?? 0.0; } }
    }

    /// <summary>
    /// Seek to a specific position. Called when receiving a network sync command.
    /// Does NOT change IsPlaying state — caller decides whether to Play() after seeking.
    /// </summary>
    public void SeekTo(double seconds)
    {
        lock (playLock)
        {
            if (audioStream == null) return;
            var ts = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, audioStream.TotalTime.TotalSeconds));
            audioStream.CurrentTime = ts;
        }
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void PlayAt(int index)
    {
        if (playlist.Count == 0) return;
        if (index < 0 || index >= playlist.Count) return;

        currentSongIndex = index;
        isLoading        = true;
        loadError        = null;
        downloadProgress = 0f;
        StopPlayback();

        string url = playlist[index].YoutubeUrl;
        Plugin.Log.Debug("[BGM] loading idx={0} room={1} (autoAdvance={2})", index, currentRoomCode ?? "", AutoAdvance);
        Task.Run(async () => await LoadAndPlayAsync(url));
    }

    private async Task LoadAndPlayAsync(string youtubeUrl)
    {
        var cts = new CancellationTokenSource();
        downloadCts = cts;
        try
        {
            // 1. Fetch the server-prepared WAV into cache (or load from cache)
            string? filePath = await GetOrDownloadAsync(youtubeUrl, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (filePath == null)
            {
                if (loadError == null)
                    loadError = "Could not get audio from the server. The video may be unavailable or region-locked.";
                return;
            }

            // 2. Open and play. WaveFileReader decodes PCM WAV with zero OS codec dependency,
            //    so playback is identical on Windows and Linux/Wine.
            lock (playLock)
            {
                try
                {
                    var stream  = new WaveFileReader(filePath);
                    var volProv = new VolumeSampleProvider(stream.ToSampleProvider())
                        { Volume = isMuted ? 0f : volume * MaxGain };
                    var device  = new WaveOutEvent();
                    device.Init(volProv);
                    device.PlaybackStopped += OnPlaybackStopped;
                    device.Play();
                    audioStream    = stream;
                    volumeProvider = volProv;
                    outputDevice   = device;
                    Plugin.Log.Debug("[BGM] playback started: room={0} idx={1}", currentRoomCode ?? "", currentSongIndex);
                }
                catch (Exception ex) { loadError = $"Playback error: {ex.Message}"; Plugin.Log.Warning(ex, "[BGM] open/play failed"); }
            }
        }
        catch (OperationCanceledException) { /* song changed — expected */ }
        catch (Exception ex)               { loadError = $"Load error: {ex.Message}"; }
        finally
        {
            isLoading = false;
            cts.Dispose();
            if (downloadCts == cts) downloadCts = null;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // NAudio reports the real reason a device stopped here, and we used to swallow it. A device that
        // dies the instant after Play() (no audio, button stuck on "Play") shows up as either an
        // exception (decode/output failure) or an immediate end-of-stream (pos≈0 / pos≈len on a
        // zero-length file). Surface both so a single repro pinpoints the cause.
        if (e.Exception != null)
            Plugin.Log.Warning(e.Exception, "[BGM] playback stopped with error");
        else
        {
            double pos, len;
            lock (playLock) { pos = audioStream?.CurrentTime.TotalSeconds ?? -1; len = audioStream?.TotalTime.TotalSeconds ?? -1; }
            Plugin.Log.Debug("[BGM] playback stopped (pos={0:0.0}s len={1:0.0}s isLoading={2})", pos, len, isLoading);
            // A clean end-of-track (no exception, not a deliberate stop — those detach this handler first):
            // signal the coordinator so the controller can advance the room to the next gated song.
            if (!isLoading) pendingTrackEnded = true;
        }
        if (!isLoading) pendingAdvance = true;
    }

    private void AdvanceToNext()
    {
        if (!AutoAdvance) return; // listeners follow the room, they don't advance themselves
        if (playlist.Count == 0) return;
        switch (loop)
        {
            case LoopMode.Single: PlayAt(currentSongIndex); break;
            case LoopMode.All:    PlayAt((currentSongIndex + 1) % playlist.Count); break;
            default:
                if (currentSongIndex + 1 < playlist.Count)
                    PlayAt(currentSongIndex + 1);
                else
                {
                    currentSongIndex = -1;
                    pendingStopBroadcast = true;
                }
                break;
        }
    }

    private void StopPlayback()
    {
        downloadCts?.Cancel();
        lock (playLock)
        {
            if (outputDevice != null)
            {
                outputDevice.PlaybackStopped -= OnPlaybackStopped;
                outputDevice.Stop();
                outputDevice.Dispose();
                outputDevice = null;
            }
            audioStream?.Dispose();
            audioStream    = null;
            volumeProvider = null;
        }
    }

    public void Dispose() => StopPlayback();

    // ── Audio cache ───────────────────────────────────────────────────────────

    // The server only ever serves PCM WAV, so the client cache is WAV-only. (Any leftover .m4a/.webm
    // from the old client-side yt-dlp era is simply ignored and re-fetched as .wav.)
    private static readonly string[] AudioExts = { ".wav" };

    private async Task<string?> GetOrDownloadAsync(string youtubeUrl, CancellationToken ct)
    {
        try
        {
            // Whitelist + ID-shape validation — nothing else reaches the resolve call or the cache path
            string? videoId = TryGetVideoId(youtubeUrl);
            if (videoId == null)
            {
                loadError = "Only youtube.com / youtu.be links are supported.";
                return null;
            }
            string cachePath = Path.Combine(cacheDir, videoId + ".wav");

            // Return cached file if present
            if (File.Exists(cachePath)) { downloadProgress = 1f; return cachePath; }

            // Ask the server (over the hub) for a signed, fully-qualified WAV URL. The server runs
            // yt-dlp + ffmpeg and transcodes to PCM WAV; we never touch either tool locally.
            if (ResolveAudioUrl == null)
            {
                loadError = "Not connected to the server, so audio can't be fetched.";
                return null;
            }

            string? audioUrl = await ResolveAudioUrl(videoId);
            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            if (string.IsNullOrEmpty(audioUrl))
            {
                loadError = "The server could not prepare audio for this video.";
                return null;
            }

            // Stream the WAV to a temp file, then atomically move into place so a cancelled/failed
            // download never leaves a half-written cache entry that later plays as garbage.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DownloadTimeout);
            var dlCt = timeoutCts.Token;

            string tmpPath = cachePath + ".part";
            try
            {
                using (var resp = await Http.GetAsync(audioUrl, HttpCompletionOption.ResponseHeadersRead, dlCt))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        Plugin.Log.Warning("[BGM] server WAV fetch failed {0} for {1}", (int)resp.StatusCode, videoId);
                        loadError = resp.StatusCode == System.Net.HttpStatusCode.NotFound
                            ? "The server could not prepare audio for this video."
                            : $"Audio fetch failed ({(int)resp.StatusCode}).";
                        return null;
                    }

                    long? total = resp.Content.Headers.ContentLength;
                    await using var src = await resp.Content.ReadAsStreamAsync(dlCt);
                    await using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[81920];
                        long read = 0;
                        int n;
                        while ((n = await src.ReadAsync(buf, dlCt)) > 0)
                        {
                            await dst.WriteAsync(buf.AsMemory(0, n), dlCt);
                            read += n;
                            if (total is > 0) downloadProgress = Math.Clamp((float)read / total.Value, 0f, 1f);
                        }
                    }
                }

                File.Move(tmpPath, cachePath, overwrite: true);
                downloadProgress = 1f;
                return cachePath;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tmpPath);
                if (ct.IsCancellationRequested) throw;   // song changed — propagate
                loadError = "Audio download timed out.";
                return null;
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[BGM] download failed"); return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Ensures the server-prepared WAV for a YouTube url is downloaded into the local cache, WITHOUT
    /// starting playback. Used by the "waiting for members" prepare gate so every client can ready the
    /// cued song before anyone presses play. Returns true when the file is locally available.
    /// </summary>
    public async Task<bool> EnsureCachedAsync(string youtubeUrl, CancellationToken ct)
    {
        var path = await GetOrDownloadAsync(youtubeUrl, ct);
        return path != null;
    }

    // ── Title fetch (used by add-song modal) ──────────────────────────────────

    public static async Task<string> GetTitleAsync(string youtubeUrl)
    {
        if (!IsAllowedYoutubeUrl(youtubeUrl)) return "Unknown";
        try
        {
            var video = await Youtube.Videos.GetAsync(youtubeUrl);
            return string.IsNullOrWhiteSpace(video.Title) ? "Unknown" : video.Title;
        }
        catch { return "Unknown"; }
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    public void DeleteCacheFor(string youtubeUrl)
    {
        try
        {
            string? videoId = TryGetVideoId(youtubeUrl);
            if (videoId == null) return;
            string cacheBase = Path.Combine(cacheDir, videoId);
            foreach (string ext in AudioExts) { string p = cacheBase + ext; if (File.Exists(p)) File.Delete(p); }
        }
        catch { }
    }

    public long GetCacheSizeBytes()
    {
        try { return Directory.GetFiles(cacheDir).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    public void ClearCache()
    {
        try { foreach (var f in Directory.GetFiles(cacheDir)) File.Delete(f); }
        catch { }
    }
}
