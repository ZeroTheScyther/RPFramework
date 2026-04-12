using System.IO;
using System.Threading.Tasks;
using Dalamud.Game.Command;
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

    private const string CmdInventory      = "/rpinventory";
    private const string CmdInventoryShort = "/rpinv";
    private const string CmdBgm            = "/rpbgm";
    private const string DefaultServerUrl  = "https://rpframework.example.com";

    public Configuration   Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("RPFramework");

    private InventoryWindow         InventoryWindow         { get; init; }
    private BgmWindow               BgmWindow               { get; init; }
    private BgmPlayerWindow         BgmPlayerWindow         { get; init; }
    private TradeNotificationWindow TradeNotificationWindow { get; init; }
    private BagShareInviteWindow    BagShareInviteWindow    { get; init; }
    private SettingsWindow          SettingsWindow          { get; init; }
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

        WindowSystem.AddWindow(InventoryWindow);
        WindowSystem.AddWindow(BgmWindow);
        WindowSystem.AddWindow(BgmPlayerWindow);
        WindowSystem.AddWindow(TradeNotificationWindow);
        WindowSystem.AddWindow(BagShareInviteWindow);
        WindowSystem.AddWindow(SettingsWindow);

        // Wire up network events
        Network.TradeOfferReceived  += OnTradeOfferReceived;
        Network.TradeItemReceived   += OnTradeItemReceived;
        Network.TradeAccepted       += OnTradeAccepted;
        Network.BagShareInviteReceived += OnBagShareInvite;
        Network.BagOperationApplied += OnBagOperationApplied;
        Network.BagDissolved        += OnBagDissolved;
        Network.BagStateReceived    += OnBagStateReceived;

        CommandManager.AddHandler(CmdInventory,      new CommandInfo(OnInventoryCmd) { HelpMessage = "Opens the RP Inventory" });
        CommandManager.AddHandler(CmdInventoryShort, new CommandInfo(OnInventoryCmd) { HelpMessage = "Opens the RP Inventory" });
        CommandManager.AddHandler(CmdBgm,            new CommandInfo(OnBgmCmd)       { HelpMessage = "Opens the RP BGM music player" });

        PluginInterface.UiBuilder.Draw         += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   += InventoryWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += SettingsWindow.Toggle;
        Framework.Update                       += OnFrameworkUpdate;
        ClientState.Login                      += OnLogin;

        // Auto-connect if the server URL has been configured (i.e. not the default placeholder)
        if (Configuration.ServerUrl != DefaultServerUrl && LocalPlayerId != null)
        {
            string url = Configuration.ServerUrl, id = LocalPlayerId, name = LocalDisplayName;
            Task.Run(() => Network.ConnectAsync(url, id, name));
        }
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
        BgmService.Dispose();
        Network.Dispose();

        CommandManager.RemoveHandler(CmdInventory);
        CommandManager.RemoveHandler(CmdInventoryShort);
        CommandManager.RemoveHandler(CmdBgm);
    }

    private void OnFrameworkUpdate(IFramework framework) => BgmService.Update();

    private void OnLogin()
    {
        if (Configuration.ServerUrl == DefaultServerUrl || Network.IsConnected) return;
        string? id = LocalPlayerId;
        if (id == null) return;
        string url = Configuration.ServerUrl, name = LocalDisplayName;
        Task.Run(() => Network.ConnectAsync(url, id, name));
    }

    private void OnInventoryCmd(string command, string args) => InventoryWindow.Toggle();
    private void OnBgmCmd(string command, string args)       => BgmWindow.Toggle();

    // ── Network event handlers (all called on framework thread) ──────────────

    private void OnTradeOfferReceived(Models.Net.TradeOfferDto offer)
    {
        TradeNotificationWindow.AddOffer(offer);
        TradeNotificationWindow.IsOpen = true;
    }

    private void OnTradeAccepted(System.Guid offerId, bool isCopy)
    {
        // If isCopy == false, the sender must remove the item from their inventory.
        // The InventoryWindow handles removal via TradeNotificationWindow.PendingRemoval.
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
                if (existing != null) { existing.Name = op.Item.Name; existing.Amount = op.Item.Amount; }
                break;
            case Models.Net.BagOpType.Rename when op.NewName != null:
                bag.Name = op.NewName;
                break;
        }
        Configuration.Save();
    }

    private void OnBagDissolved(System.Guid bagId)
    {
        Configuration.Bags.RemoveAll(b => b.Id == bagId);
        Configuration.SharedBags.RemoveAll(r => r.BagId == bagId);
        Configuration.Save();
    }

    private void OnBagStateReceived(Models.Net.SharedBagDto dto)
    {
        var bag = Configuration.Bags.Find(b => b.Id == dto.BagId);
        if (bag == null) return;
        bag.Items.Clear();
        bag.Items.AddRange(dto.Items.ConvertAll(DtoToItem));
        Configuration.Save();
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
}
