using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RPFramework.Contracts;

namespace RPFramework.Services;

/// <summary>
/// Owns BGM playback for the player's single active room, independent of any window. Driven every
/// framework tick. The server is authoritative: it holds a "waiting for members" prepare gate and a
/// playback clock. Every client (controller and listener alike) downloads the cued song during the
/// gate, then plays in sync with the server clock. The controller additionally issues playback
/// commands and, when a track ends naturally, drives the room to the next (gated) song.
/// </summary>
public sealed class BgmCoordinator
{
    private readonly RpStateStore   _store;
    private readonly NetworkService _net;
    private readonly BgmService     _bgm;
    private readonly Func<float>    _volume;

    public string? ActiveRoom { get; private set; }

    // Playback-clock anchor. When the authoritative room state changes (Version bumps), we record the
    // live position it reported and the LOCAL monotonic time we saw it. We extrapolate forward using
    // Environment.TickCount64 only — never a cross-machine wall clock — so all that matters is relative
    // elapsed time, which ticks identically on every machine.
    private long   _clockVersion  = -1;
    private double _clockPos;
    private long   _clockAnchorMs;
    private bool   _clockPlaying;

    // Prepare-gate tracking — one cue at a time, keyed by the room Version of the cue.
    private long _prepareVersion   = -1;
    private volatile bool _prepareReady;
    private long _readySentVersion = -1;
    private CancellationTokenSource? _prepareCts;

    // Only re-seek when off by more than this, and never within this of the track end (the controller
    // advances instead). Both guard against the historical per-frame "seek past the end" reload loop.
    private const double DriftThreshold = 0.75;
    private const double EndGuard       = 0.5;

    public BgmCoordinator(RpStateStore store, NetworkService net, BgmService bgm, Func<float> volume)
    {
        _store  = store;
        _net    = net;
        _bgm    = bgm;
        _volume = volume;
    }

    public RoomStateDto? Room => _store.Room(ActiveRoom);

    /// <summary>Marks a room as the one being controlled/listened to and tells the server we're an active listener.</summary>
    public void SetActiveRoom(string code)
    {
        ActiveRoom = code;
        _ = _net.BgmSetActive(code);
    }

    /// <summary>Re-declares our active room after a (re)connect, since the server's per-connection map resets.</summary>
    public void ReassertActive()
    {
        if (ActiveRoom != null) _ = _net.BgmSetActive(ActiveRoom);
    }

    /// <summary>Leaves a room (any room). Stops playback if it was the active one.</summary>
    public void LeaveRoom(string code)
    {
        _ = _net.RoomLeave(code);
        if (code == ActiveRoom)
        {
            ActiveRoom = null;
            _ = _net.BgmSetActive("");
            _bgm.Stop();
            ResetPrepare();
        }
    }

    public void LeaveActive() { if (ActiveRoom != null) LeaveRoom(ActiveRoom); }

    public bool CanControl(string? localId)
    {
        var room = Room;
        if (room == null) return false;
        return room.OwnerPlayerId == localId
            || room.Members.FirstOrDefault(m => m.PlayerId == localId)?.Role is RoomRole.Owner or RoomRole.Admin;
    }

    // ── Control actions (from the UI) — issue intents; the gate + room state drive actual audio ──────

