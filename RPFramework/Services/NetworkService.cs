using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using RPFramework.Contracts;
using Ev = RPFramework.Contracts.HubRoutes.Events;
using In = RPFramework.Contracts.HubRoutes.Intents;

namespace RPFramework.Services;

/// <summary>
/// SignalR connection to the relay. Server-first: this sends INTENTS and applies the
/// server's authoritative snapshots into <see cref="RpStateStore"/>. Inbound handlers are
/// marshalled to the Dalamud framework thread before touching the store. Transient,
/// non-stored messages (dice results, trade offers, errors) are surfaced as events for
/// the plugin to react to.
/// </summary>
public sealed class NetworkService : IDisposable
{
    private readonly RpStateStore  _store;
    private HubConnection?         _conn;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private string _playerId    = string.Empty;
    private string _secret      = string.Empty;
    private string _displayName = string.Empty;

    public NetworkService(RpStateStore store) => _store = store;

    public HubConnectionState State => _conn?.State ?? HubConnectionState.Disconnected;
    public bool IsConnected => State == HubConnectionState.Connected;

    // ── Lifecycle + transient events (fired on framework thread) ──────────────
    public event Action?                    Connected;
    public event Action?                    Disconnected;
    public event Action?                    Reconnected;
    public event Action<DiceRollResultDto>? DiceRollReceived;
    public event Action<TradeOfferDto>?     TradeOfferReceived;
    public event Action<BagShareInviteDto>? BagShareInviteReceived;
    public event Action<string, string>?    ErrorReceived;   // context, message

    // ═════════════════════════════════════════════════════════════════════════
    // Connection lifecycle
    // ═════════════════════════════════════════════════════════════════════════

