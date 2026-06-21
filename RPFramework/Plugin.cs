using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using RPFramework.Contracts;
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

    private const string CmdHub = "/rphub", CmdIni = "/rpini", CmdInventory = "/rpinventory",
        CmdInventoryShort = "/rpinv", CmdBgm = "/rpbgm", CmdSettings = "/rpsettings",
        CmdSheet = "/rpsheet", CmdSheetShort = "/rpcs", CmdStats = "/rpstats", CmdDice = "/rpdice",
        CmdSkills = "/rpskills", CmdSkillsShort = "/rpsk", CmdEquipment = "/rpequipment",
        CmdCharacter = "/rpcharacter", CmdHelp = "/rphelp";
    private const string DefaultServerUrl = "https://rpframework.example.com";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("RPFramework");

    public RpStateStore    Store      { get; }
    public NetworkService  Network    { get; }
    public BgmService      BgmService { get; }
    public BgmCoordinator  Bgm        { get; }

    private HubWindow               HubWindow               { get; }
    internal InitiativeWindow       InitiativeWindow        { get; }
    private InventoryWindow         InventoryWindow         { get; }
    private BgmWindow               BgmWindow               { get; }
    private BgmPlayerWindow         BgmPlayerWindow         { get; }
    private TradeNotificationWindow TradeNotificationWindow { get; }
    private BagShareInviteWindow    BagShareInviteWindow    { get; }
    internal SettingsWindow         SettingsWindow          { get; }
    internal CharacterSheetWindow   CharacterSheetWindow    { get; }
    private DiceRollerWindow        DiceRollerWindow        { get; }
    internal SkillsWindow           SkillsWindow            { get; }
    internal RpCharacterWindow      RpCharacterWindow       { get; }
    private HelpWindow              HelpWindow              { get; }

    // Dynamic read-only windows for remote players, keyed by playerId
    private readonly Dictionary<string, PlayerSheetWindow>  _playerSheetWindows  = new();
    private readonly Dictionary<string, PlayerSkillsWindow> _playerSkillsWindows = new();

    // Open nested-bag windows, keyed by "{bagId}:{path}" so a bag-in-a-bag is its own window
    private readonly Dictionary<string, BagItemWindow> _bagWindows = new();

    private bool _autoConnectPending;

    public Plugin()
    {
        Configuration? loaded = null;
        try { loaded = PluginInterface.GetPluginConfig() as Configuration; }
        catch (Exception ex) { Log.Error(ex, "Failed to load configuration — starting with defaults."); }
        // Clean break: ignore pre-rewrite (v0) configs entirely.
        Configuration = (loaded is { Version: >= 1 }) ? loaded : new Configuration();
        if (string.IsNullOrEmpty(Configuration.IdentitySecret))
        {
            Configuration.IdentitySecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            Configuration.Save();
        }

        string bgmCacheDir = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "bgm_cache");
        BgmService = new BgmService(bgmCacheDir);
        Store      = new RpStateStore();
        Network    = new NetworkService(Store);
        Bgm        = new BgmCoordinator(Store, Network, BgmService, () => Configuration.BgmVolume);

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
        RpCharacterWindow       = new RpCharacterWindow(this);
        HelpWindow              = new HelpWindow();

        foreach (var w in new Window[]
        {
            HubWindow, InitiativeWindow, InventoryWindow, BgmWindow, BgmPlayerWindow,
            TradeNotificationWindow, BagShareInviteWindow, SettingsWindow, CharacterSheetWindow,
            DiceRollerWindow, SkillsWindow, RpCharacterWindow, HelpWindow,
        }) WindowSystem.AddWindow(w);

        Network.Connected              += OnConnected;
        Network.Reconnected            += OnConnected;
        Network.Disconnected           += OnDisconnected;
        Network.DiceRollReceived       += OnDiceRollReceived;
        Network.TradeOfferReceived     += OnTradeOfferReceived;
        Network.BagShareInviteReceived += OnBagShareInvite;
        Network.BagShareDeclined += OnBagShareDeclined;
        Network.ErrorReceived          += OnError;

        ContextMenu.OnMenuOpened += OnContextMenuOpened;

        AddCommand(CmdHub, OnHubCmd, "Opens RP Hub — connection and campaign management");
        AddCommand(CmdInventory, OnInventoryCmd, "Opens the RP Inventory");
        AddCommand(CmdInventoryShort, OnInventoryCmd, "Opens the RP Inventory");
        AddCommand(CmdBgm, OnBgmCmd, "Opens the RP BGM music player");
        AddCommand(CmdSettings, OnSettingsCmd, "Opens RPFramework Settings");
        AddCommand(CmdCharacter, OnCharacterCmd, "Opens the RP Character window");
        AddCommand(CmdSheet, OnSheetCmd, "Opens the RP Character window (Sheet tab)");
        AddCommand(CmdSheetShort, OnSheetCmd, "Opens the RP Character window (Sheet tab)");
        AddCommand(CmdStats, OnSheetCmd, "Opens the RP Character window (Sheet tab)");
        AddCommand(CmdDice, OnDiceCmd, "Opens dice roller, or rolls immediately: /rpdice [d4/.../dN]");
        AddCommand(CmdSkills, OnSkillsCmd, "Opens the RP Character window (Skills tab)");
        AddCommand(CmdSkillsShort, OnSkillsCmd, "Opens the RP Character window (Skills tab)");
        AddCommand(CmdEquipment, OnEquipmentCmd, "Opens the RP Character window (Equipment tab)");
        AddCommand(CmdIni, OnIniCmd, "Opens the RP Initiative tracker");
        AddCommand(CmdHelp, OnHelpCmd, "Opens the RPFramework help window");

        PluginInterface.UiBuilder.Draw         += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   += HubWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += SettingsWindow.Toggle;
        Framework.Update                       += OnFrameworkUpdate;
        ClientState.Login                      += OnLogin;

        _autoConnectPending = Configuration.ServerUrl != DefaultServerUrl;
    }

    private static void AddCommand(string cmd, IReadOnlyCommandInfo.HandlerDelegate handler, string help)
        => CommandManager.AddHandler(cmd, new CommandInfo(handler) { HelpMessage = help });

    public void Dispose()
    {
        Network.Connected              -= OnConnected;
        Network.Reconnected            -= OnConnected;
        Network.Disconnected           -= OnDisconnected;
        Network.DiceRollReceived       -= OnDiceRollReceived;
        Network.TradeOfferReceived     -= OnTradeOfferReceived;
        Network.BagShareInviteReceived -= OnBagShareInvite;
        Network.BagShareDeclined -= OnBagShareDeclined;
        Network.ErrorReceived          -= OnError;
        ContextMenu.OnMenuOpened       -= OnContextMenuOpened;

        foreach (var w in _playerSheetWindows.Values)  w.Dispose();
        foreach (var w in _playerSkillsWindows.Values) w.Dispose();

        Framework.Update                       -= OnFrameworkUpdate;
        ClientState.Login                      -= OnLogin;
        PluginInterface.UiBuilder.Draw         -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= HubWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= SettingsWindow.Toggle;

        WindowSystem.RemoveAllWindows();
        foreach (var w in new IDisposable[]
        {
            HubWindow, InitiativeWindow, InventoryWindow, BgmWindow, BgmPlayerWindow,
            TradeNotificationWindow, BagShareInviteWindow, SettingsWindow, CharacterSheetWindow,
            DiceRollerWindow, SkillsWindow, RpCharacterWindow, BgmService, Network,
        }) w.Dispose();

        foreach (var cmd in new[]
        {
            CmdHub, CmdInventory, CmdInventoryShort, CmdBgm, CmdSettings, CmdSheet, CmdSheetShort,
            CmdStats, CmdDice, CmdSkills, CmdSkillsShort, CmdEquipment, CmdCharacter, CmdIni, CmdHelp,
        }) CommandManager.RemoveHandler(cmd);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Identity + connection
    // ═════════════════════════════════════════════════════════════════════════

    public string? LocalPlayerId
    {
        get
        {
            var player = ObjectTable.LocalPlayer;
            if (player == null) return null;
            if (!DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()
                            .TryGetRow(player.HomeWorld.RowId, out var worldRow)) return null;
            return $"{player.Name}@{worldRow.Name}";
        }
    }

    public string LocalDisplayName => ObjectTable.LocalPlayer?.Name.ToString() ?? "Unknown";

    public void Connect()
    {
        string? id = LocalPlayerId;
        if (id == null) return;
        string url = Configuration.ServerUrl, secret = Configuration.IdentitySecret, name = LocalDisplayName;
        Task.Run(() => Network.ConnectAsync(url, id, secret, name));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        BgmService.Update();
        Bgm.Tick(LocalPlayerId);
        if (_autoConnectPending && !Network.IsConnected && LocalPlayerId != null)
        {
            _autoConnectPending = false;
            Connect();
        }
    }

    private void OnLogin()
    {
        if (Configuration.ServerUrl != DefaultServerUrl && !Network.IsConnected)
            Connect();
    }

    private void OnConnected()
    {
        // Restore the last active campaign once hydration lands; falls back to the personal scope.
        if (Configuration.ActiveCampaignCode != null)
            Store.ActiveCampaign = Configuration.ActiveCampaignCode;
        // BGM room membership is persistent server-side — the server re-adds us to our room groups
        // on Identify, so there's nothing to re-join here.
    }

    private void OnDisconnected() => Store.Clear();

    // ═════════════════════════════════════════════════════════════════════════
    // Active campaign (the scope the player embodies)
    // ═════════════════════════════════════════════════════════════════════════

    public PartyDto? PersonalParty => Store.Parties.FirstOrDefault(p => p.IsPersonal);

    /// <summary>The active campaign code — restored from config, else the personal scope. Null before hydration.</summary>
    public string? ActiveCampaign
    {
        get
        {
            var code = Store.ActiveCampaign ?? Configuration.ActiveCampaignCode;
            if (code != null && Store.Party(code) != null) return code;
            return PersonalParty?.Code;
        }
    }

    public void SetActiveCampaign(string code)
    {
        Store.ActiveCampaign = code;
        Configuration.ActiveCampaignCode = code;
        Configuration.Save();
    }

    public SheetTemplate ActiveTemplate => Store.TemplateOrDefault(ActiveCampaign);
    public CharacterDto? ActiveCharacter
        => ActiveCampaign != null && LocalPlayerId != null ? Store.Character(ActiveCampaign, LocalPlayerId) : null;

    public PartyRole? MyRole(string? code)
    {
        var party = Store.Party(code);
        var pid = LocalPlayerId;
        return party?.Members.FirstOrDefault(m => m.PlayerId == pid)?.Role;
    }

    public bool IsDm(string? code) => MyRole(code) is PartyRole.Owner or PartyRole.CoDm;

    public int InitiativeBonus(CharacterState state, SheetTemplate template)
        => StatMath.InitiativeBonus(state, template);

    // ═════════════════════════════════════════════════════════════════════════
    // Commands
    // ═════════════════════════════════════════════════════════════════════════

    private void OnHubCmd(string c, string a)       => HubWindow.Toggle();
    private void OnInventoryCmd(string c, string a) => InventoryWindow.Toggle();
    private void OnBgmCmd(string c, string a)       => BgmWindow.Toggle();
    private void OnSettingsCmd(string c, string a)  => SettingsWindow.Toggle();
    private void OnIniCmd(string c, string a)       => InitiativeWindow.Toggle();
    private void OnHelpCmd(string c, string a)      => HelpWindow.Toggle();

    private void OnCharacterCmd(string c, string args) => RpCharacterWindow.OpenTo(RpCharacterWindow.Tab.Profile);

    private void OnSheetCmd(string c, string args)
    {
        string target = args.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target)) RpCharacterWindow.OpenTo(RpCharacterWindow.Tab.Stats);
        else OpenPlayerSheet(target);
    }

    private void OnSkillsCmd(string c, string args)
    {
        string target = args.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target)) RpCharacterWindow.OpenTo(RpCharacterWindow.Tab.Skills);
        else OpenPlayerSkills(target);
    }

    private void OnEquipmentCmd(string c, string args) => RpCharacterWindow.OpenTo(RpCharacterWindow.Tab.Equipment);

    private void OnDiceCmd(string c, string args)
    {
        if (string.IsNullOrWhiteSpace(args)) DiceRollerWindow.Toggle();
        else DiceRollerWindow.RollFromCommand(args);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Transient network events
    // ═════════════════════════════════════════════════════════════════════════

    private void OnDiceRollReceived(DiceRollResultDto dto)
    {
        // ChatGui.Print injects into our OWN client log only (never sent to SQEX servers) — TOS-safe.
        ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
        {
            Message = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
                .AddUiForeground("[RPDice] ", 32)
                .AddText($"{dto.DisplayName}: {dto.Message}")
                .Build(),
            Type = Dalamud.Game.Text.XivChatType.Echo,
        });
    }

    private void OnTradeOfferReceived(TradeOfferDto offer)
    {
        TradeNotificationWindow.AddOffer(offer);
        TradeNotificationWindow.IsOpen = true;
    }

    private void OnBagShareInvite(BagShareInviteDto invite)
    {
        BagShareInviteWindow.AddInvite(invite);
        BagShareInviteWindow.IsOpen = true;
    }

    private void OnBagShareDeclined(BagShareDeclinedDto d)
    {
        // ChatGui.Print injects into our OWN client log only (never sent to SQEX servers) — TOS-safe.
        ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
        {
            Message = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
                .AddUiForeground("[RPInv] ", 32)
                .AddText($"{d.DeclinerDisplayName} declined to share \"{d.BagName}\".")
                .Build(),
            Type = Dalamud.Game.Text.XivChatType.Echo,
        });
    }

    private void OnError(string ctx, string msg) => Log.Warning("[Server] {0}: {1}", ctx, msg);

    // ═════════════════════════════════════════════════════════════════════════
    // Remote player windows (read straight from the store — no fetch needed)
    // ═════════════════════════════════════════════════════════════════════════

    public void OpenPlayerSheet(string playerId)
    {
        if (_playerSheetWindows.TryGetValue(playerId, out var existing)) { existing.IsOpen = true; return; }
        var win = new PlayerSheetWindow(this, playerId, id =>
        {
            if (_playerSheetWindows.Remove(id, out var w)) WindowSystem.RemoveWindow(w);
        });
        _playerSheetWindows[playerId] = win;
        WindowSystem.AddWindow(win);
        win.IsOpen = true;
    }

    /// <summary>Opens (or raises) a nested bag as its own window. Path is the chain of bag-item ids
    /// from the inventory root to the bag being opened.</summary>
    public void OpenBag(Guid bagId, Guid[] path, string name)
    {
        string key = $"{bagId}:{string.Join(",", path)}";
        if (_bagWindows.TryGetValue(key, out var existing)) { existing.IsOpen = true; return; }
        var win = new BagItemWindow(this, bagId, path, key, name, k =>
        {
            if (_bagWindows.Remove(k, out var w)) WindowSystem.RemoveWindow(w);
        });
        _bagWindows[key] = win;
        WindowSystem.AddWindow(win);
        win.IsOpen = true;
    }

    public void OpenPlayerSkills(string playerId)
    {
        if (_playerSkillsWindows.TryGetValue(playerId, out var existing)) { existing.IsOpen = true; return; }
        var win = new PlayerSkillsWindow(this, playerId, id =>
        {
            if (_playerSkillsWindows.Remove(id, out var w)) WindowSystem.RemoveWindow(w);
        });
        _playerSkillsWindows[playerId] = win;
        WindowSystem.AddWindow(win);
        win.IsOpen = true;
    }

    public void OpenSheetForParty(string code)
    {
        SetActiveCampaign(code);
        RpCharacterWindow.OpenTo(RpCharacterWindow.Tab.Stats);
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault target) return;
        if (target.TargetObject is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc) return;
        if (!Network.IsConnected) return;
        if (!DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()
                        .TryGetRow(pc.HomeWorld.RowId, out var worldRow)) return;

        string targetId = $"{pc.Name}@{worldRow.Name}";
        if (targetId == LocalPlayerId) return;
        // Only offer if we share a campaign with them (i.e. their character is in our store).
        if (Store.Character(ActiveCampaign ?? "", targetId) == null) return;

        args.AddMenuItem(new MenuItem { Name = "Open Character Sheet", OnClicked = _ => OpenPlayerSheet(targetId) });
        args.AddMenuItem(new MenuItem { Name = "Open Skills",          OnClicked = _ => OpenPlayerSkills(targetId) });
    }
}