    public void PlayIndex(int i)
    {
        var room = Room;
        if (room == null || i < 0 || i >= room.Playlist.Count) return;
        // Don't play locally: ask the server to open a prepare gate. Everyone (including us) starts
        // together once ready, so we just request it here.
        _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Play, i, 0, 0, null));
    }

    public void TogglePlayPause()
    {
        var room = Room;
        if (room == null) return;
        if (room.IsPlaying)
            _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Pause, room.CurrentIndex, 0, 0, null));
        else if (!room.Preparing && room.CurrentIndex >= 0 && room.PositionSeconds > 0.01)
            // Truly paused mid-track: everyone already has the file, so just resume (no gate).
            _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Resume, room.CurrentIndex, 0, 0, null));
        else
            // Stopped / never started: open a fresh "waiting for members" gate.
            PlayIndex(Math.Max(0, room.CurrentIndex));
    }

    public void Next() { var r = Room; if (r != null && r.Playlist.Count > 0) PlayIndex(Math.Min(r.Playlist.Count - 1, Math.Max(0, r.CurrentIndex) + 1)); }
    public void Prev() { var r = Room; if (r != null && r.Playlist.Count > 0) PlayIndex(Math.Max(0, Math.Max(0, r.CurrentIndex) - 1)); }

    public void Stop()
    {
        var room = Room;
        if (room == null) return;
        _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Stop, 0, 0, 0, null));
    }

    public void SetLoop(LoopMode mode)
    {
        var room = Room;
        if (room == null) return;
        _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.LoopChanged, 0, 0, 0, mode));
    }

    // ── Per-frame sync (framework tick) ───────────────────────────────────────

    public void Tick(string? localId)
    {
        var room = Room;
        if (room == null) return;
        _bgm.SetVolume(_volume());
        _bgm.AutoAdvance = false; // advance is server-driven through the prepare gate, never local

        // Re-anchor the playback clock whenever the authoritative state changes.
        if (room.Version != _clockVersion)
        {
            _clockVersion  = room.Version;
            _clockPos      = room.PositionSeconds;
            _clockAnchorMs = Environment.TickCount64;
            _clockPlaying  = room.IsPlaying;
        }

        bool control = CanControl(localId);
        if (control) DriveAdvance(room);
        else         _bgm.PendingTrackEnded = false; // listeners never advance the room

        if (room.Preparing) { SyncPreparing(room); return; }
        ResetPrepare();
        SyncPlayback(room);
    }

    /// <summary>Live server playhead, extrapolated from the last anchor using the local monotonic clock.</summary>
    private double ClockPos()
        => _clockPlaying ? _clockPos + (Environment.TickCount64 - _clockAnchorMs) / 1000.0 : _clockPos;

    /// <summary>Controller only: when our local track ends naturally, advance the room (gated) per loop mode.</summary>
    private void DriveAdvance(RoomStateDto room)
    {
        if (!_bgm.PendingTrackEnded) return;
        _bgm.PendingTrackEnded = false;
        if (room.Preparing || room.Playlist.Count == 0) return;
        switch (room.Loop)
        {
            case LoopMode.Single:
                PlayIndex(Math.Max(0, room.CurrentIndex));
                break;
            case LoopMode.All:
                PlayIndex((Math.Max(0, room.CurrentIndex) + 1) % room.Playlist.Count);
                break;
            default:
                int next = Math.Max(0, room.CurrentIndex) + 1;
                if (next < room.Playlist.Count) PlayIndex(next);
                else _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Stop, 0, 0, 0, null));
                break;
        }
    }

    /// <summary>Gate phase: download the cued song WITHOUT playing, then report ready once. Audio stays silent.</summary>
    private void SyncPreparing(RoomStateDto room)
    {
        if (_bgm.IsPlaying || _bgm.IsPaused) _bgm.Stop();
        if (room.CurrentIndex < 0 || room.CurrentIndex >= room.Playlist.Count) return;

        if (_prepareVersion != room.Version)
        {
            // New cue: cancel any prior prepare and start downloading this song in the background.
            ResetPrepare();
            _prepareVersion = room.Version;
            _prepareReady   = false;
            var cts = new CancellationTokenSource();
            _prepareCts = cts;
            string url = room.Playlist[room.CurrentIndex].YoutubeUrl;
            _ = Task.Run(async () =>
            {
                try { if (await _bgm.EnsureCachedAsync(url, cts.Token) && !cts.IsCancellationRequested) _prepareReady = true; }
                catch { /* cancelled or failed — gate times out server-side */ }
            });
        }

        if (_prepareReady && _readySentVersion != room.Version)
        {
            _readySentVersion = room.Version;
            _ = _net.BgmReady(room.Code, room.CurrentIndex, room.Version);
        }
    }

    /// <summary>Steady phase: match the authoritative play/pause/stop state and the server playback clock.</summary>
    private void SyncPlayback(RoomStateDto room)
    {
        bool wantPlay = room.IsPlaying && room.CurrentIndex >= 0 && room.CurrentIndex < room.Playlist.Count;
        if (!wantPlay)
        {
            bool stopped = room.CurrentIndex < 0 || (!room.IsPlaying && room.PositionSeconds <= 0);
            if (stopped) { if (_bgm.IsPlaying || _bgm.IsPaused || _bgm.IsLoading) _bgm.Stop(); }
            else if (_bgm.IsPlaying) _bgm.PlayPause(); // pause to match the room
            return;
        }

        bool onSong = _bgm.CurrentRoomCode == room.Code && _bgm.CurrentSongIndex == room.CurrentIndex;
        if (!onSong)
        {
            if (_bgm.IsLoading) return;
            // Load the room's current song (gate missed / song changed / late joiner). PlayAt sets the
            // indices synchronously, so next frame onSong is true and we stop re-issuing.
            _bgm.PlayRoom(room.Code, room.Playlist.ToList(), room.CurrentIndex);
            return;
        }

        if (_bgm.IsLoading) return;
        if (_bgm.LoadError != null) return; // broken track — wait for the controller to advance

        if (_bgm.IsPaused) { _bgm.PlayPause(); return; } // resume to match the room

        double target = ClockPos();
        double len    = _bgm.TotalDurationSeconds;

        if (!_bgm.IsPlaying)
        {
            // Device idle but the room wants play and we're on the song: (re)start once, unless we're
            // basically at the end (the controller will advance us instead).
            if (len <= 0 || target < len - EndGuard)
                _bgm.PlayRoom(room.Code, room.Playlist.ToList(), room.CurrentIndex);
            return;
        }

        // Playing the right song: gently pull toward the server clock. The target advances at real time
        // just like local playback, so once aligned the drift stays under the threshold and we stop seeking.
        if (len > 0 && target < len - EndGuard && Math.Abs(_bgm.CurrentPositionSeconds - target) > DriftThreshold)
            _bgm.SeekTo(target);
    }

    private void ResetPrepare()
    {
        _prepareCts?.Cancel();
        _prepareCts       = null;
        _prepareVersion   = -1;
        _prepareReady     = false;
        _readySentVersion = -1;
    }
}
