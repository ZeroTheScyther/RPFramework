using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using RPFramework.Models;
using RPFramework.Models.Net;
using RPFramework.Services;
using RPFramework.Windows;

namespace RPFramework;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager     { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState     { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui         { get; private set; } = null!;
    [PluginService] internal static IContextMenu            ContextMenu     { get; private set; } = null!;

    private const string CmdHub          = "/rphub";
    private const string CmdIni          = "/rpini";
    private const string CmdInventory    = "/rpinventory";
    private const string CmdInventoryShort = "/rpinv";
    private const string CmdBgm         = "/rpbgm";
    private const string CmdSettings    = "/rpsettings";
    private const string CmdSheet       = "/rpsheet";
    private const string CmdSheetShort  = "/rpcs";
    private const string CmdStats       = "/rpstats";
    private const string CmdDice        = "/rpdice";
    private const string CmdSkills      = "/rpskills";
    private const string CmdSkillsShort = "/rpsk";
    private const string CmdHelp        = "/rphelp";
    private const string DefaultServerUrl = "https://rpframework.example.com";

    public Configuration   Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("RPFramework");

    /// <summary>In-memory participant display names, keyed by bag ID then player ID.</summary>
    public readonly Dictionary<Guid, Dictionary<string, string>> BagParticipants = new();

    /// <summary>In-memory party member lists, keyed by party code then player ID.</summary>
    public readonly Dictionary<string, List<PartyMemberDto>> PartyMembers = new();

    /// <summary>Players whose profiles have been received this session (i.e., they are/were online).</summary>
    public readonly HashSet<string> KnownOnlinePlayers = new();

    /// <summary>Active initiative states, keyed by party code.</summary>
    public readonly Dictionary<string, InitiativeStateDto> InitiativeStates = new();

    /// <summary>Per-party ShowHpAp preference — persists before, during, and between initiative rounds.</summary>
    public readonly Dictionary<string, bool> PartyInitiativeShowHpAp = new();

    private bool _autoConnectPending;
    private bool _partyMigrationPending;

    // Dynamic player profile windows (one per remote player ID)
    private readonly Dictionary<string, PlayerSheetWindow>  _playerSheetWindows  = new();
    private readonly Dictionary<string, PlayerSkillsWindow> _playerSkillsWindows = new();
    private readonly HashSet<string> _pendingSheetFetches  = new();
    private readonly HashSet<string> _pendingSkillsFetches = new();
    private readonly Dictionary<string, string> _pendingSheetParty = new();
    private DateTime _lastProfilePush = DateTime.MinValue;

    private HubWindow               HubWindow               { get; init; }
    internal InitiativeWindow       InitiativeWindow        { get; init; }
    private InventoryWindow         InventoryWindow         { get; init; }
    private BgmWindow               BgmWindow               { get; init; }
    private BgmPlayerWindow         BgmPlayerWindow         { get; init; }
    private TradeNotificationWindow TradeNotificationWindow { get; init; }
    private BagShareInviteWindow    BagShareInviteWindow    { get; init; }
    internal SettingsWindow         SettingsWindow          { get; init; }
    internal CharacterSheetWindow   CharacterSheetWindow    { get; init; }
    private DiceRollerWindow        DiceRollerWindow        { get; init; }
    private SkillsWindow            SkillsWindow            { get; init; }
    private HelpWindow              HelpWindow              { get; init; }
    public  BgmService              BgmService              { get; init; }
    public  NetworkService          Network                 { get; init; }

    public Plugin()
    {
        // A corrupted / hand-edited config file must never prevent the plugin from loading
        Configuration? loaded = null;
        try { loaded = PluginInterface.GetPluginConfig() as Configuration; }
        catch (Exception ex) { Log.Error(ex, "Failed to load configuration — starting with defaults."); }
        Configuration = loaded ?? new Configuration();
        ValidateConfiguration(Configuration);

        if (Configuration.Bags.Count == 0)
            Configuration.Bags.Add(new RpBag { Name = "Bag" });

        // Shared bags are transient — purge any leftover data from previous sessions.
        // They are re-populated by the server via OnBagStateReceived after connecting.
        ClearSharedBagData();

        string bgmCacheDir = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "bgm_cache");
        BgmService = new BgmService(bgmCacheDir);
        Network    = new NetworkService();

        HubWindow               = new HubWindow(this);
        InitiativeWindow        = new InitiativeWindow(this);
        TradeNotificationWindow = new TradeNotificationWindow(this);
        BagShareInviteWindow    = new BagShareInviteWindow(this);
        SettingsWindow          = new SettingsWindow(this);
        InventoryWindow         = new InventoryWindow(this);
        BgmPlayerWindow         = new BgmPlayerWindow(this);
        BgmWindow               = new BgmWindow(this, BgmService, BgmPlayerWindow);
        CharacterSheetWindow    = new CharacterSheetWindow(this);
        DiceRollerWindow        = new DiceRollerWindow(this);
        SkillsWindow            = new SkillsWindow(this);
        HelpWindow              = new HelpWindow();

        WindowSystem.AddWindow(HubWindow);
        WindowSystem.AddWindow(InitiativeWindow);
        WindowSystem.AddWindow(InventoryWindow);
        WindowSystem.AddWindow(BgmWindow);
        WindowSystem.AddWindow(BgmPlayerWindow);
        WindowSystem.AddWindow(TradeNotificationWindow);
        WindowSystem.AddWindow(BagShareInviteWindow);
        WindowSystem.AddWindow(SettingsWindow);
        WindowSystem.AddWindow(CharacterSheetWindow);
        WindowSystem.AddWindow(DiceRollerWindow);
        WindowSystem.AddWindow(SkillsWindow);
        WindowSystem.AddWindow(HelpWindow);

