using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using RPFramework.Models.Net;

namespace RPFramework.Services;

/// <summary>
/// Manages the SignalR connection to the RPFramework relay server.
/// All server→client callbacks are marshalled to the Dalamud framework thread
/// via Plugin.Framework.RunOnFrameworkThread before firing events.
/// </summary>
public class NetworkService : IDisposable
{
    private HubConnection? _conn;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // Track which BGM rooms we're in so we can re-join on reconnect
    private readonly HashSet<string>     _activeRooms = new();
    private readonly HashSet<Guid>       _activeBags  = new();
    private          string              _playerId    = string.Empty;
    private          string              _displayName = string.Empty;

    public HubConnectionState State => _conn?.State ?? HubConnectionState.Disconnected;
    public bool IsConnected => State == HubConnectionState.Connected;

    // ── Lifecycle events ─────────────────────────────────────────────────────
    /// <summary>Fired (on framework thread) when an initial connection is established.</summary>
    public event Action? Connected;

    /// <summary>Fired (on framework thread) after an automatic reconnect completes. Rooms are already re-joined by NetworkService — subscribers must NOT call BgmJoinAsync again.</summary>
    public event Action? Reconnected;

    // ── BGM events ────────────────────────────────────────────────────────────
    public event Action<RoomStateDto>?              BgmRoomStateReceived;
    public event Action<string, RoomMemberDto>?     BgmMemberJoined;      // code, member
    public event Action<string, string>?            BgmMemberLeft;        // code, playerId
    public event Action<string, RoomMemberDto>?     BgmMemberRoleChanged; // code, member
    public event Action<string, PlaybackCommandDto>? BgmPlaybackCommand;  // code, cmd
    public event Action<string, RpSongDto, int>?    BgmSongAdded;         // code, song, idx
    public event Action<string, Guid>?              BgmSongRemoved;       // code, songId

    // ── Trade events ──────────────────────────────────────────────────────────
    public event Action<TradeOfferDto>?  TradeOfferReceived;
    public event Action<Guid, bool, Guid>? TradeAccepted;   // offerId, isCopy, itemId
    public event Action<Guid>?           TradeRejected;
    public event Action<Guid, RpItemDto, bool>? TradeItemReceived; // offerId, item, isCopy

    // ── Shared bag events ─────────────────────────────────────────────────────
    public event Action<SharedBagDto, string, string>? BagShareInviteReceived; // bag, fromId, fromName
    public event Action<SharedBagDto>?                 BagStateReceived;
    public event Action<BagOperationDto, long>?        BagOperationApplied;    // op, newVersion
    public event Action<Guid>?                         BagDissolved;
    public event Action<Guid, string>?                 BagParticipantLeft;     // bagId, playerId
    public event Action<Guid, string, string>?         BagParticipantJoined;   // bagId, playerId, displayName
    public event Action<Guid, string>?                 BagParticipantDeclined; // bagId, playerId

    // ── Generic error ─────────────────────────────────────────────────────────
    public event Action<string, string>? ErrorReceived; // feature, message

    // ═════════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═════════════════════════════════════════════════════════════════════════

