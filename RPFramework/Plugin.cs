using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
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
    [PluginService] internal static IChatGui               ChatGui         { get; private set; } = null!;

    private const string CmdInventory      = "/rpinventory";
    private const string CmdInventoryShort = "/rpinv";
    private const string CmdBgm            = "/rpbgm";
    private const string CmdSettings       = "/rpsettings";
    private const string CmdSheet         = "/rpsheet";
    private const string CmdSheetShort    = "/rpcs";
    private const string CmdDice          = "/rpdice";
    private const string DefaultServerUrl = "https://rpframework.example.com";

    public Configuration   Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("RPFramework");

    /// <summary>
    /// In-memory participant display names, keyed by bag ID then player ID.
    /// Populated from server events; cleared on plugin unload.
    /// </summary>
    public readonly Dictionary<Guid, Dictionary<string, string>> BagParticipants = new();

    private bool _autoConnectPending;

    private InventoryWindow         InventoryWindow         { get; init; }
    private BgmWindow               BgmWindow               { get; init; }
    private BgmPlayerWindow         BgmPlayerWindow         { get; init; }
    private TradeNotificationWindow TradeNotificationWindow { get; init; }
    private BagShareInviteWindow    BagShareInviteWindow    { get; init; }
    private SettingsWindow          SettingsWindow          { get; init; }
    private CharacterSheetWindow    CharacterSheetWindow    { get; init; }
    private DiceRollerWindow        DiceRollerWindow        { get; init; }
    public  BgmService              BgmService              { get; init; }
    public  NetworkService          Network                 { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Bags.Count == 0)
            Configuration.Bags.Add(new Models.RpBag { Name = "Bag" });

        string bgmCacheDir = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "bgm_cache");
        BgmService = new BgmService(bgmCacheDir);
        Network    = new NetworkService();

        TradeNotificationWindow = new TradeNotificationWindow(this);
        BagShareInviteWindow    = new BagShareInviteWindow(this);
        SettingsWindow          = new SettingsWindow(this);
        InventoryWindow         = new InventoryWindow(this);
        BgmPlayerWindow         = new BgmPlayerWindow(this);
        BgmWindow               = new BgmWindow(this, BgmService, BgmPlayerWindow);
        CharacterSheetWindow    = new CharacterSheetWindow(this);
        DiceRollerWindow        = new DiceRollerWindow(this);

        WindowSystem.AddWindow(InventoryWindow);
        WindowSystem.AddWindow(BgmWindow);
        WindowSystem.AddWindow(BgmPlayerWindow);
        WindowSystem.AddWindow(TradeNotificationWindow);
        WindowSystem.AddWindow(BagShareInviteWindow);
        WindowSystem.AddWindow(SettingsWindow);
        WindowSystem.AddWindow(CharacterSheetWindow);
        WindowSystem.AddWindow(DiceRollerWindow);

        // Wire up network events
        Network.TradeOfferReceived  += OnTradeOfferReceived;
        Network.TradeItemReceived   += OnTradeItemReceived;
        Network.TradeAccepted       += OnTradeAccepted;
        Network.BagShareInviteReceived += OnBagShareInvite;
        Network.BagOperationApplied    += OnBagOperationApplied;
        Network.BagDissolved           += OnBagDissolved;
        Network.BagStateReceived       += OnBagStateReceived;
        Network.BagParticipantJoined   += OnBagParticipantJoined;
        Network.BagParticipantLeft     += OnBagParticipantLeft;

        CommandManager.AddHandler(CmdInventory,      new CommandInfo(OnInventoryCmd)  { HelpMessage = "Opens the RP Inventory" });
        CommandManager.AddHandler(CmdInventoryShort, new CommandInfo(OnInventoryCmd)  { HelpMessage = "Opens the RP Inventory" });
        CommandManager.AddHandler(CmdBgm,            new CommandInfo(OnBgmCmd)        { HelpMessage = "Opens the RP BGM music player" });
        CommandManager.AddHandler(CmdSettings,       new CommandInfo(OnSettingsCmd)   { HelpMessage = "Opens RPFramework Settings" });
        CommandManager.AddHandler(CmdSheet,          new CommandInfo(OnSheetCmd)      { HelpMessage = "Opens the RP Character Sheet" });
        CommandManager.AddHandler(CmdSheetShort,     new CommandInfo(OnSheetCmd)      { HelpMessage = "Opens the RP Character Sheet" });
        CommandManager.AddHandler(CmdDice,           new CommandInfo(OnDiceCmd)       { HelpMessage = "Opens dice roller, or rolls immediately: /rpdice [d4/d6/.../d20/dN]" });

        PluginInterface.UiBuilder.Draw         += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   += InventoryWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += SettingsWindow.Toggle;
        Framework.Update                       += OnFrameworkUpdate;
        ClientState.Login                      += OnLogin;

        // Auto-connect deferred to first framework tick (LocalPlayerId not safe to call in ctor)
        _autoConnectPending = Configuration.ServerUrl != DefaultServerUrl;
    }

    public void Dispose()
    {
        Network.TradeOfferReceived     -= OnTradeOfferReceived;
        Network.TradeItemReceived      -= OnTradeItemReceived;
        Network.TradeAccepted          -= OnTradeAccepted;
        Network.BagShareInviteReceived -= OnBagShareInvite;
        Network.BagOperationApplied    -= OnBagOperationApplied;
        Network.BagDissolved           -= OnBagDissolved;
        Network.BagStateReceived       -= OnBagStateReceived;
        Network.BagParticipantJoined   -= OnBagParticipantJoined;
        Network.BagParticipantLeft     -= OnBagParticipantLeft;

        Framework.Update                       -= OnFrameworkUpdate;
        ClientState.Login                      -= OnLogin;
        PluginInterface.UiBuilder.Draw         -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= InventoryWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= SettingsWindow.Toggle;

        WindowSystem.RemoveAllWindows();
        InventoryWindow.Dispose();
        BgmWindow.Dispose();
        BgmPlayerWindow.Dispose();
        TradeNotificationWindow.Dispose();
        BagShareInviteWindow.Dispose();
        SettingsWindow.Dispose();
        CharacterSheetWindow.Dispose();
        DiceRollerWindow.Dispose();
        BgmService.Dispose();
        Network.Dispose();

        CommandManager.RemoveHandler(CmdInventory);
        CommandManager.RemoveHandler(CmdInventoryShort);
        CommandManager.RemoveHandler(CmdBgm);
        CommandManager.RemoveHandler(CmdSettings);
        CommandManager.RemoveHandler(CmdSheet);
        CommandManager.RemoveHandler(CmdSheetShort);
        CommandManager.RemoveHandler(CmdDice);
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

    private void OnInventoryCmd(string command, string args)  => InventoryWindow.Toggle();
    private void OnBgmCmd(string command, string args)        => BgmWindow.Toggle();
    private void OnSettingsCmd(string command, string args)   => SettingsWindow.Toggle();
    private void OnSheetCmd(string command, string args)      => CharacterSheetWindow.Toggle();
    private void OnDiceCmd(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            DiceRollerWindow.Toggle();
        else
            DiceRollerWindow.RollFromCommand(args);
    }

    // ── Network event handlers (all called on framework thread) ──────────────

    private void OnTradeOfferReceived(Models.Net.TradeOfferDto offer)
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
                    // If the item came from a shared bag, sync the removal to all participants
                    if (bag.IsShared)
                        PublishBagOp(bag.Id, Models.Net.BagOpType.RemoveItem, itemId: itemId);
                    break;
                }
            }
        }
        TradeNotificationWindow.OnAccepted(offerId, isCopy);
    }

    private void OnTradeItemReceived(System.Guid offerId, Models.Net.RpItemDto item, bool isCopy)
    {
        // Add received item to first bag
        if (Configuration.Bags.Count > 0)
        {
            Configuration.Bags[0].Items.Add(new Models.RpItem
            {
                Name        = item.Name,
                Description = item.Description,
                IconId      = item.IconId,
                Amount      = item.Amount,
            });
            Configuration.Save();
        }
    }

    private void OnBagShareInvite(Models.Net.SharedBagDto bag, string fromId, string fromName)
    {
        BagShareInviteWindow.AddInvite(bag, fromId, fromName);
        BagShareInviteWindow.IsOpen = true;
    }

    private void OnBagOperationApplied(Models.Net.BagOperationDto op, long newVersion)
    {
        var bag = Configuration.Bags.Find(b => b.Id == op.BagId);
        if (bag == null) return;

        switch (op.OpType)
        {
            case Models.Net.BagOpType.AddItem when op.Item != null:
                if (!bag.Items.Exists(i => i.Id == op.Item.Id))
                    bag.Items.Add(DtoToItem(op.Item));
                break;
            case Models.Net.BagOpType.RemoveItem when op.ItemId.HasValue:
                bag.Items.RemoveAll(i => i.Id == op.ItemId.Value);
                break;
            case Models.Net.BagOpType.UpdateItem when op.Item != null:
                var existing = bag.Items.Find(i => i.Id == op.Item.Id);
                if (existing != null)
                {
                    existing.Name        = op.Item.Name;
                    existing.Description = op.Item.Description;
                    existing.IconId      = op.Item.IconId;
                    existing.Amount      = op.Item.Amount;
                }
                break;
            case Models.Net.BagOpType.Rename when op.NewName != null:
                bag.Name = op.NewName;
                break;
            case Models.Net.BagOpType.SetGil when op.Gil.HasValue:
                bag.Gil = op.Gil.Value;
                break;
        }

        var sharedRef = Configuration.SharedBags.Find(r => r.BagId == op.BagId);
        if (sharedRef != null) sharedRef.Version = newVersion;
        Configuration.Save();
    }

    private void OnBagDissolved(System.Guid bagId)
    {
        Configuration.Bags.RemoveAll(b => b.Id == bagId);
        Configuration.SharedBags.RemoveAll(r => r.BagId == bagId);
        BagParticipants.Remove(bagId);
        Configuration.Save();
    }

    private void OnBagParticipantJoined(System.Guid bagId, string playerId, string displayName)
    {
        if (!BagParticipants.TryGetValue(bagId, out var dict))
            BagParticipants[bagId] = dict = new Dictionary<string, string>();
        dict[playerId] = displayName;
    }

    private void OnBagParticipantLeft(System.Guid bagId, string playerId)
    {
        if (BagParticipants.TryGetValue(bagId, out var dict))
            dict.Remove(playerId);
    }

    private void OnBagStateReceived(Models.Net.SharedBagDto dto)
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

    /// <summary>
    /// Sends a BagApplyOperation to the server for the given shared bag.
    /// No-op if the bag is not registered as shared.
    /// </summary>
    public void PublishBagOp(Guid bagId, Models.Net.BagOpType opType,
        Models.Net.RpItemDto? item = null, Guid? itemId = null, string? newName = null, int? gil = null)
    {
        var sharedRef = Configuration.SharedBags.Find(r => r.BagId == bagId);
        if (sharedRef == null) return;
        var op = new Models.Net.BagOperationDto(bagId, sharedRef.Version, opType, item, itemId, newName, gil);
        Task.Run(() => Network.ApplyBagOperation(op));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    public static Models.RpItem DtoToItem(Models.Net.RpItemDto dto) => new()
    {
        Id          = dto.Id,
        Name        = dto.Name,
        Description = dto.Description,
        IconId      = dto.IconId,
        Amount      = dto.Amount,
    };

    /// <summary>Gets the local player's identity string "Name@World" for use with the relay server.</summary>
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

    public Models.RpCharacter GetOrCreateCharacter(string playerId)
    {
        if (!Configuration.Characters.TryGetValue(playerId, out var ch))
        {
            ch = new Models.RpCharacter();
            Configuration.Characters[playerId] = ch;
            Configuration.Save();
        }
        return ch;
    }
}