        // Wire up network events
        Network.Connected              += OnConnected;
        Network.Reconnected            += OnConnected;
        Network.Disconnected           += OnDisconnected;
        Network.TradeOfferReceived     += OnTradeOfferReceived;
        Network.TradeItemReceived      += OnTradeItemReceived;
        Network.TradeAccepted          += OnTradeAccepted;
        Network.BagShareInviteReceived += OnBagShareInvite;
        Network.BagOperationApplied    += OnBagOperationApplied;
        Network.BagDissolved           += OnBagDissolved;
        Network.BagStateReceived       += OnBagStateReceived;
        Network.BagShareFailed         += OnBagShareFailed;
        Network.BagParticipantJoined   += OnBagParticipantJoined;
        Network.BagParticipantLeft     += OnBagParticipantLeft;
        Network.ProfileReceived        += OnProfileReceived;
        Network.ProfileFetchFailed     += OnProfileFetchFailed;
        Network.PartyInfoReceived      += OnPartyInfoReceived;
        Network.PartyMemberJoined        += OnPartyMemberJoined;
        Network.PartyMemberLeft          += OnPartyMemberLeft;
        Network.PartyMemberDisconnected  += OnPartyMemberDisconnected;
        Network.PartyDisbanded           += OnPartyDisbanded;
        Network.PartyMemberBgmChanged  += OnPartyMemberBgmChanged;
        Network.PartyMemberRoleChanged += OnPartyMemberRoleChanged;
        Network.BgmRoomDeleted         += OnBgmRoomDeleted;
        Network.PartyInitiativeStarted        += OnPartyInitiativeStarted;
        Network.PartyInitiativeUpdated        += OnPartyInitiativeUpdated;
        Network.PartyInitiativeEnded          += OnPartyInitiativeEnded;
        Network.PartyInitiativeShowHpApChanged += OnPartyInitiativeShowHpApChanged;
        Network.PartySheetTemplateReceived     += OnSheetTemplateReceived;
        Network.DiceRollReceived               += OnDiceRollReceived;

        MigrateCharacters();
        _partyMigrationPending = true; // deferred: LocalPlayerId requires framework thread

        ContextMenu.OnMenuOpened += OnContextMenuOpened;

        CommandManager.AddHandler(CmdHub,            new CommandInfo(OnHubCmd)       { HelpMessage = "Opens RP Hub — connection and party management" });
        CommandManager.AddHandler(CmdInventory,      new CommandInfo(OnInventoryCmd) { HelpMessage = "Opens the RP Inventory" });
        CommandManager.AddHandler(CmdInventoryShort, new CommandInfo(OnInventoryCmd) { HelpMessage = "Opens the RP Inventory" });
        CommandManager.AddHandler(CmdBgm,            new CommandInfo(OnBgmCmd)       { HelpMessage = "Opens the RP BGM music player" });
        CommandManager.AddHandler(CmdSettings,       new CommandInfo(OnSettingsCmd)  { HelpMessage = "Opens RPFramework Settings" });
        CommandManager.AddHandler(CmdSheet,          new CommandInfo(OnSheetCmd)     { HelpMessage = "Opens the RP Character Sheet" });
        CommandManager.AddHandler(CmdSheetShort,     new CommandInfo(OnSheetCmd)     { HelpMessage = "Opens the RP Character Sheet" });
        CommandManager.AddHandler(CmdStats,          new CommandInfo(OnSheetCmd)     { HelpMessage = "Opens the RP Character Sheet" });
        CommandManager.AddHandler(CmdDice,           new CommandInfo(OnDiceCmd)      { HelpMessage = "Opens dice roller, or rolls immediately: /rpdice [d4/d6/.../d20/dN]" });
        CommandManager.AddHandler(CmdSkills,         new CommandInfo(OnSkillsCmd)    { HelpMessage = "Opens the RP Skills & Passives window" });
        CommandManager.AddHandler(CmdSkillsShort,    new CommandInfo(OnSkillsCmd)    { HelpMessage = "Opens the RP Skills & Passives window" });
        CommandManager.AddHandler(CmdIni,            new CommandInfo(OnIniCmd)       { HelpMessage = "Opens the RP Initiative tracker" });
        CommandManager.AddHandler(CmdHelp,           new CommandInfo(OnHelpCmd)      { HelpMessage = "Opens the RPFramework help window" });

        PluginInterface.UiBuilder.Draw         += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   += HubWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += SettingsWindow.Toggle;
        Framework.Update                       += OnFrameworkUpdate;
        ClientState.Login                      += OnLogin;

        // Auto-connect deferred to first framework tick (LocalPlayerId not safe to call in ctor)
        _autoConnectPending = Configuration.ServerUrl != DefaultServerUrl;
    }

