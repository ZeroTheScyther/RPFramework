using System;
using System.Linq;
using RPFramework.Contracts;

namespace RPFramework.Services;

/// <summary>
/// Owns BGM playback for the player's single active room, independent of any window. Driven every
/// framework tick: the controller drives the local audio engine and reports song advances /
/// end-of-playlist to the server; listeners follow the authoritative room state. The BGM windows
/// are pure UI that call the control methods here — closing a window never affects playback.
/// </summary>
public sealed class BgmCoordinator
{
    private readonly RpStateStore   _store;
    private readonly NetworkService _net;
    private readonly BgmService     _bgm;
    private readonly Func<float>    _volume;

    public string? ActiveRoom { get; private set; }

    public BgmCoordinator(RpStateStore store, NetworkService net, BgmService bgm, Func<float> volume)
    {
        _store  = store;
        _net    = net;
        _bgm    = bgm;
        _volume = volume;
    }

    public RoomStateDto? Room => _store.Room(ActiveRoom);

    /// <summary>Marks a room as the one being controlled/listened to. Membership persistence is server-side.</summary>
    public void SetActiveRoom(string code) => ActiveRoom = code;

    /// <summary>Leaves a room (any room). Stops playback if it was the active one.</summary>
    public void LeaveRoom(string code)
    {
        _ = _net.RoomLeave(code);
        if (code == ActiveRoom) { ActiveRoom = null; _bgm.Stop(); }
    }

    public void LeaveActive() { if (ActiveRoom != null) LeaveRoom(ActiveRoom); }

    public bool CanControl(string? localId)
    {
        var room = Room;
        if (room == null) return false;
        return room.OwnerPlayerId == localId
            || room.Members.FirstOrDefault(m => m.PlayerId == localId)?.Role is RoomRole.Owner or RoomRole.Admin;
    }

    // ── Control actions (called from the UI) ──────────────────────────────────

    public void PlayIndex(int i)
    {
        var room = Room;
        if (room == null || i < 0 || i >= room.Playlist.Count) return;
        _bgm.PlayRoom(room.Code, room.Playlist.ToList(), i);
        _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Play, i, 0, 0, null));
    }

    public void TogglePlayPause()
    {
        var room = Room;
        if (room == null) return;
        int idx = Math.Max(0, room.CurrentIndex);
        if (_bgm.IsPlaying)
        { _bgm.PlayPause(); _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Pause, _bgm.CurrentSongIndex, _bgm.CurrentPositionSeconds, 0, null)); }
        else if (_bgm.CurrentRoomCode == room.Code && _bgm.CurrentSongIndex >= 0)
        { _bgm.PlayPause(); _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Resume, _bgm.CurrentSongIndex, _bgm.CurrentPositionSeconds, 0, null)); }
        else PlayIndex(idx);
    }

    public void Next() { var r = Room; if (r != null) PlayIndex(Math.Min(r.Playlist.Count - 1, Math.Max(0, r.CurrentIndex) + 1)); }
    public void Prev() { var r = Room; if (r != null) PlayIndex(Math.Max(0, Math.Max(0, r.CurrentIndex) - 1)); }

    public void Stop()
    {
        var room = Room;
        if (room == null) return;
        _bgm.Stop();
        _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Stop, 0, 0, 0, null));
    }

    public void SetLoop(LoopMode mode)
    {
        var room = Room;
        if (room == null) return;
        _bgm.SetLoop(mode);
        _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.LoopChanged, 0, 0, 0, mode));
    }

    // ── Per-frame sync (called from the framework tick) ───────────────────────

    public void Tick(string? localId)
    {
        var room = Room;
        if (room == null) return;
        _bgm.SetVolume(_volume());
        _bgm.SetLoop(room.Loop);
        bool control = CanControl(localId);
        _bgm.AutoAdvance = control;           // only the controller advances; listeners follow the room
        if (control) Report(room); else Follow(room);
    }

    /// <summary>Controller: mirror local auto-advance + end-of-playlist to the server.</summary>
    private void Report(RoomStateDto room)
    {
        if (_bgm.PendingStopBroadcast)
        {
            _bgm.PendingStopBroadcast = false;
            _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Stop, 0, 0, 0, null));
        }
        else if (_bgm.IsPlaying && _bgm.CurrentSongIndex >= 0 && _bgm.CurrentSongIndex != room.CurrentIndex)
        {
            _ = _net.PlaybackCommand(room.Code, new PlaybackCommandDto(PlaybackCommandType.Play, _bgm.CurrentSongIndex, 0, 0, null));
        }
    }

    /// <summary>
    /// Listener: match the authoritative room playback. No per-frame seeking — that previously
    /// chased a latency target past the end of the track, ending the song instantly and looping a
    /// reload (the "Play/Pause flicker, no audio" bug). Listeners just load the current song, pause
    /// / resume with the room, and reload if they fall idle while the room is still playing it.
    /// </summary>
    private void Follow(RoomStateDto room)
    {
        if (room.IsPlaying && room.CurrentIndex >= 0 && room.CurrentIndex < room.Playlist.Count)
        {
            if (_bgm.IsLoading) return;
            bool wrongSong = _bgm.CurrentRoomCode != room.Code || _bgm.CurrentSongIndex != room.CurrentIndex;
            if (wrongSong)
                _bgm.PlayRoom(room.Code, room.Playlist.ToList(), room.CurrentIndex);
            else if (_bgm.IsPaused)
                _bgm.PlayPause(); // resume to match the room
            else if (!_bgm.IsPlaying && _bgm.LoadError == null)
                _bgm.PlayRoom(room.Code, room.Playlist.ToList(), room.CurrentIndex); // finished early; room still on this song
        }
        else if (!room.IsPlaying && _bgm.IsPlaying)
        {
            _bgm.PlayPause(); // pause to match the room
        }
    }
}
