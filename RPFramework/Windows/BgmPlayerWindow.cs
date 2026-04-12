using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models;
using RPFramework.Models.Net;
using RPFramework.Services;

namespace RPFramework.Windows;

public class BgmPlayerWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private BgmService? bgmService;
    private RpRoom?     room;

    // Add-song modal
    private bool   pendingAddSong;
    private bool   addSongOpen  = true;
    private string addSongUrl   = string.Empty;
    private bool   addSongLoading;

    // Network: live member list maintained by NetworkService events
    private readonly List<RoomMemberDto> members = new();

    // Whether local player is owner/admin in this room
    private bool isAuthority;

    public BgmPlayerWindow(Plugin plugin)
        : base("RP BGM Player##RPFramework.BGMPlayer")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 400),
            MaximumSize = new Vector2(800, 1000),
        };
        Size          = new Vector2(420, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen        = false;

        // Subscribe to network events
        plugin.Network.BgmRoomStateReceived  += OnRoomState;
        plugin.Network.BgmMemberJoined       += OnMemberJoined;
        plugin.Network.BgmMemberLeft         += OnMemberLeft;
        plugin.Network.BgmMemberRoleChanged  += OnMemberRoleChanged;
        plugin.Network.BgmPlaybackCommand    += OnPlaybackCommand;
        plugin.Network.BgmSongAdded          += OnSongAdded;
        plugin.Network.BgmSongRemoved        += OnSongRemoved;
        plugin.Network.Connected             += OnNetworkConnected;
        plugin.Network.Reconnected           += OnNetworkReconnected;
    }

    public void OpenRoom(RpRoom rpRoom, BgmService svc)
    {
        room       = rpRoom;
        bgmService = svc;
        WindowName = $"{rpRoom.Name}##RPFramework.BGMPlayer";
        members.Clear();
        RefreshAuthority(); // correct button state immediately for local-only mode

        // Join the room on the relay — Identify already ran at connect time, no need to re-check LocalPlayerId
        if (plugin.Network.IsConnected)
            Task.Run(() => plugin.Network.BgmJoinAsync(rpRoom.Code));
    }

    public void Dispose()
    {
        plugin.Network.BgmRoomStateReceived -= OnRoomState;
        plugin.Network.BgmMemberJoined      -= OnMemberJoined;
        plugin.Network.BgmMemberLeft        -= OnMemberLeft;
        plugin.Network.BgmMemberRoleChanged -= OnMemberRoleChanged;
        plugin.Network.BgmPlaybackCommand   -= OnPlaybackCommand;
        plugin.Network.BgmSongAdded         -= OnSongAdded;
        plugin.Network.BgmSongRemoved       -= OnSongRemoved;
        plugin.Network.Connected            -= OnNetworkConnected;
        plugin.Network.Reconnected          -= OnNetworkReconnected;
    }

    // ── Network handlers (called on framework thread) ─────────────────────────

    private void OnRoomState(RoomStateDto state)
    {
        if (room == null || state.Code != room.Code) return;
        members.Clear();
        members.AddRange(state.Members);
        RefreshAuthority();

        if (!isAuthority)
        {
            // Non-authority: merge server playlist by ID to avoid wiping songs that
            // arrived via OnSongAdded before this state packet completed.
            var serverIds = new System.Collections.Generic.HashSet<Guid>(
                state.Playlist.ConvertAll(d => d.Id));
            room.Playlist.RemoveAll(s => !serverIds.Contains(s.Id));
            for (int i = 0; i < state.Playlist.Count; i++)
            {
                var dto = state.Playlist[i];
                if (!room.Playlist.Exists(s => s.Id == dto.Id))
                    room.Playlist.Insert(
                        Math.Min(i, room.Playlist.Count),
                        new RpSong { Id = dto.Id, Title = dto.Title, YoutubeUrl = dto.YoutubeUrl });
            }
            plugin.Configuration.Save();
        }
        else if (state.Playlist.Count < room.Playlist.Count)
        {
            // Authority reconnecting to a server that lost its state (restart / session expiry).
            // Re-upload any songs the server is missing.
            var code    = room.Code;
            var missing = room.Playlist.GetRange(
                state.Playlist.Count, room.Playlist.Count - state.Playlist.Count);
            Task.Run(async () =>
            {
                foreach (var s in missing)
                    await plugin.Network.BgmSendAddSong(code, new RpSongDto(s.Id, s.Title, s.YoutubeUrl));
            });
        }

        // Sync playback: if not authority, apply the owner's state
        if (!isAuthority && bgmService != null)
            ApplyPlaybackState(state.CurrentIndex, state.IsPlaying, state.PositionSeconds, state.ServerTimestamp);
    }

    private void OnMemberJoined(string code, RoomMemberDto member)
    {
        if (room?.Code != code) return;
        members.RemoveAll(m => m.PlayerId == member.PlayerId);
        members.Add(member);
        RefreshAuthority();
    }

    private void OnMemberLeft(string code, string playerId)
    {
        if (room?.Code != code) return;
        members.RemoveAll(m => m.PlayerId == playerId);
        RefreshAuthority();
    }

    private void OnMemberRoleChanged(string code, RoomMemberDto updated)
    {
        if (room?.Code != code) return;
        var idx = members.FindIndex(m => m.PlayerId == updated.PlayerId);
        if (idx >= 0) members[idx] = updated;
        RefreshAuthority();
    }

    private void OnSongAdded(string code, RpSongDto dto, int idx)
    {
        if (room?.Code != code) return;
        // Server sends OnBgmSongAdded to OthersInGroup, so authority normally won't receive this.
        // Dedup by Id handles any edge case where it arrives anyway.
        if (room.Playlist.Exists(s => s.Id == dto.Id)) return;
        var song = new RpSong { Id = dto.Id, Title = dto.Title, YoutubeUrl = dto.YoutubeUrl };
        if (idx >= 0 && idx <= room.Playlist.Count)
            room.Playlist.Insert(idx, song);
        else
            room.Playlist.Add(song);
        plugin.Configuration.Save();
    }

    private void OnSongRemoved(string code, Guid songId)
    {
        if (room?.Code != code) return;
        room.Playlist.RemoveAll(s => s.Id == songId);
        plugin.Configuration.Save();
    }

    private void OnPlaybackCommand(string code, PlaybackCommandDto cmd)
    {
        if (room?.Code != code || bgmService == null || isAuthority) return;

        switch (cmd.CommandType)
        {
            case PlaybackCommandType.Play:
            case PlaybackCommandType.Resume:
                ApplyPlaybackState(cmd.SongIndex, true, cmd.PositionSeconds, cmd.ServerTimestamp);
                break;
            case PlaybackCommandType.Pause:
                bgmService.SeekTo(cmd.PositionSeconds);
                if (bgmService.IsPlaying)
                    bgmService.PlayPause(); // pause
                break;
            case PlaybackCommandType.Stop:
                bgmService.Stop();
                break;
            case PlaybackCommandType.Seek:
                bgmService.SeekTo(AdjustedPosition(cmd.PositionSeconds, cmd.ServerTimestamp));
                break;
            case PlaybackCommandType.LoopChanged when cmd.Loop.HasValue:
                if (room != null)
                    room.Loop = cmd.Loop.Value switch
                    {
                        NetLoopMode.Single => LoopMode.Single,
                        NetLoopMode.All    => LoopMode.All,
                        _                  => LoopMode.None,
                    };
                break;
        }
    }

    private void ApplyPlaybackState(int songIndex, bool isPlaying, double positionSeconds, long serverTimestamp)
    {
        if (bgmService == null || room == null) return;
        double adjusted = AdjustedPosition(positionSeconds, serverTimestamp);

        bool wrongSong = songIndex != bgmService.CurrentSongIndex || bgmService.CurrentRoom != room;

        if (isPlaying)
        {
            if (songIndex >= 0 && songIndex < room.Playlist.Count && wrongSong)
                bgmService.PlayRoom(room, songIndex); // load correct song; seek handled post-load
            else
            {
                bgmService.SeekTo(adjusted);          // snap to owner's position unconditionally
                if (!bgmService.IsPlaying) bgmService.PlayPause();
            }
        }
        else
        {
            if (bgmService.IsPlaying) bgmService.PlayPause(); // force pause/stop
            bgmService.SeekTo(adjusted);
        }
    }

    private static double AdjustedPosition(double positionSecs, long serverTimestamp)
    {
        long elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - serverTimestamp;
        return positionSecs + Math.Max(0, elapsedMs / 1000.0);
    }

    private void OnNetworkConnected()
    {
        if (room == null) return;
        // Identify already ran during ConnectAsync — no need to re-check LocalPlayerId here
        Task.Run(() => plugin.Network.BgmJoinAsync(room.Code));
    }

    private void OnNetworkReconnected()
    {
        // NetworkService already re-sent BgmJoin during reconnect — do NOT call BgmJoinAsync again
        // (that would trigger a second OnBgmRoomState with 0 songs, clearing the playlist).
        // Just reset authority until the incoming OnBgmRoomState re-establishes it.
        isAuthority = false;
    }

    private void RefreshAuthority()
    {
        string? localId = plugin.LocalPlayerId;
        if (localId == null) { isAuthority = true; return; } // not logged in → local only

        // No server connection → act as local authority (no one to sync with)
        if (!plugin.Network.IsConnected) { isAuthority = true; return; }

        // Connected: only grant authority if the server has explicitly said so
        var self = members.Find(m => m.PlayerId == localId);
        isAuthority = self?.Role is RoomRole.Owner or RoomRole.Admin;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Draw
    // ═════════════════════════════════════════════════════════════════════════

    public override void Draw()
    {
        if (room == null || bgmService == null)
        {
            ImGui.TextDisabled("No room selected.");
            return;
        }

        if (pendingAddSong)
        {
            addSongOpen    = true;
            addSongLoading = false;
            addSongUrl     = string.Empty;
            ImGui.OpenPopup("##bgm_addsong");
            pendingAddSong = false;
        }

        // Code row
        ImGui.TextDisabled("Code:");
        ImGui.SameLine();
        ImGui.TextUnformatted(room.Code);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##bgmplayercode"))
            ImGui.SetClipboardText(room.Code);

        // Connection status indicator
        if (plugin.Network.IsConnected)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 0.85f, 0.3f, 1f), "●");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Connected to relay");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "●");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Not connected — click to connect");
            if (ImGui.IsItemClicked())
            {
                string  url  = plugin.Configuration.ServerUrl;
                string? id   = plugin.LocalPlayerId;
                string  name = plugin.LocalDisplayName;
                if (id != null)
                    Task.Run(() => plugin.Network.ConnectAsync(url, id, name));
            }
        }

        ImGui.Separator();
        DrawControls();
        ImGui.Separator();
        DrawTabs();

        DrawAddSongModal();
    }

    // ── Transport controls ────────────────────────────────────────────────────

    private void DrawControls()
    {
        if (bgmService == null || room == null) return;

        bool loading = bgmService.IsLoading;
        bool playing = bgmService.IsPlaying && bgmService.CurrentRoom == room;
        bool muted   = bgmService.IsMuted;

        // Members who are not owner/admin cannot issue transport commands
        if (!isAuthority) ImGui.BeginDisabled();

        // Mute (always available, even to members — it's client-side)
        if (!isAuthority) ImGui.EndDisabled();
        if (ImGui.Button(muted ? "[M]##bgmmute" : "[ ]##bgmmute"))
            bgmService.ToggleMute();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(muted ? "Unmute" : "Mute");
        ImGui.SameLine();
        if (!isAuthority) ImGui.BeginDisabled();

        // Previous
        if (ImGui.Button("|<##bgmprev")) { bgmService.Previous(); SyncPlay(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Previous");
        ImGui.SameLine();

        // Play / Pause
        string ppLabel = loading
            ? $"{(int)(bgmService.DownloadProgress * 100)}%##bgmpp"
            : playing ? "||##bgmpp" : " >##bgmpp";
        if (loading) ImGui.BeginDisabled();
        if (ImGui.Button(ppLabel, new Vector2(38 * ImGuiHelpers.GlobalScale, 0)))
        {
            bgmService.PlayPause();
            if (!loading) SyncPlayPause(playing);
        }
        if (loading) ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(loading ? "Downloading..." : playing ? "Pause" : "Play");
        ImGui.SameLine();

        // Next
        if (ImGui.Button(">|##bgmnext")) { bgmService.Next(); SyncPlay(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Next");
        ImGui.SameLine();

        // Loop
        string loopLbl = room.Loop switch
        {
            LoopMode.Single => "[1]##bgmloop",
            LoopMode.All    => "[*]##bgmloop",
            _               => "[ ]##bgmloop",
        };
        if (ImGui.Button(loopLbl))
        {
            room.Loop = room.Loop switch
            {
                LoopMode.None   => LoopMode.Single,
                LoopMode.Single => LoopMode.All,
                _               => LoopMode.None,
            };
            plugin.Configuration.Save();
            SyncLoop();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Loop: {room.Loop}  (click to cycle)");
        ImGui.SameLine();

        // Add song
        if (ImGui.Button("+##bgmadd")) pendingAddSong = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add song");

        if (!isAuthority) ImGui.EndDisabled();

        // Load error
        if (bgmService.LoadError is { } err)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), err);

        // Volume slider (always client-side)
        float vol = room.Volume * 100f;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##bgmvol", ref vol, 0f, 100f, "%.0f%%"))
        {
            room.Volume = vol / 100f;
            bgmService.SetVolume(room.Volume);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Volume (local only)");
    }

    // ── Sync helpers: only fires if connected and authority ───────────────────

    private void SyncPlay()
    {
        if (!isAuthority || !plugin.Network.IsConnected || room == null || bgmService == null) return;
        Task.Run(() => plugin.Network.BgmSyncPlay(
            room.Code, bgmService.CurrentSongIndex, bgmService.CurrentPositionSeconds));
    }

    private void SyncPlayPause(bool wasPLaying)
    {
        if (!isAuthority || !plugin.Network.IsConnected || room == null || bgmService == null) return;
        double pos = bgmService.CurrentPositionSeconds;
        if (wasPLaying)
            Task.Run(() => plugin.Network.BgmSyncPause(room.Code, pos));
        else
            Task.Run(() => plugin.Network.BgmSyncResume(room.Code, pos));
    }

    private void SyncLoop()
    {
        if (!isAuthority || !plugin.Network.IsConnected || room == null) return;
        NetLoopMode netLoop = room.Loop switch
        {
            LoopMode.Single => NetLoopMode.Single,
            LoopMode.All    => NetLoopMode.All,
            _               => NetLoopMode.None,
        };
        Task.Run(() => plugin.Network.BgmSyncLoopMode(room.Code, netLoop));
    }

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private void DrawTabs()
    {
        using var tabBar = ImRaii.TabBar("##bgmtabs");
        if (!tabBar) return;

        using (var t = ImRaii.TabItem("Playlist##bgmpl"))
            if (t) DrawPlaylistTab();

        using (var t = ImRaii.TabItem("Members##bgmmembers"))
            if (t) DrawMembersTab();
    }

    private void DrawPlaylistTab()
    {
        if (room == null || bgmService == null) return;

        var  playlist      = room.Playlist;
        bool isCurrentRoom = bgmService.CurrentRoom == room;

        using var child = ImRaii.Child("##bgmplaylist", new Vector2(-1, -1));
        if (!child) return;

        for (int i = 0; i < playlist.Count; i++)
        {
            var  song      = playlist[i];
            bool isCurrent = isCurrentRoom && bgmService.CurrentSongIndex == i;

            ImGui.PushID($"##bgms{song.Id}");

            if (isCurrent)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.85f, 0.3f, 1f));

            string label = $"{(isCurrent ? "> " : "  ")}{i + 1}. {song.Title}";
            bool   sel   = ImGui.Selectable(label, isCurrent, ImGuiSelectableFlags.AllowDoubleClick);

            if (isCurrent) ImGui.PopStyleColor();

            if (sel && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && isAuthority)
            {
                bgmService.PlayRoom(room!, i);
                SyncPlay();
            }

            // Right-click → remove (authority only)
            if (ImGui.BeginPopupContextItem("##bgmsongctx"))
            {
                if (!isAuthority) ImGui.BeginDisabled();
                if (ImGui.MenuItem("Remove"))
                {
                    if (isCurrentRoom && bgmService.CurrentSongIndex == i) bgmService.Stop();
                    Guid removedId = song.Id;
                    playlist.RemoveAt(i);
                    plugin.Configuration.Save();
                    if (plugin.Network.IsConnected && room != null)
                        Task.Run(() => plugin.Network.BgmSendRemoveSong(room.Code, removedId));
                    ImGui.CloseCurrentPopup();
                }
                if (!isAuthority) ImGui.EndDisabled();
                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        if (playlist.Count == 0)
            ImGui.TextDisabled("No songs. Click + to add one.");
    }

    private void DrawMembersTab()
    {
        string? localId = plugin.LocalPlayerId;

        if (members.Count == 0)
        {
            string localName = plugin.LocalDisplayName;
            ImGui.TextUnformatted($"{localName} (Owner — local only)");
            ImGui.Spacing();
            ImGui.TextDisabled("No other members connected.");
            return;
        }

        using var child = ImRaii.Child("##bgmmembers", new Vector2(-1, -1));
        if (!child) return;

        foreach (var m in members)
        {
            bool isSelf = m.PlayerId == localId;
            string roleTag = m.Role switch
            {
                RoomRole.Owner => " [Owner]",
                RoomRole.Admin => " [Admin]",
                _              => string.Empty,
            };

            if (isSelf) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.85f, 1f, 1f));
            ImGui.TextUnformatted($"{m.DisplayName}{roleTag}{(isSelf ? " (you)" : "")}");
            if (isSelf) ImGui.PopStyleColor();

            // Owner can promote/demote via right-click
            if (isAuthority && m.Role != RoomRole.Owner && !isSelf
                && ImGui.BeginPopupContextItem($"##bgmmc{m.PlayerId}"))
            {
                if (m.Role == RoomRole.Admin && ImGui.MenuItem("Demote to Member"))
                {
                    if (room != null) Task.Run(() =>
                        plugin.Network.BgmPromoteMember(room.Code, m.PlayerId, RoomRole.Member));
                    ImGui.CloseCurrentPopup();
                }
                if (m.Role == RoomRole.Member && ImGui.MenuItem("Promote to Admin"))
                {
                    if (room != null) Task.Run(() =>
                        plugin.Network.BgmPromoteMember(room.Code, m.PlayerId, RoomRole.Admin));
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }
    }

    // ── Add song modal ────────────────────────────────────────────────────────

    private void DrawAddSongModal()
    {
        if (room == null) return;

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        float w1 = 400 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSizeConstraints(new Vector2(w1, 0), new Vector2(w1, float.MaxValue));

        if (!ImGui.BeginPopupModal("##bgm_addsong", ref addSongOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        ImGui.TextUnformatted("Add Song");
        ImGui.Separator();
        ImGui.TextUnformatted("YouTube URL:");
        ImGui.SetNextItemWidth(-1);
        bool enter = ImGui.InputText("##bgmsongurl", ref addSongUrl, 512,
            ImGuiInputTextFlags.EnterReturnsTrue);

        bool canAdd = !string.IsNullOrWhiteSpace(addSongUrl) && !addSongLoading;

        if (!canAdd) ImGui.BeginDisabled();
        if ((ImGui.Button(addSongLoading ? "Adding...##bgmaddok" : "Add##bgmaddok",
                new Vector2(96, 0)) || enter) && canAdd)
        {
            string url = addSongUrl.Trim();
            var song = new RpSong { Title = "Loading...", YoutubeUrl = url };
            room.Playlist.Add(song);
            plugin.Configuration.Save();
            addSongLoading = true;

            Task.Run(async () =>
            {
                string title = await BgmService.GetTitleAsync(url);
                song.Title = title;
                plugin.Configuration.Save();
                addSongLoading = false;

                // Broadcast song addition to room members
                if (plugin.Network.IsConnected && room != null)
                    await plugin.Network.BgmSendAddSong(room.Code,
                        new RpSongDto(song.Id, song.Title, song.YoutubeUrl));
            });

            ImGui.CloseCurrentPopup();
        }
        if (!canAdd) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel##bgmaddcancel", new Vector2(96, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }
}