    public void Dispose()
    {
        Network.Connected              -= OnConnected;
        Network.Reconnected            -= OnConnected;
        Network.Disconnected           -= OnDisconnected;
        Network.TradeOfferReceived     -= OnTradeOfferReceived;
        Network.TradeItemReceived      -= OnTradeItemReceived;
        Network.TradeAccepted          -= OnTradeAccepted;
        Network.BagShareInviteReceived -= OnBagShareInvite;
        Network.BagOperationApplied    -= OnBagOperationApplied;
        Network.BagDissolved           -= OnBagDissolved;
        Network.BagStateReceived       -= OnBagStateReceived;
        Network.BagShareFailed         -= OnBagShareFailed;
        Network.BagParticipantJoined   -= OnBagParticipantJoined;
        Network.BagParticipantLeft     -= OnBagParticipantLeft;
        Network.ProfileReceived        -= OnProfileReceived;
        Network.ProfileFetchFailed     -= OnProfileFetchFailed;
        Network.PartyInfoReceived      -= OnPartyInfoReceived;
        Network.PartyMemberJoined        -= OnPartyMemberJoined;
        Network.PartyMemberLeft          -= OnPartyMemberLeft;
        Network.PartyMemberDisconnected  -= OnPartyMemberDisconnected;
        Network.PartyDisbanded           -= OnPartyDisbanded;
        Network.PartyMemberBgmChanged  -= OnPartyMemberBgmChanged;
        Network.PartyMemberRoleChanged -= OnPartyMemberRoleChanged;
        Network.BgmRoomDeleted         -= OnBgmRoomDeleted;
        Network.PartyInitiativeStarted         -= OnPartyInitiativeStarted;
        Network.PartyInitiativeUpdated         -= OnPartyInitiativeUpdated;
        Network.PartyInitiativeEnded           -= OnPartyInitiativeEnded;
        Network.PartyInitiativeShowHpApChanged -= OnPartyInitiativeShowHpApChanged;
        Network.PartySheetTemplateReceived     -= OnSheetTemplateReceived;
        Network.DiceRollReceived               -= OnDiceRollReceived;

        ContextMenu.OnMenuOpened -= OnContextMenuOpened;

        foreach (var w in _playerSheetWindows.Values)  w.Dispose();
        foreach (var w in _playerSkillsWindows.Values) w.Dispose();

        Framework.Update                       -= OnFrameworkUpdate;
        ClientState.Login                      -= OnLogin;
        PluginInterface.UiBuilder.Draw         -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= HubWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= SettingsWindow.Toggle;

        WindowSystem.RemoveAllWindows();
        HubWindow.Dispose();
        InitiativeWindow.Dispose();
        InventoryWindow.Dispose();
        BgmWindow.Dispose();
        BgmPlayerWindow.Dispose();
        TradeNotificationWindow.Dispose();
        BagShareInviteWindow.Dispose();
        SettingsWindow.Dispose();
        CharacterSheetWindow.Dispose();
        DiceRollerWindow.Dispose();
        SkillsWindow.Dispose();
        BgmService.Dispose();
        Network.Dispose();

        CommandManager.RemoveHandler(CmdHub);
        CommandManager.RemoveHandler(CmdInventory);
        CommandManager.RemoveHandler(CmdInventoryShort);
        CommandManager.RemoveHandler(CmdBgm);
        CommandManager.RemoveHandler(CmdSettings);
        CommandManager.RemoveHandler(CmdSheet);
        CommandManager.RemoveHandler(CmdSheetShort);
        CommandManager.RemoveHandler(CmdStats);
        CommandManager.RemoveHandler(CmdDice);
        CommandManager.RemoveHandler(CmdSkills);
        CommandManager.RemoveHandler(CmdSkillsShort);
        CommandManager.RemoveHandler(CmdIni);
        CommandManager.RemoveHandler(CmdHelp);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        BgmService.Update();

        if (_partyMigrationPending && LocalPlayerId != null)
        {
            _partyMigrationPending = false;
            MigrateToPartyCharacters();
        }

        if (_autoConnectPending && !Network.IsConnected)
        {
            string? id = LocalPlayerId;
            if (id != null)
            {
                _autoConnectPending = false;
                string url = Configuration.ServerUrl, name = LocalDisplayName;
                Task.Run(() => Network.ConnectAsync(url, id, name));
            }
        }
    }

    private void OnLogin()
    {
        if (LocalPlayerId is { } pid)
            GetOrCreateCharacter(pid);

        _partyMigrationPending = true; // re-run on login in case character changed

        if (Configuration.ServerUrl == DefaultServerUrl || Network.IsConnected) return;
        string? id = LocalPlayerId;
        if (id == null) return;
        string url = Configuration.ServerUrl, name = LocalDisplayName;
        Task.Run(() => Network.ConnectAsync(url, id, name));
    }

    private void OnHubCmd(string command, string args)       => HubWindow.Toggle();
    private void OnInventoryCmd(string command, string args) => InventoryWindow.Toggle();
    private void OnBgmCmd(string command, string args)       => BgmWindow.Toggle();
    private void OnSettingsCmd(string command, string args)  => SettingsWindow.Toggle();