    public static Uri? ValidateServerUrl(string? serverUrl, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(serverUrl)) { error = "Server URL is empty."; return null; }
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri))
        { error = "Server URL is not a valid absolute URL."; return null; }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        { error = $"Unsupported URL scheme '{uri.Scheme}' — only http/https are allowed."; return null; }
        return uri;
    }

    public async Task ConnectAsync(string serverUrl, string playerId, string secret, string displayName)
    {
        var uri = ValidateServerUrl(serverUrl, out var urlError);
        if (uri == null) { Plugin.Log.Error("[Net] Refusing to connect: {0}", urlError ?? "invalid URL"); return; }
        if (uri.Scheme == Uri.UriSchemeHttp)
            Plugin.Log.Warning("[Net] Server URL uses plain HTTP — traffic is unencrypted. Prefer https://.");

        await _connectLock.WaitAsync();
        try
        {
            if (_conn != null) await _conn.DisposeAsync();
            _playerId = playerId; _secret = secret; _displayName = displayName;

            _conn = new HubConnectionBuilder()
                .WithUrl(serverUrl.TrimEnd('/') + HubRoutes.Path)
                .WithAutomaticReconnect(new CappedBackoffRetryPolicy())
                .Build();

            RegisterHandlers();

            _conn.Reconnecting += _ => { Plugin.Log.Info("[Net] Reconnecting..."); return Task.CompletedTask; };
            _conn.Reconnected  += async _ =>
            {
                Plugin.Log.Info("[Net] Reconnected — re-identifying (server resends snapshot).");
                await SafeInvoke(In.Identify, _playerId, _secret, _displayName);
                Fire(() => Reconnected?.Invoke());
            };
            _conn.Closed += _ => { Fire(() => Disconnected?.Invoke()); return Task.CompletedTask; };

            await _conn.StartAsync();
            await SafeInvoke(In.Identify, _playerId, _secret, _displayName);
            Fire(() => Connected?.Invoke());
        }
        finally { _connectLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        if (_conn == null) return;
        await _conn.StopAsync();
        await _conn.DisposeAsync();
        _conn = null;
        Fire(() => { _store.Clear(); Disconnected?.Invoke(); });
    }

    public void Dispose()
    {
        _conn?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _connectLock.Dispose();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Inbound: apply authoritative state into the store
    // ═════════════════════════════════════════════════════════════════════════

    private void RegisterHandlers()
    {
        if (_conn == null) return;

        _conn.On<SnapshotDto>(Ev.Snapshot,                 s => Fire(() => _store.ApplySnapshot(s)));
        _conn.On<PartyDto>(Ev.PartyUpdated,                p => Fire(() => _store.ApplyParty(p)));
        _conn.On<string>(Ev.PartyDisbanded,             code => Fire(() => _store.RemoveParty(code)));
        _conn.On<CharacterDto>(Ev.CharacterUpdated,        c => Fire(() => _store.ApplyCharacter(c)));
        _conn.On<TemplateDto>(Ev.TemplateUpdated,          t => Fire(() => _store.ApplyTemplate(t)));
        _conn.On<InitiativeStateDto>(Ev.InitiativeUpdated, i => Fire(() => _store.ApplyInitiative(i)));
        _conn.On<string>(Ev.InitiativeEnded,            code => Fire(() => _store.RemoveInitiative(code)));
        _conn.On<BagDto>(Ev.BagUpdated,                    b => Fire(() => _store.ApplyBag(b)));
        _conn.On<Guid>(Ev.BagRemoved,                     id => Fire(() => _store.RemoveBag(id)));
        _conn.On<RoomStateDto>(Ev.RoomUpdated,             r => Fire(() => _store.ApplyRoom(r)));
        _conn.On<string>(Ev.RoomRemoved,                code => Fire(() => _store.RemoveRoom(code)));

        // Transient — not stored; surfaced to the plugin
        _conn.On<DiceRollResultDto>(Ev.DiceRoll,           d => Fire(() => DiceRollReceived?.Invoke(d)));
        _conn.On<TradeOfferDto>(Ev.TradeOffered,           o => Fire(() => TradeOfferReceived?.Invoke(o)));
        _conn.On<BagShareInviteDto>(Ev.BagShareInvited,    i => Fire(() => BagShareInviteReceived?.Invoke(i)));
        _conn.On<string, string>(Ev.Error,        (ctx, msg) => Fire(() => ErrorReceived?.Invoke(ctx, msg)));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Outbound intents (fire-and-forget; state returns via authoritative broadcasts)
    // ═════════════════════════════════════════════════════════════════════════

    // Parties
    public Task PartyCreate(string name, string? password)         => SafeInvoke(In.PartyCreate, name, password);
    public Task PartyJoin(string code, string? password)          => SafeInvoke(In.PartyJoin, code, password);
    public Task PartyLeave(string code)                           => SafeInvoke(In.PartyLeave, code);
    public Task PartyDisband(string code)                         => SafeInvoke(In.PartyDisband, code);
    public Task PartyKick(string code, string targetId)          => SafeInvoke(In.PartyKick, code, targetId);
    public Task PartySetRole(string code, string targetId, PartyRole role) => SafeInvoke(In.PartySetRole, code, targetId, role);
    public Task PartySetShowHpAp(string code, bool show)         => SafeInvoke(In.PartySetShowHpAp, code, show);

    // Character + template
    public Task CharacterEditStat(string code, string key, int value)     => SafeInvoke(In.CharacterEditStat, code, key, value);
    public Task CharacterEditCheck(string code, string fieldId, bool v)   => SafeInvoke(In.CharacterEditCheck, code, fieldId, v);
    public Task CharacterEditText(string code, string fieldId, string v)  => SafeInvoke(In.CharacterEditText, code, fieldId, v);
    public Task CharacterSetSkills(string code, List<RpSkill> skills)   => SafeInvoke(In.CharacterSetSkills, code, skills);
    public Task UseSkill(string code, Guid skillId)                     => SafeInvoke(In.UseSkill, code, skillId);
    public Task TemplatePublish(string code, SheetTemplate template)    => SafeInvoke(In.TemplatePublish, code, template);

    // Dice
    public Task RollDice(string code, int die, RollMode mode, string? statFieldId, string? specFieldId)
        => SafeInvoke(In.RollDice, code, die, mode, statFieldId, specFieldId);

    // Initiative
    public Task InitiativeStart(string code)     => SafeInvoke(In.InitiativeStart, code);
    public Task InitiativeEndTurn(string code)   => SafeInvoke(In.InitiativeEndTurn, code);
    public Task InitiativeEndCombat(string code) => SafeInvoke(In.InitiativeEndCombat, code);
    public Task InitiativeAddNpc(string code, string name, int roll, int bonus, int hp, int ap)
        => SafeInvoke(In.InitiativeAddNpc, code, name, roll, bonus, hp, ap);
    public Task InitiativeRemove(string code, string entryId) => SafeInvoke(In.InitiativeRemove, code, entryId);

    // Inventory + trading
    public Task BagCreate(string code, string name, bool isDmBag) => SafeInvoke(In.BagCreate, code, name, isDmBag);
    public Task BagRename(Guid bagId, string name)               => SafeInvoke(In.BagRename, bagId, name);
    public Task BagDelete(Guid bagId)                            => SafeInvoke(In.BagDelete, bagId);
    // `path` descends into a nested bag (RpItemType.Bag) item; empty = the inventory root.
    public Task ItemAdd(Guid bagId, Guid[] path, RpItemDto item)    => SafeInvoke(In.ItemAdd, bagId, path, item);
    public Task ItemRemove(Guid bagId, Guid[] path, Guid itemId)    => SafeInvoke(In.ItemRemove, bagId, path, itemId);
    public Task ItemUpdate(Guid bagId, Guid[] path, RpItemDto item) => SafeInvoke(In.ItemUpdate, bagId, path, item);
    public Task ItemMove(Guid fromBag, Guid[] fromPath, Guid toBag, Guid[] toPath, Guid itemId)
        => SafeInvoke(In.ItemMove, fromBag, fromPath, toBag, toPath, itemId);
    public Task ItemSplit(Guid bagId, Guid[] path, Guid itemId, int amount)
        => SafeInvoke(In.ItemSplit, bagId, path, itemId, amount);
    public Task UseItem(Guid bagId, Guid[] path, Guid itemId)
        => SafeInvoke(In.UseItem, bagId, path, itemId);
    public Task BagShareInvite(Guid bagId, string toId) => SafeInvoke(In.BagShareInvite, bagId, toId);
    public Task BagShareAccept(Guid bagId)              => SafeInvoke(In.BagShareAccept, bagId);
    public Task BagShareDecline(Guid bagId)             => SafeInvoke(In.BagShareDecline, bagId);
    public Task BagLeave(Guid bagId)                    => SafeInvoke(In.BagLeave, bagId);
    // Trade a specific amount of an item located at (sourceBag, sourcePath, itemId). For bags/non-stackables, amount = 1.
    public Task TradeOffer(string code, string toId, Guid sourceBag, Guid[] sourcePath, Guid itemId, int amount, bool isCopy)
        => SafeInvoke(In.TradeOffer, code, toId, sourceBag, sourcePath, itemId, amount, isCopy);
    public Task TradeAccept(Guid offerId)                       => SafeInvoke(In.TradeAccept, offerId);
    public Task TradeDecline(Guid offerId)                      => SafeInvoke(In.TradeDecline, offerId);

    // BGM
    public Task RoomCreate(string name, string? password, string? campaignCode) => SafeInvoke(In.RoomCreate, name, password, campaignCode);
    public Task RoomJoin(string code, string? password)      => SafeInvoke(In.RoomJoin, code, password);
    public Task RoomLeave(string code)                       => SafeInvoke(In.RoomLeave, code);
    public Task RoomPromote(string code, string targetId, RoomRole role) => SafeInvoke(In.RoomPromote, code, targetId, role);
    public Task PlaylistAdd(string code, string title, string url)       => SafeInvoke(In.PlaylistAdd, code, title, url);
    public Task PlaylistRemove(string code, Guid songId)               => SafeInvoke(In.PlaylistRemove, code, songId);
    public Task PlaybackCommand(string code, PlaybackCommandDto cmd)    => SafeInvoke(In.PlaybackCommand, code, cmd);

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private async Task SafeInvoke(string method, params object?[] args)
    {
        if (_conn == null || _conn.State != HubConnectionState.Connected) return;
        try   { await _conn.InvokeCoreAsync(method, args); }
        catch (Exception ex) { Plugin.Log.Warning(ex, $"[Net] {method} failed."); }
    }

    private static void Fire(Action action) => Plugin.Framework.RunOnFrameworkThread(action);

    /// <summary>Retries forever with capped backoff (0s, 2s, 5s, 15s, then 30s) so long outages self-heal.</summary>
    private sealed class CappedBackoffRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Steps =
        {
            TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        };

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
            => retryContext.PreviousRetryCount < Steps.Length
                ? Steps[retryContext.PreviousRetryCount]
                : TimeSpan.FromSeconds(30);
    }
}
