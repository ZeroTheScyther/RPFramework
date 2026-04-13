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

    private bool _autoConnectPending;

    // Dynamic player profile windows (one per remote player ID)
    private readonly Dictionary<string, PlayerSheetWindow>  _playerSheetWindows  = new();
    private readonly Dictionary<string, PlayerSkillsWindow> _playerSkillsWindows = new();
    private readonly HashSet<string> _pendingSheetFetches  = new();
    private readonly HashSet<string> _pendingSkillsFetches = new();
    private DateTime _lastProfilePush = DateTime.MinValue;

    private HubWindow               HubWindow               { get; init; }
    internal InitiativeWindow       InitiativeWindow        { get; init; }
    private InventoryWindow         InventoryWindow         { get; init; }
    private BgmWindow               BgmWindow               { get; init; }
    private BgmPlayerWindow         BgmPlayerWindow         { get; init; }
    private TradeNotificationWindow TradeNotificationWindow { get; init; }
    private BagShareInviteWindow    BagShareInviteWindow    { get; init; }
    internal SettingsWindow         SettingsWindow          { get; init; }
    private CharacterSheetWindow    CharacterSheetWindow    { get; init; }
    private DiceRollerWindow        DiceRollerWindow        { get; init; }
    private SkillsWindow            SkillsWindow            { get; init; }
    public  BgmService              BgmService              { get; init; }
    public  NetworkService          Network                 { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Bags.Count == 0)
            Configuration.Bags.Add(new RpBag { Name = "Bag" });

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

        // Wire up network events
        Network.Connected              += OnConnected;
        Network.Reconnected            += OnConnected;
        Network.TradeOfferReceived     += OnTradeOfferReceived;
        Network.TradeItemReceived      += OnTradeItemReceived;
        Network.TradeAccepted          += OnTradeAccepted;
        Network.BagShareInviteReceived += OnBagShareInvite;
        Network.BagOperationApplied    += OnBagOperationApplied;
        Network.BagDissolved           += OnBagDissolved;
        Network.BagStateReceived       += OnBagStateReceived;
        Network.BagParticipantJoined   += OnBagParticipantJoined;
        Network.BagParticipantLeft     += OnBagParticipantLeft;
        Network.ProfileReceived        += OnProfileReceived;
        Network.ProfileFetchFailed     += OnProfileFetchFailed;
        Network.PartyInfoReceived      += OnPartyInfoReceived;
        Network.PartyMemberJoined      += OnPartyMemberJoined;
        Network.PartyMemberLeft        += OnPartyMemberLeft;
        Network.PartyDisbanded         += OnPartyDisbanded;
        Network.PartyMemberBgmChanged  += OnPartyMemberBgmChanged;
        Network.PartyMemberRoleChanged += OnPartyMemberRoleChanged;
        Network.BgmRoomDeleted         += OnBgmRoomDeleted;
        Network.PartyInitiativeStarted += OnPartyInitiativeStarted;
        Network.PartyInitiativeUpdated += OnPartyInitiativeUpdated;
        Network.PartyInitiativeEnded   += OnPartyInitiativeEnded;

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
        Network.TradeOfferReceived     -= OnTradeOfferReceived;
        Network.TradeItemReceived      -= OnTradeItemReceived;
        Network.TradeAccepted          -= OnTradeAccepted;
        Network.BagShareInviteReceived -= OnBagShareInvite;
        Network.BagOperationApplied    -= OnBagOperationApplied;
        Network.BagDissolved           -= OnBagDissolved;
        Network.BagStateReceived       -= OnBagStateReceived;
        Network.BagParticipantJoined   -= OnBagParticipantJoined;
        Network.BagParticipantLeft     -= OnBagParticipantLeft;
        Network.ProfileReceived        -= OnProfileReceived;
        Network.ProfileFetchFailed     -= OnProfileFetchFailed;
        Network.PartyInfoReceived      -= OnPartyInfoReceived;
        Network.PartyMemberJoined      -= OnPartyMemberJoined;
        Network.PartyMemberLeft        -= OnPartyMemberLeft;
        Network.PartyDisbanded         -= OnPartyDisbanded;
        Network.PartyMemberBgmChanged  -= OnPartyMemberBgmChanged;
        Network.PartyMemberRoleChanged -= OnPartyMemberRoleChanged;
        Network.BgmRoomDeleted         -= OnBgmRoomDeleted;
        Network.PartyInitiativeStarted -= OnPartyInitiativeStarted;
        Network.PartyInitiativeUpdated -= OnPartyInitiativeUpdated;
        Network.PartyInitiativeEnded   -= OnPartyInitiativeEnded;

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
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        BgmService.Update();

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
            CharacterSheetWindow.Toggle();
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

    private void OnIniCmd(string command, string args) => InitiativeWindow.Toggle();

    private void OnDiceCmd(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            DiceRollerWindow.Toggle();
        else
            DiceRollerWindow.RollFromCommand(args);
    }

    // ── Network lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Called on both initial connect and reconnect.
    /// Pushes local profile so party members get an immediate update.
    /// </summary>
    private void OnConnected()
    {
        // Push profile immediately — bypasses the throttle for the first push after connect
        _lastProfilePush = DateTime.MinValue;
        PushLocalProfile();
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
        if (Configuration.Bags.Count > 0)
        {
            Configuration.Bags[0].Items.Add(new RpItem
            {
                Name        = item.Name,
                Description = item.Description,
                IconId      = item.IconId,
                Amount      = item.Amount,
            });
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
        var bag = Configuration.Bags.Find(b => b.Id == dto.BagId);
        if (bag == null) return;
        bag.Items.Clear();
        bag.Items.AddRange(dto.Items.ConvertAll(DtoToItem));
        bag.Gil = dto.Gil;
        var sharedRef = Configuration.SharedBags.Find(r => r.BagId == dto.BagId);
        if (sharedRef != null) sharedRef.Version = dto.Version;
        Configuration.Save();
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
    }

    private void OnPartyDisbanded(string code)
    {
        PartyMembers.Remove(code);
        InitiativeStates.Remove(code);
        Configuration.Parties.RemoveAll(p => p.Code == code);
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

        // Auto-roll d24 + SPD bonus and submit
        var ch      = GetOrCreateCharacter(pid);
        int roll     = new Random().Next(1, 25);          // d24: 1–24
        int spdBonus = SkillHelpers.StatMod(ch.Spd);     // floor((SPD-10)/2), e.g. 20→+5, 10→+0
        Task.Run(() => Network.PartySubmitRollAsync(code, roll, spdBonus));

        InitiativeWindow.IsOpen = true;
    }

    private void OnPartyInitiativeUpdated(string code, InitiativeStateDto state)
    {
        InitiativeStates[code] = state;
        InitiativeWindow.IsOpen = true;
    }

    private void OnPartyInitiativeEnded(string code)
    {
        InitiativeStates.Remove(code);
        // Leave the window open so players can see the result; they close it manually
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

    public static RpItem DtoToItem(RpItemDto dto) => new()
    {
        Id          = dto.Id,
        Name        = dto.Name,
        Description = dto.Description,
        IconId      = dto.IconId,
        Amount      = dto.Amount,
    };

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
            ch.HpCurrent, ch.HpMax, ch.ApCurrent, ch.ApMax,
            ch.Str, ch.Dex, ch.Spd, ch.Con,
            ch.Mem, ch.Mtl, ch.Int, ch.Cha,
            ch.Proficiencies, skills);
    }

    public void PushLocalProfile()
    {
        if (!Network.IsConnected) return;
        if ((DateTime.UtcNow - _lastProfilePush).TotalSeconds < 3) return;
        string? pid = LocalPlayerId;
        if (pid == null) return;
        _lastProfilePush = DateTime.UtcNow;
        var dto = BuildProfileDto(pid, LocalDisplayName, GetOrCreateCharacter(pid));
        Task.Run(() => Network.PushProfileAsync(dto));
    }

    // ── Player profile windows ────────────────────────────────────────────────

    public void OpenPlayerSheet(string playerId, string displayName)
    {
        if (_playerSheetWindows.TryGetValue(playerId, out var existing))
        {
            existing.IsOpen = true;
            return;
        }
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
        string pid = profile.PlayerId;
        KnownOnlinePlayers.Add(pid);   // mark as online / profile cached
        bool openSheet  = _pendingSheetFetches.Remove(pid);
        bool openSkills = _pendingSkillsFetches.Remove(pid);

        if (_playerSheetWindows.TryGetValue(pid, out var existingSheet))
        {
            existingSheet.UpdateProfile(profile);
            if (openSheet) existingSheet.IsOpen = true;
        }
        else if (openSheet)
        {
            var win = new PlayerSheetWindow(profile, ClosePlayerSheetWindow);
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
            var win = new PlayerSkillsWindow(profile, ClosePlayerSkillsWindow);
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

        args.AddMenuItem(new MenuItem
        {
            Name      = "Open Character Sheet",
            OnClicked = _ => OpenPlayerSheet(targetId, displayName),
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