    private void OnSheetCmd(string command, string args)
    {
        string target = args.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target))
        {
            CharacterSheetWindow.OpenPicker();
            CharacterSheetWindow.IsOpen = true;
        }
        else
            OpenPlayerSheet(target, ParseDisplayName(target));
    }

    private void OnSkillsCmd(string command, string args)
    {
        string target = args.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target))
            SkillsWindow.Toggle();
        else
            OpenPlayerSkills(target, ParseDisplayName(target));
    }

    private void OnIniCmd(string command, string args)  => InitiativeWindow.Toggle();
    private void OnHelpCmd(string command, string args) => HelpWindow.Toggle();

    private void OnDiceCmd(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            DiceRollerWindow.Toggle();
        else
            DiceRollerWindow.RollFromCommand(args);
    }

    // ── Network lifecycle ─────────────────────────────────────────────────────

    private void OnDisconnected() => ClearSharedBagData();

    /// <summary>
    /// Removes shared bag data from local config without touching the SharedBagRef list.
    /// The refs are needed to re-join after reconnect; the server re-sends bag contents via OnBagStateReceived.
    /// </summary>
    private void ClearSharedBagData()
    {
        var sharedIds = Configuration.Bags
            .Where(b => b.IsShared)
            .Select(b => b.Id)
            .ToList();
        if (sharedIds.Count == 0) return;

        Configuration.Bags.RemoveAll(b => b.IsShared);
        foreach (var id in sharedIds)
            BagParticipants.Remove(id);
        Configuration.Save();
    }

    /// <summary>
    /// Called on both initial connect and reconnect.
    /// Pushes local profile so party members get an immediate update.
    /// </summary>
    private void OnConnected()
    {
        // Push profile immediately — bypasses the throttle for the first push after connect
        _lastProfilePush = DateTime.MinValue;
        PushLocalProfile();

        // Re-join shared bags from persisted config. _activeBags is empty after a plugin
        // reload, so the NetworkService reconnect handler skips them. AcceptBagShare is
        // idempotent (HashSet add + server group join), so this is safe on normal reconnects too.
        foreach (var sharedRef in Configuration.SharedBags)
        {
            var bagId = sharedRef.BagId;
            Task.Run(() => Network.AcceptBagShare(bagId));
        }
    }

    // ── Trade event handlers ──────────────────────────────────────────────────

    private void OnTradeOfferReceived(TradeOfferDto offer)
    {
        TradeNotificationWindow.AddOffer(offer);
        TradeNotificationWindow.IsOpen = true;
    }

    private void OnTradeAccepted(Guid offerId, bool isCopy, Guid itemId)
    {
        if (!isCopy)
        {
            foreach (var bag in Configuration.Bags)
            {
                if (bag.Items.RemoveAll(i => i.Id == itemId) > 0)
                {
                    Configuration.Save();
                    if (bag.IsShared)
                        PublishBagOp(bag.Id, BagOpType.RemoveItem, itemId: itemId);
                    break;
                }
            }
        }
        TradeNotificationWindow.OnAccepted(offerId, isCopy);
    }

    private void OnTradeItemReceived(Guid offerId, RpItemDto item, bool isCopy)
    {
        if (item == null)
        {
            Log.Warning("[Net] Discarded malformed trade item payload from server.");
            return;
        }
        if (Configuration.Bags.Count > 0)
        {
            // Depth/length-guarded conversion; new Id so a duplicate-Id payload
            // can't collide with an item we already own
            var received = DtoToItem(item);
            received.Id = Guid.NewGuid();
            Configuration.Bags[0].Items.Add(received);
            Configuration.Save();
        }
    }

    // ── Bag event handlers ────────────────────────────────────────────────────

    private void OnBagShareInvite(SharedBagDto bag, string fromId, string fromName)
    {
        BagShareInviteWindow.AddInvite(bag, fromId, fromName);
        BagShareInviteWindow.IsOpen = true;
    }

    private void OnBagOperationApplied(BagOperationDto op, long newVersion)
    {
        var bag = Configuration.Bags.Find(b => b.Id == op.BagId);
        if (bag == null) return;

        switch (op.OpType)
        {
            case BagOpType.AddItem when op.Item != null:
                if (!bag.Items.Exists(i => i.Id == op.Item.Id))
                    bag.Items.Add(DtoToItem(op.Item));
                break;
            case BagOpType.RemoveItem when op.ItemId.HasValue:
                bag.Items.RemoveAll(i => i.Id == op.ItemId.Value);
                break;
            case BagOpType.UpdateItem when op.Item != null:
                var existing = bag.Items.Find(i => i.Id == op.Item.Id);
                if (existing != null)
                {
                    existing.Name        = op.Item.Name;
                    existing.Description = op.Item.Description;
                    existing.IconId      = op.Item.IconId;
                    existing.Amount      = op.Item.Amount;
                    existing.Type        = op.Item.Type;
                    existing.Capacity    = op.Item.Capacity;
                    existing.Contents    = op.Item.Contents?.ConvertAll(DtoToItem) ?? new();
                }
                break;
            case BagOpType.Rename when op.NewName != null:
                bag.Name = op.NewName;
                break;
            case BagOpType.SetGil when op.Gil.HasValue:
                bag.Gil = op.Gil.Value;
                break;
        }

        var sharedRef = Configuration.SharedBags.Find(r => r.BagId == op.BagId);
        if (sharedRef != null) sharedRef.Version = newVersion;
        Configuration.Save();
    }

    private void OnBagDissolved(Guid bagId)
    {
        Configuration.Bags.RemoveAll(b => b.Id == bagId);
        Configuration.SharedBags.RemoveAll(r => r.BagId == bagId);
        BagParticipants.Remove(bagId);
        Configuration.Save();
    }

    private void OnBagParticipantJoined(Guid bagId, string playerId, string displayName)
    {
        if (!BagParticipants.TryGetValue(bagId, out var dict))
            BagParticipants[bagId] = dict = new Dictionary<string, string>();
        dict[playerId] = displayName;
    }

    private void OnBagParticipantLeft(Guid bagId, string playerId)
    {
        if (BagParticipants.TryGetValue(bagId, out var dict))
            dict.Remove(playerId);
    }

    private void OnBagStateReceived(SharedBagDto dto)
    {
        if (dto == null || dto.BagId == Guid.Empty || dto.Items == null
            || dto.Items.Count > MaxItemsPerLevel)
        {
            Log.Warning("[Net] Discarded malformed shared-bag payload from server.");
            return;
        }

        var bag = Configuration.Bags.Find(b => b.Id == dto.BagId);
        if (bag == null)
        {
            // First time seeing this bag this session — create it from server data
            bag = new RpBag
            {
                Id          = dto.BagId,
                Name        = dto.Name,
                SharedOwner = dto.OwnerPlayerId,
            };
            Configuration.Bags.Add(bag);
        }

        bag.Items.Clear();
        bag.Items.AddRange(dto.Items.ConvertAll(DtoToItem));
        bag.Gil = dto.Gil;

        var sharedRef = Configuration.SharedBags.Find(r => r.BagId == dto.BagId);
        if (sharedRef != null) sharedRef.Version = dto.Version;
        Configuration.Save();
    }

    private void OnBagShareFailed(Guid bagId)
    {
        bool dirty = false;

        // Remove the pending/stale shared-bag reference.
        dirty |= Configuration.SharedBags.RemoveAll(r => r.BagId == bagId) > 0;

        // If we were the owner attempting to share (bag still exists locally),
        // clear SharedOwner so ClearSharedBagData() won't delete the bag on disconnect.
        var bag = Configuration.Bags.Find(b => b.Id == bagId);
        if (bag != null && bag.SharedOwner != null)
        {
            bag.SharedOwner = null;
            dirty = true;
        }

        if (dirty)
        {
            Configuration.Save();
            Log.Warning("Bag share failed for {BagId}: target is not connected to the relay.", bagId);
        }
    }

    // ── Party event handlers ──────────────────────────────────────────────────

    /// <summary>
    /// Handles full party info from server. Upserts the party in config and updates in-memory members.
    /// Also called by HubWindow when create/join returns.
    /// </summary>
    public void OnPartyInfoReceived(PartyInfoDto info)
    {
        // Upsert in config
        var existing = Configuration.Parties.Find(p => p.Code == info.Code);
        if (existing == null)
        {
            Configuration.Parties.Add(new RpParty
            {
                Code          = info.Code,
                Name          = info.Name,
                OwnerPlayerId = info.OwnerPlayerId,
            });
        }
        else
        {
            existing.Name          = info.Name;
            existing.OwnerPlayerId = info.OwnerPlayerId;
        }
        Configuration.Save();

        // Refresh in-memory member list
        PartyMembers[info.Code] = info.Members.ToList();

        // Seed ShowHpAp setting for this party (only if not already set locally)
        PartyInitiativeShowHpAp.TryAdd(info.Code, info.ShowHpAp);

        // Fetch profiles of online party members we don't have yet
        foreach (var member in info.Members)
        {
            if (member.PlayerId != LocalPlayerId)
                Task.Run(() => Network.FetchProfileAsync(member.PlayerId));
        }
    }

    private void OnPartyMemberJoined(string code, PartyMemberDto member)
    {
        if (!PartyMembers.TryGetValue(code, out var list))
            PartyMembers[code] = list = new List<PartyMemberDto>();

        // Replace if already present (reconnect / role update)
        int idx = list.FindIndex(m => m.PlayerId == member.PlayerId);
        if (idx >= 0) list[idx] = member;
        else          list.Add(member);

        // Fetch fresh profile for the joining member
        if (member.PlayerId != LocalPlayerId)
            Task.Run(() => Network.FetchProfileAsync(member.PlayerId));
    }

    private void OnPartyMemberLeft(string code, string playerId)
    {
        if (PartyMembers.TryGetValue(code, out var list))
            list.RemoveAll(m => m.PlayerId == playerId);
        KnownOnlinePlayers.Remove(playerId);
    }

    private void OnPartyMemberDisconnected(string code, string playerId)
    {
        // Keep the member in the list so they show as greyed-out; just mark offline.
        KnownOnlinePlayers.Remove(playerId);
    }

    private void OnPartyDisbanded(string code)
    {
        PartyMembers.Remove(code);
        InitiativeStates.Remove(code);
        Configuration.Parties.RemoveAll(p => p.Code == code);
        Configuration.PartyTemplates.Remove(code);
        if (Configuration.ActivePartyCode == code)
            Configuration.ActivePartyCode = null;
        Configuration.Save();
    }

    private void OnPartyMemberBgmChanged(string code, string playerId, List<string> bgmRoomCodes)
    {
        if (!PartyMembers.TryGetValue(code, out var list)) return;
        int idx = list.FindIndex(m => m.PlayerId == playerId);
        if (idx >= 0)
        {
            var m = list[idx];
            list[idx] = m with { BgmRoomCodes = bgmRoomCodes };
        }
    }

    private void OnPartyMemberRoleChanged(string code, PartyMemberDto member)
    {
        if (!PartyMembers.TryGetValue(code, out var list)) return;
        int idx = list.FindIndex(m => m.PlayerId == member.PlayerId);
        if (idx >= 0)
            list[idx] = member;
        // Also update our config if the owner changed (shouldn't happen, but be defensive)
        var party = Configuration.Parties.Find(p => p.Code == code);
        if (party != null && member.Role == PartyRole.Owner)
        {
            party.OwnerPlayerId = member.PlayerId;
            Configuration.Save();
        }
    }

    /// <summary>
    /// Leaves a party: tells the server, then removes from config + in-memory state.
    /// </summary>
    public void LeaveParty(string code)
    {
        Task.Run(() => Network.PartyLeaveAsync(code));
        // Optimistically remove locally — server will confirm via OnPartyMemberLeft / OnPartyDisbanded
        PartyMembers.Remove(code);
        InitiativeStates.Remove(code);
        Configuration.Parties.RemoveAll(p => p.Code == code);
        Configuration.PartyTemplates.Remove(code);
        if (Configuration.ActivePartyCode == code)
            Configuration.ActivePartyCode = null;
        Configuration.Save();
    }

    // ── BGM helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Joins a BGM room from a party member's room list.
    /// Adds it to local config if not already present, then joins on the server.
    /// </summary>
    public void JoinBgmRoomFromParty(string code)
    {
        var rooms = Configuration.Rooms;
        if (!rooms.Any(r => r.Code == code))
        {
            rooms.Add(new RpRoom { Name = $"Room {code}", Code = code });
            Configuration.Save();
        }
        Task.Run(() => Network.BgmJoinAsync(code));
    }

    private void OnBgmRoomDeleted(string code)
    {
        // Stop playback if we're in this room
        if (BgmService.CurrentRoom?.Code == code)
            BgmService.Stop();

        if (Configuration.Rooms.RemoveAll(r => r.Code == code) > 0)
            Configuration.Save();

        // Also tell the server we've left (in case we hadn't already)
        Task.Run(() => Network.BgmLeaveAsync(code));
    }

    // ── Initiative event handlers ─────────────────────────────────────────────

    private void OnPartyInitiativeStarted(string code)
    {
        string? pid = LocalPlayerId;
        if (pid == null) return;

        var ch        = GetOrCreatePartyCharacter(code, pid);
        var template  = GetPartyTemplate(code);
        int initBonus = GetInitiativeBonus(ch, template);
        // The d24 is rolled server-side (anti-cheat); the roll argument exists only
        // for wire compatibility and is ignored by current servers.
        int roll      = new Random().Next(1, 25);
        Task.Run(() => Network.PartySubmitRollAsync(code, roll, initBonus));

        InitiativeWindow.IsOpen = true;
    }

    public int GetInitiativeBonus(RpCharacter ch, SheetTemplate template)
    {
        var initField = template.FindInitiativeStat();
        if (initField == null) return 0;
        ch.StatValues.TryGetValue(initField.Id, out int raw);
        return SkillHelpers.StatMod(raw + SkillHelpers.PassiveStatAdjust(ch, initField.Id, template));
    }

    private void OnPartyInitiativeUpdated(string code, InitiativeStateDto state)
    {
        InitiativeStates[code] = state;
        PartyInitiativeShowHpAp[code] = state.ShowHpAp;
        InitiativeWindow.IsOpen = true;
    }

    private void OnPartyInitiativeEnded(string code)
    {
        InitiativeStates.Remove(code);
        // Leave the window open so players can see the result; they close it manually
    }

    private void OnPartyInitiativeShowHpApChanged(string code, bool show)
    {
        PartyInitiativeShowHpAp[code] = show;
    }

    // ── Shared bags public helper ─────────────────────────────────────────────

    public void PublishBagOp(Guid bagId, BagOpType opType,
        RpItemDto? item = null, Guid? itemId = null, string? newName = null, int? gil = null)
    {
        var sharedRef = Configuration.SharedBags.Find(r => r.BagId == bagId);
        if (sharedRef == null) return;
        var op = new BagOperationDto(bagId, sharedRef.Version, opType, item, itemId, newName, gil);
        Task.Run(() => Network.ApplyBagOperation(op));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>Max nested bag depth / item counts accepted from the network.</summary>
    private const int MaxItemDepth     = 4;
    private const int MaxItemsPerLevel = 500;

    public static RpItem DtoToItem(RpItemDto dto) => DtoToItem(dto, 0);

    private static RpItem DtoToItem(RpItemDto dto, int depth) => new()
    {
        Id          = dto.Id,
        Name        = Truncate(dto.Name, 64),
        Description = Truncate(dto.Description, 2048),
        IconId      = dto.IconId,
        Amount      = Math.Clamp(dto.Amount, 0, 9_999_999),
        Type        = dto.Type,
        Capacity    = Math.Clamp(dto.Capacity, 0, 1000),
        // Depth/size-guarded: a malicious server payload cannot blow up the config
        // with an unbounded or cyclic item tree.
        Contents    = depth >= MaxItemDepth
            ? new()
            : dto.Contents?.Take(MaxItemsPerLevel).Select(c => DtoToItem(c, depth + 1)).ToList() ?? new(),
    };

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);

    /// <summary>
    /// Repairs a loaded configuration: drops structurally invalid entries (null slots,
    /// empty codes/ids) so a corrupted or tampered file can't crash later code paths.
    /// </summary>
    private static void ValidateConfiguration(Configuration cfg)
    {
        cfg.Bags    ??= new();
        cfg.Rooms   ??= new();
        cfg.Parties ??= new();
        cfg.SharedBags        ??= new();
        cfg.Characters        ??= new();
        cfg.PartyTemplates    ??= new();
        cfg.PartyCharacters   ??= new();
        cfg.FellowAdventurers ??= new();
        cfg.ServerUrl         ??= string.Empty;

        cfg.Bags.RemoveAll(b => b == null || b.Id == Guid.Empty);
        foreach (var bag in cfg.Bags)
        {
            bag.Items ??= new();
            bag.Items.RemoveAll(i => i == null || i.Id == Guid.Empty);
        }
        cfg.Rooms.RemoveAll(r => r == null || string.IsNullOrWhiteSpace(r.Code));
        cfg.Parties.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Code));
        cfg.SharedBags.RemoveAll(r => r == null || r.BagId == Guid.Empty);

        foreach (var ch in cfg.Characters.Values)
        {
            ch.StatValues  ??= new();
            ch.CheckValues ??= new();
            ch.Skills      ??= new();
            ch.Skills.RemoveAll(s => s == null);
        }
        foreach (var ch in cfg.PartyCharacters.Values)
        {
            ch.StatValues  ??= new();
            ch.CheckValues ??= new();
            ch.Skills      ??= new();
            ch.Skills.RemoveAll(s => s == null);
        }
    }

    public static RpItemDto ItemToDto(RpItem item) => new(
        item.Id,
        item.Name,
        item.Description,
        item.IconId,
        item.Amount,
        item.Type,
        item.Capacity,
        item.Contents.Count > 0 ? item.Contents.ConvertAll(ItemToDto) : null
    );

    public string? LocalPlayerId
    {
        get
        {
            var player = ObjectTable.LocalPlayer;
            if (player == null) return null;
            string? world = null;
            if (DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()
                           .TryGetRow(player.HomeWorld.RowId, out var worldRow))
                world = worldRow.Name.ToString();
            return world == null ? null : $"{player.Name}@{world}";
        }
    }

    public string LocalDisplayName => ObjectTable.LocalPlayer?.Name.ToString() ?? "Unknown";

    private static string ParseDisplayName(string playerId)
        => playerId.Contains('@') ? playerId[..playerId.IndexOf('@')] : playerId;

    // ── Profile sync ──────────────────────────────────────────────────────────

    public CharacterProfileDto BuildProfileDto(string pid, string displayName, RpCharacter ch)
    {
        var skills = ch.Skills.Select(s => new RpSkillDto(
            s.Id, s.Name, s.Description, s.Type, s.Cooldown, s.Duration,
            s.Conditions, s.Effects)).ToList();
        return new CharacterProfileDto(
            pid, displayName,
            new Dictionary<string, int>(ch.StatValues),
            new Dictionary<string, bool>(ch.CheckValues),
            skills);
    }

    private void MigrateCharacters()
    {
        // Idempotent: each character carries a SheetMigrated flag, and the config-level
        // MigrationVersion short-circuits the whole scan once every character is done.
        if (Configuration.MigrationVersion >= 1) return;

        bool dirty = false;
        foreach (var (_, ch) in Configuration.Characters)
        {
            if (ch.SheetMigrated) continue;

            ch.StatValues[WellKnownIds.Str] = ch.Str;
            ch.StatValues[WellKnownIds.Dex] = ch.Dex;
            ch.StatValues[WellKnownIds.Spd] = ch.Spd;
            ch.StatValues[WellKnownIds.Con] = ch.Con;
            ch.StatValues[WellKnownIds.Mem] = ch.Mem;
            ch.StatValues[WellKnownIds.Mtl] = ch.Mtl;
            ch.StatValues[WellKnownIds.Int] = ch.Int;
            ch.StatValues[WellKnownIds.Cha] = ch.Cha;

            ch.StatValues[WellKnownIds.Hp + ":cur"] = ch.HpCurrent;
            ch.StatValues[WellKnownIds.Hp + ":max"] = ch.HpMax;
            ch.StatValues[WellKnownIds.Ap + ":cur"] = ch.ApCurrent;
            ch.StatValues[WellKnownIds.Ap + ":max"] = ch.ApMax;

            foreach (var (specName, val) in ch.Proficiencies)
                ch.CheckValues[WellKnownIds.SpecId(specName)] = val;

            foreach (var skill in ch.Skills)
            {
                foreach (var cond in skill.Conditions)
                    if (string.IsNullOrEmpty(cond.FieldId))
                        cond.FieldId = SkillHelpers.LegacyStatId(cond.Stat);
                foreach (var fx in skill.Effects)
                    if (string.IsNullOrEmpty(fx.FieldId))
                        fx.FieldId = SkillHelpers.LegacyStatId(fx.Target);
            }

            ch.SheetMigrated = true;
            dirty = true;
        }
        Configuration.MigrationVersion = 1;
        Configuration.Save();
        if (dirty) Log.Info("Migrated legacy character stats to the modular sheet format.");
    }

    /// <summary>
    /// NOT a one-time migration: this seeds a per-party character copy for any party
    /// that doesn't have one yet (including parties joined later), so it must keep
    /// running on every load/login. It is idempotent via the ContainsKey check.
    /// </summary>
    private void MigrateToPartyCharacters()
    {
        if (Configuration.Parties.Count == 0) return;
        string? pid = LocalPlayerId;
        if (pid == null) return; // deferred to OnLogin if not yet logged in

        if (!Configuration.Characters.TryGetValue(pid, out var globalCh)) return;
        if (!globalCh.SheetMigrated) return; // wait for v1 migration first

        bool dirty = false;
        foreach (var party in Configuration.Parties)
        {
            string key = $"{party.Code}/{pid}";
            if (!Configuration.PartyCharacters.ContainsKey(key))
            {
                Configuration.PartyCharacters[key] = DeepCopyCharacter(globalCh);
                dirty = true;
            }
        }
        if (dirty) Configuration.Save();
    }

    private void OnSheetTemplateReceived(string partyCode, SheetTemplate template)
    {
        // Validate the template before persisting — every window iterates Groups/Fields,
        // so a malformed broadcast must not be able to poison the saved config.
        if (string.IsNullOrWhiteSpace(partyCode) || template?.Groups == null
            || template.Groups.Count > 100)
        {
            Log.Warning("[Net] Discarded malformed sheet template for party {0}.", partyCode ?? "?");
            return;
        }
        template.Groups.RemoveAll(g => g == null || g.Fields == null);
        foreach (var group in template.Groups)
        {
            if (group.Fields.Count > 200) group.Fields.RemoveRange(200, group.Fields.Count - 200);
            group.Fields.RemoveAll(f => f == null || string.IsNullOrEmpty(f.Id));
            foreach (var f in group.Fields)
            {
                f.Name    ??= string.Empty;
                f.Tooltip ??= string.Empty;
            }
        }

        Configuration.PartyTemplates[partyCode] = template;
        Configuration.Save();
    }

    private void OnDiceRollReceived(DiceRollBroadcastDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Message)) return;
        ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
        {
            Message = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
                .AddUiForeground("[RPDice] ", 32)
                .AddText($"{Truncate(dto.DisplayName, 64)}: {Truncate(dto.Message, 256)}")
                .Build(),
            Type = Dalamud.Game.Text.XivChatType.Echo,
        });
    }

    public void PushLocalProfile()
    {
        if (!Network.IsConnected) return;
        if ((DateTime.UtcNow - _lastProfilePush).TotalSeconds < 3) return;
        string? pid = LocalPlayerId;
        if (pid == null) return;
        _lastProfilePush = DateTime.UtcNow;
        var ch  = Configuration.ActivePartyCode != null
            ? GetOrCreatePartyCharacter(Configuration.ActivePartyCode, pid)
            : GetOrCreateCharacter(pid);
        var dto = BuildProfileDto(pid, LocalDisplayName, ch);
        Task.Run(() => Network.PushProfileAsync(dto));
    }

    // ── Player profile windows ────────────────────────────────────────────────

    // ── Party-scoped helpers ──────────────────────────────────────────────────

    public void SetActiveParty(string? code)
    {
        Configuration.ActivePartyCode = code;
        Configuration.Save();
        PushLocalProfile();
    }

    public RpCharacter GetOrCreatePartyCharacter(string partyCode, string playerId)
    {
        string key = $"{partyCode}/{playerId}";
        if (!Configuration.PartyCharacters.TryGetValue(key, out var ch))
        {
            // Seed from global character if present so existing stats carry over
            if (Configuration.Characters.TryGetValue(playerId, out var global))
                ch = DeepCopyCharacter(global);
            else
                ch = new RpCharacter();
            Configuration.PartyCharacters[key] = ch;
            Configuration.Save();
        }
        return ch;
    }

    public SheetTemplate GetPartyTemplate(string partyCode)
        => Configuration.PartyTemplates.TryGetValue(partyCode, out var t) ? t : SheetTemplate.Default();

    public void OpenSheetForParty(string partyCode)
    {
        Configuration.ActivePartyCode = partyCode;
        Configuration.Save();
        CharacterSheetWindow.OpenForParty(partyCode);
        CharacterSheetWindow.IsOpen = true;
    }

    public void OpenTemplateEditorForParty(string partyCode)
    {
        CharacterSheetWindow.OpenTemplateEditorForParty(partyCode);
        CharacterSheetWindow.IsOpen = true;
    }

    private static RpCharacter DeepCopyCharacter(RpCharacter src) => new()
    {
        StatValues    = new Dictionary<string, int>(src.StatValues),
        CheckValues   = new Dictionary<string, bool>(src.CheckValues),
        Skills        = src.Skills.Select(s => new RpSkill
        {
            Id                = s.Id,
            Name              = s.Name,
            Description       = s.Description,
            Type              = s.Type,
            Cooldown          = s.Cooldown,
            Duration          = s.Duration,
            CooldownRemaining = 0,
            DurationRemaining = 0,
            TriggerOnTurnEnd  = s.TriggerOnTurnEnd,
            Conditions        = s.Conditions.Select(c => new SkillCondition
                { FieldId = c.FieldId, Stat = c.Stat, Op = c.Op, Value = c.Value }).ToList(),
            Effects           = s.Effects.Select(e => new SkillEffect
                { FieldId = e.FieldId, Target = e.Target, Op = e.Op, Value = e.Value }).ToList(),
        }).ToList(),
        SheetMigrated = true,
    };

    public void OpenPlayerSheet(string playerId, string displayName, string? partyCode = null)
    {
        if (_playerSheetWindows.TryGetValue(playerId, out var existing))
        {
            existing.IsOpen = true;
            return;
        }
        if (partyCode != null)
            _pendingSheetParty[playerId] = partyCode;
        _pendingSheetFetches.Add(playerId);
        Task.Run(() => Network.FetchProfileAsync(playerId));
    }

    public void OpenPlayerSkills(string playerId, string displayName)
    {
        if (_playerSkillsWindows.TryGetValue(playerId, out var existing))
        {
            existing.IsOpen = true;
            return;
        }
        _pendingSkillsFetches.Add(playerId);
        Task.Run(() => Network.FetchProfileAsync(playerId));
    }

    private void OnProfileReceived(CharacterProfileDto profile)
    {
        // Defensive: never let a malformed payload from the server corrupt local state
        if (profile == null || string.IsNullOrWhiteSpace(profile.PlayerId)
            || profile.StatValues == null || profile.CheckValues == null || profile.Skills == null
            || profile.StatValues.Count > 1000 || profile.CheckValues.Count > 1000
            || profile.Skills.Count > 200)
        {
            Log.Warning("[Net] Discarded malformed profile payload from server.");
            return;
        }

        string pid = profile.PlayerId;
        KnownOnlinePlayers.Add(pid);   // mark as online / profile cached
        bool openSheet  = _pendingSheetFetches.Remove(pid);
        bool openSkills = _pendingSkillsFetches.Remove(pid);
        _pendingSheetParty.Remove(pid, out string? sheetParty);

        if (_playerSheetWindows.TryGetValue(pid, out var existingSheet))
        {
            existingSheet.UpdateProfile(profile);
            if (openSheet) existingSheet.IsOpen = true;
        }
        else if (openSheet)
        {
            var win = new PlayerSheetWindow(this, profile, sheetParty, ClosePlayerSheetWindow);
            _playerSheetWindows[pid] = win;
            WindowSystem.AddWindow(win);
            win.IsOpen = true;
        }

        if (_playerSkillsWindows.TryGetValue(pid, out var existingSkills))
        {
            existingSkills.UpdateProfile(profile);
            if (openSkills) existingSkills.IsOpen = true;
        }
        else if (openSkills)
        {
            var win = new PlayerSkillsWindow(this, profile, ClosePlayerSkillsWindow);
            _playerSkillsWindows[pid] = win;
            WindowSystem.AddWindow(win);
            win.IsOpen = true;
        }
    }

    private void OnProfileFetchFailed(string playerId)
    {
        _pendingSheetFetches.Remove(playerId);
        _pendingSkillsFetches.Remove(playerId);
        // Don't spam chat for background party profile pre-fetches; only show if it was explicitly requested
    }

    private void ClosePlayerSheetWindow(string playerId)
    {
        if (_playerSheetWindows.Remove(playerId, out var win))
            WindowSystem.RemoveWindow(win);
    }

    private void ClosePlayerSkillsWindow(string playerId)
    {
        if (_playerSkillsWindows.Remove(playerId, out var win))
            WindowSystem.RemoveWindow(win);
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault target) return;
        if (target.TargetObject is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc) return;
        if (!Network.IsConnected) return;

        string? worldName = null;
        if (DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()
                       .TryGetRow(pc.HomeWorld.RowId, out var worldRow))
            worldName = worldRow.Name.ToString();
        if (worldName == null) return;

        string targetId    = $"{pc.Name}@{worldName}";
        string displayName = pc.Name.ToString();
        if (targetId == LocalPlayerId) return;

        string? ctxParty = Configuration.ActivePartyCode;
        args.AddMenuItem(new MenuItem
        {
            Name      = "Open Character Sheet",
            OnClicked = _ => OpenPlayerSheet(targetId, displayName, ctxParty),
        });
        args.AddMenuItem(new MenuItem
        {
            Name      = "Open Skills",
            OnClicked = _ => OpenPlayerSkills(targetId, displayName),
        });
    }

    public RpCharacter GetOrCreateCharacter(string playerId)
    {
        if (!Configuration.Characters.TryGetValue(playerId, out var ch))
        {
            ch = new RpCharacter();
            Configuration.Characters[playerId] = ch;
            Configuration.Save();
        }
        return ch;
    }
}