    public async Task ConnectAsync(string serverUrl, string playerId, string displayName)
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_conn != null) await _conn.DisposeAsync();

            _playerId    = playerId;
            _displayName = displayName;

            _conn = new HubConnectionBuilder()
                .WithUrl(serverUrl.TrimEnd('/') + "/rphub")
                .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) })
                .Build();

            RegisterHandlers();

            _conn.Reconnecting  += _ => { Plugin.Log.Info("[Net] Reconnecting..."); return Task.CompletedTask; };
            _conn.Reconnected   += async _ =>
            {
                Plugin.Log.Info("[Net] Reconnected — re-identifying and re-joining rooms.");
                await SafeInvoke("Identify", _playerId, _displayName);
                foreach (var code in _activeRooms) await SafeInvoke("BgmJoin", code);
                foreach (var id   in _activeBags)  await SafeInvoke("BagShareAccept", id);
                // Fire Reconnected (NOT Connected) — rooms already re-joined, subscribers must not call BgmJoinAsync again
                await Plugin.Framework.RunOnFrameworkThread(() => Reconnected?.Invoke());
            };
            _conn.Closed += ex =>
            {
                if (ex != null) Plugin.Log.Warning(ex, "[Net] Connection closed with error.");
                return Task.CompletedTask;
            };

            await _conn.StartAsync();
            await SafeInvoke("Identify", _playerId, _displayName);
            await Plugin.Framework.RunOnFrameworkThread(() => Connected?.Invoke());
        }
        finally { _connectLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        if (_conn == null) return;
        await _conn.StopAsync();
        await _conn.DisposeAsync();
        _conn = null;
        _activeRooms.Clear();
        _activeBags.Clear();
    }

    public void Dispose()
    {
        _conn?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _connectLock.Dispose();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Handler registration
    // ═════════════════════════════════════════════════════════════════════════

    private void RegisterHandlers()
    {
        if (_conn == null) return;

        // BGM
        _conn.On<RoomStateDto>("OnBgmRoomState",
            s => Fire(() => BgmRoomStateReceived?.Invoke(s)));

        _conn.On<string, RoomMemberDto>("OnBgmMemberJoined",
            (code, m) => Fire(() => BgmMemberJoined?.Invoke(code, m)));

        _conn.On<string, string>("OnBgmMemberLeft",
            (code, pid) => Fire(() => BgmMemberLeft?.Invoke(code, pid)));

        _conn.On<string, RoomMemberDto>("OnBgmMemberRoleChanged",
            (code, m) => Fire(() => BgmMemberRoleChanged?.Invoke(code, m)));

        _conn.On<string, PlaybackCommandDto>("OnBgmPlaybackCommand",
            (code, cmd) => Fire(() => BgmPlaybackCommand?.Invoke(code, cmd)));

        _conn.On<string, RpSongDto, int>("OnBgmSongAdded",
            (code, song, idx) => Fire(() => BgmSongAdded?.Invoke(code, song, idx)));

        _conn.On<string, Guid>("OnBgmSongRemoved",
            (code, id) => Fire(() => BgmSongRemoved?.Invoke(code, id)));

        // Trading
        _conn.On<TradeOfferDto>("OnTradeOfferReceived",
            o => Fire(() => TradeOfferReceived?.Invoke(o)));

        _conn.On<Guid, bool, Guid>("OnTradeAccepted",
            (id, isCopy, itemId) => Fire(() => TradeAccepted?.Invoke(id, isCopy, itemId)));

        _conn.On<Guid>("OnTradeRejected",
            id => Fire(() => TradeRejected?.Invoke(id)));

        _conn.On<Guid, RpItemDto, bool>("OnTradeItemReceived",
            (id, item, isCopy) => Fire(() => TradeItemReceived?.Invoke(id, item, isCopy)));

        // Shared bags
        _conn.On<SharedBagDto, string, string>("OnBagShareInvite",
            (bag, fromId, fromName) => Fire(() => BagShareInviteReceived?.Invoke(bag, fromId, fromName)));

        _conn.On<SharedBagDto>("OnBagStateReceived",
            b => Fire(() => BagStateReceived?.Invoke(b)));

        _conn.On<BagOperationDto, long>("OnBagOperationApplied",
            (op, v) => Fire(() => BagOperationApplied?.Invoke(op, v)));

        _conn.On<Guid>("OnBagDissolved",
            id => { _activeBags.Remove(id); Fire(() => BagDissolved?.Invoke(id)); });

        _conn.On<Guid, string>("OnBagParticipantLeft",
            (id, pid) => Fire(() => BagParticipantLeft?.Invoke(id, pid)));

        _conn.On<Guid, string, string>("OnBagParticipantJoined",
            (id, pid, name) => Fire(() => BagParticipantJoined?.Invoke(id, pid, name)));

        _conn.On<Guid, string>("OnBagParticipantDeclined",
            (id, pid) => Fire(() => BagParticipantDeclined?.Invoke(id, pid)));

        // Errors
        _conn.On<string, string>("OnError",
            (feat, msg) => Fire(() => ErrorReceived?.Invoke(feat, msg)));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // BGM
    // ═════════════════════════════════════════════════════════════════════════

    public async Task BgmJoinAsync(string code)
    {
        _activeRooms.Add(code);
        await SafeInvoke("BgmJoin", code);
    }

    public async Task BgmLeaveAsync(string code)
    {
        _activeRooms.Remove(code);
        await SafeInvoke("BgmLeave", code);
    }

    public Task BgmSyncPlay(string code, int songIndex, double pos)
        => SafeInvoke("BgmSyncPlay", code, songIndex, pos);

    public Task BgmSyncPause(string code, double pos)
        => SafeInvoke("BgmSyncPause", code, pos);

    public Task BgmSyncResume(string code, double pos)
        => SafeInvoke("BgmSyncResume", code, pos);

    public Task BgmSyncSeek(string code, double pos)
        => SafeInvoke("BgmSyncSeek", code, pos);

    public Task BgmSyncStop(string code)
        => SafeInvoke("BgmSyncStop", code);

    public Task BgmSyncLoopMode(string code, NetLoopMode loop)
        => SafeInvoke("BgmSyncLoopMode", code, loop);

    public Task BgmSendAddSong(string code, RpSongDto song)
        => SafeInvoke("BgmAddSong", code, song);

    public Task BgmSendRemoveSong(string code, Guid songId)
        => SafeInvoke("BgmRemoveSong", code, songId);

    public Task BgmPromoteMember(string code, string targetPlayerId, RoomRole role)
        => SafeInvoke("BgmPromoteMember", code, targetPlayerId, role);

    // ═════════════════════════════════════════════════════════════════════════
    // Trading
    // ═════════════════════════════════════════════════════════════════════════

    public Task SendTradeOffer(string targetPlayerId, RpItemDto item, bool isCopy)
        => SafeInvoke("InventoryTrade", targetPlayerId, item, isCopy);

    public Task AcceptTrade(Guid offerId) => SafeInvoke("InventoryTradeAccept", offerId);
    public Task RejectTrade(Guid offerId) => SafeInvoke("InventoryTradeReject", offerId);

    // ═════════════════════════════════════════════════════════════════════════
    // Shared Bags
    // ═════════════════════════════════════════════════════════════════════════

    public async Task ShareBag(string targetPlayerId, SharedBagDto bag)
    {
        _activeBags.Add(bag.BagId);
        await SafeInvoke("BagShare", targetPlayerId, bag);
    }

    public async Task AcceptBagShare(Guid bagId)
    {
        _activeBags.Add(bagId);
        await SafeInvoke("BagShareAccept", bagId);
    }

    public Task RejectBagShare(Guid bagId)  => SafeInvoke("BagShareReject", bagId);

    public Task ApplyBagOperation(BagOperationDto op) => SafeInvoke("BagApplyOperation", op);

    public async Task DissolveBag(Guid bagId)
    {
        _activeBags.Remove(bagId);
        await SafeInvoke("BagDissolve", bagId);
    }

    public async Task DisconnectFromBag(Guid bagId)
    {
        _activeBags.Remove(bagId);
        await SafeInvoke("BagDisconnect", bagId);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private async Task SafeInvoke(string method, params object?[] args)
    {
        if (_conn == null || _conn.State != HubConnectionState.Connected) return;
        try   { await _conn.InvokeCoreAsync(method, args); }
        catch (Exception ex) { Plugin.Log.Warning(ex, $"[Net] {method} failed."); }
    }

    /// <summary>Fires a callback on the Dalamud framework thread.</summary>
    private static void Fire(Action action)
        => Plugin.Framework.RunOnFrameworkThread(action);
}
