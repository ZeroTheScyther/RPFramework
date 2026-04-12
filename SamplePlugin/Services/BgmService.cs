using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SamplePlugin.Models;
using YoutubeExplode;
using YoutubeExplode.Videos;

namespace SamplePlugin.Services;

/// <summary>
/// Handles audio playback for RPBGM.
/// yt-dlp.exe is auto-downloaded once into the cache dir and used for all
/// YouTube audio downloads. Audio files are cached locally; repeat plays are instant.
/// YoutubeExplode is kept only for lightweight title/metadata fetching.
/// </summary>
public class BgmService : IDisposable
{
    // Title-only metadata client (less likely to be blocked than stream extraction)
    private static readonly YoutubeClient Youtube = new();

    // Shared HttpClient for downloading yt-dlp itself
    private static readonly HttpClient Http = MakeHttp();
    private static HttpClient MakeHttp()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.Add("User-Agent", "RPFramework-DalamudPlugin/1.0");
        return c;
    }

    private readonly string cacheDir;
    private readonly string ytDlpExe;   // <cacheDir>/yt-dlp.exe

    private volatile bool ytDlpReady;   // true once yt-dlp.exe is confirmed present

    private WaveOutEvent?         outputDevice;
    private WaveStream?           audioStream;
    private VolumeSampleProvider? volumeProvider;

    private RpRoom? currentRoom;
    private int     currentSongIndex = -1;

    private volatile bool  isLoading;
    private volatile bool  pendingAdvance;
    private          bool  isMuted;
    private          string? loadError;
    private          float  downloadProgress;  // 0–1
    private          CancellationTokenSource? downloadCts;

    private readonly object playLock = new();

    // ── Public state ──────────────────────────────────────────────────────────

    public bool    IsPlaying        => outputDevice?.PlaybackState == PlaybackState.Playing;
    public bool    IsPaused         => outputDevice?.PlaybackState == PlaybackState.Paused;
    public bool    IsLoading        => isLoading;
    public bool    IsMuted          => isMuted;
    public string? LoadError        => loadError;
    public int     CurrentSongIndex => currentSongIndex;
    public RpRoom? CurrentRoom      => currentRoom;
    public float   DownloadProgress => downloadProgress;

    public BgmService(string cacheDir)
    {
        this.cacheDir = cacheDir;
        this.ytDlpExe = Path.Combine(cacheDir, "yt-dlp.exe");
        Directory.CreateDirectory(cacheDir);
        ytDlpReady = File.Exists(ytDlpExe);
    }

    // ── Called from Framework.Update (game thread) ────────────────────────────

    public void Update()
    {
        if (!pendingAdvance) return;
        pendingAdvance = false;
        AdvanceToNext();
    }

    // ── Playback control ─────────────────────────────────────────────────────

    public void PlayRoom(RpRoom room, int songIndex) { currentRoom = room; PlayAt(songIndex); }

    public void PlayPause()
    {
        if (isLoading) return;
        if (outputDevice == null)
        {
            if (currentRoom?.Playlist.Count > 0) PlayAt(Math.Max(0, currentRoom.CurrentIndex));
            return;
        }
        if (IsPlaying) outputDevice.Pause(); else outputDevice.Play();
    }

    public void Next()
    {
        if (currentRoom == null) return;
        int next = currentSongIndex + 1;
        if (next >= currentRoom.Playlist.Count)
        {
            if (currentRoom.Loop == LoopMode.All) next = 0; else return;
        }
        PlayAt(next);
    }

    public void Previous()
    {
        if (currentRoom == null) return;
        int prev = currentSongIndex - 1;
        if (prev < 0) prev = currentRoom.Loop == LoopMode.All ? currentRoom.Playlist.Count - 1 : 0;
        PlayAt(prev);
    }

    public void Stop()
    {
        StopPlayback();
        if (currentRoom != null) currentRoom.CurrentIndex = -1;
        currentSongIndex = -1;
    }

    public void SetVolume(float vol)
    {
        float v = Math.Clamp(vol, 0f, 1f);
        if (currentRoom != null) currentRoom.Volume = v;
        if (volumeProvider != null) volumeProvider.Volume = isMuted ? 0f : v;
    }

    public void ToggleMute()
    {
        isMuted = !isMuted;
        if (volumeProvider != null)
            volumeProvider.Volume = isMuted ? 0f : (currentRoom?.Volume ?? 1.0f);
    }

    /// <summary>Current playback position in seconds. Used by the owner to report position to the server.</summary>
    public double CurrentPositionSeconds
    {
        get { lock (playLock) { return audioStream?.CurrentTime.TotalSeconds ?? 0.0; } }
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
        if (currentRoom == null || currentRoom.Playlist.Count == 0) return;
        if (index < 0 || index >= currentRoom.Playlist.Count) return;

        currentSongIndex         = index;
        currentRoom.CurrentIndex = index;
        isLoading                = true;
        loadError                = null;
        downloadProgress         = 0f;
        StopPlayback();

        string url = currentRoom.Playlist[index].YoutubeUrl;
        Task.Run(async () => await LoadAndPlayAsync(url));
    }

    private async Task LoadAndPlayAsync(string youtubeUrl)
    {
        var cts = new CancellationTokenSource();
        downloadCts = cts;
        try
        {
            // 1. Make sure yt-dlp.exe is present (auto-download if missing)
            if (!ytDlpReady)
            {
                if (!await EnsureYtDlpAsync(cts.Token))
                {
                    loadError = "Could not obtain yt-dlp. Check your internet connection.";
                    return;
                }
            }
            if (cts.IsCancellationRequested) return;

            // 2. Download audio to cache (or load from cache)
            string? filePath = await GetOrDownloadAsync(youtubeUrl, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (filePath == null)
            {
                loadError = "Could not download audio. The video may be unavailable or region-locked.";
                return;
            }

            // 3. Open and play
            lock (playLock)
            {
                try
                {
                    var stream  = new MediaFoundationReader(filePath);
                    var volProv = new VolumeSampleProvider(stream.ToSampleProvider())
                        { Volume = isMuted ? 0f : (currentRoom?.Volume ?? 1.0f) };
                    var device  = new WaveOutEvent();
                    device.Init(volProv);
                    device.PlaybackStopped += OnPlaybackStopped;
                    device.Play();
                    audioStream    = stream;
                    volumeProvider = volProv;
                    outputDevice   = device;
                }
                catch (Exception ex) { loadError = $"Playback error: {ex.Message}"; }
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
        if (!isLoading) pendingAdvance = true;
    }

    private void AdvanceToNext()
    {
        if (currentRoom == null || currentRoom.Playlist.Count == 0) return;
        switch (currentRoom.Loop)
        {
            case LoopMode.Single: PlayAt(currentSongIndex); break;
            case LoopMode.All:    PlayAt((currentSongIndex + 1) % currentRoom.Playlist.Count); break;
            default:
                if (currentSongIndex + 1 < currentRoom.Playlist.Count)
                    PlayAt(currentSongIndex + 1);
                else
                {
                    currentSongIndex = -1;
                    if (currentRoom != null) currentRoom.CurrentIndex = -1;
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

    // ── yt-dlp bootstrap ──────────────────────────────────────────────────────

    /// <summary>
    /// Downloads yt-dlp.exe from the latest GitHub release into the cache dir.
    /// This is a one-time operation; subsequent calls return immediately.
    /// </summary>
    private async Task<bool> EnsureYtDlpAsync(CancellationToken ct)
    {
        if (File.Exists(ytDlpExe)) { ytDlpReady = true; return true; }

        loadError        = "First-time setup: fetching yt-dlp info...";
        downloadProgress = 0f;

        try
        {
            // Ask GitHub for the latest release metadata
            var json = await Http.GetStringAsync(
                "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest", ct);

            string? downloadUrl = null;
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() == "yt-dlp.exe")
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            if (downloadUrl == null) { loadError = "Could not find yt-dlp.exe in GitHub release."; return false; }

            loadError = "First-time setup: downloading yt-dlp.exe (~20 MB)...";
            var bytes = await Http.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(ytDlpExe, bytes, ct);

            ytDlpReady = true;
            loadError  = null;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            loadError = $"yt-dlp setup failed: {ex.Message}";
            return false;
        }
    }

    // ── Audio cache ───────────────────────────────────────────────────────────

    private static readonly string[] AudioExts = { ".m4a", ".webm", ".mp3", ".mp4", ".opus" };

    private async Task<string?> GetOrDownloadAsync(string youtubeUrl, CancellationToken ct)
    {
        try
        {
            string videoId   = VideoId.Parse(youtubeUrl).Value;
            string cacheBase = Path.Combine(cacheDir, videoId);

            // Return cached file if present
            foreach (string ext in AudioExts)
            {
                string cached = cacheBase + ext;
                if (File.Exists(cached)) { downloadProgress = 1f; return cached; }
            }

            // Download with yt-dlp; output template keeps video ID as filename
            string outTemplate = cacheBase + ".%(ext)s";
            var psi = new ProcessStartInfo
            {
                FileName               = ytDlpExe,
                // Prefer m4a (plays well with MediaFoundationReader); fall back to best audio
                Arguments              = $"--no-playlist -f \"bestaudio[ext=m4a]/bestaudio[ext=webm]/bestaudio\" --no-part -o \"{outTemplate}\" -- \"{videoId}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // Parse yt-dlp's "[download]  42.3% of …" progress lines
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                int pctIdx = line.IndexOf('%');
                if (pctIdx <= 0) return;
                int start = line.LastIndexOf(' ', pctIdx - 1) + 1;
                if (float.TryParse(line[start..pctIdx],
                        NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                    downloadProgress = pct / 100f;
            };

            proc.Start();
            proc.BeginOutputReadLine();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0) return null;

            foreach (string ext in AudioExts)
            {
                string downloaded = cacheBase + ext;
                if (File.Exists(downloaded)) { downloadProgress = 1f; return downloaded; }
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ── Title fetch (used by add-song modal) ──────────────────────────────────

    public static async Task<string> GetTitleAsync(string youtubeUrl)
    {
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
            string cacheBase = Path.Combine(cacheDir, VideoId.Parse(youtubeUrl).Value);
            foreach (string ext in AudioExts) { string p = cacheBase + ext; if (File.Exists(p)) File.Delete(p); }
        }
        catch { }
    }

    public long GetCacheSizeBytes()
    {
        try { return Directory.GetFiles(cacheDir).Where(f => !f.EndsWith("yt-dlp.exe")).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    public void ClearCache()
    {
        try { foreach (var f in Directory.GetFiles(cacheDir).Where(f => !f.EndsWith("yt-dlp.exe"))) File.Delete(f); }
        catch { }
    }
}
