using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using RPFramework.Models;
using RPFramework.Models.Net;

namespace RPFramework.Windows;

public class InventoryWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Grid layout constants
    private const int   Cols     = 5;
    private const float SlotPx   = 40f;
    private const float PadPx    = 2f;
    private const int   MinSlots = 35;   // 5 × 7

    // Tab state
    private int  selectedTab      = 0;
    private int  _tabBarVersion   = 0;   // incremented on reorder to force ImGui to rebuild tab order
    private Guid _pendingSelectId = Guid.Empty; // bag to force-select on the next frame after reorder

    // Item context-menu state
    private RpItem? ctxItem;
    private int     ctxBagIdx;
    private bool    pendingCtxMenu;   // open item ctx popup at parent scope

    // Bag tab context-menu state
    private RpBag?  ctxTabBag;
    private int     ctxTabBagIdx;
    private bool    pendingTabCtxMenu; // open tab ctx popup at parent scope

    // Amount modal
    private bool amountModalOpen   = true;
    private bool pendingAmountModal;
    private int  editAmount;

    // Create/Edit item modal
    private bool       itemModalOpen   = true;
    private bool       pendingCreateModal;
    private bool       pendingEditModal;
    private bool       isEditMode;
    private int        modalBagIdx;
    private string     modalName      = string.Empty;
    private string     modalDesc      = string.Empty;
    private uint       modalIconId;
    private RpItemType modalItemType  = RpItemType.Normal;
    private int        modalCapacity  = 10;
    private string     iconQuery      = string.Empty;
    private string     lastIconQuery  = string.Empty;
    private List<(string Name, uint IconId)> iconResults = new();

    // Bag item popup state
    private RpItem?  _openBagItem;
    private RpBag?   _openBagParent;
    private bool     _pendingOpenBagPopup;
    private Vector2  _openBagItemPos;
    private RpItem?  _bagCtxItem;
    private bool     _pendingBagCtxMenu;

    // Create-bag modal
    private bool   bagModalOpen   = true;
    private bool   pendingCreateBag;
    private string newBagName     = string.Empty;

    // Rename-bag modal
    private bool   renameModalOpen = true;
    private bool   pendingRenameBag;
    private RpBag? renamingBag;
    private string renameBagName   = string.Empty;

    public InventoryWindow(Plugin plugin)
        : base("RP Inventory##RPFramework",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(256, 340),
            MaximumSize = new Vector2(900, 1200),
        };
        Size          = new Vector2(288, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // ─────────────────────────────────────────────────────────────────────────
    // Main draw
    // ─────────────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        // Flush pending popup opens at the very top of the frame so
        // BeginPopupModal / BeginPopup can find them later in the same frame.
        if (pendingCreateModal)    { itemModalOpen   = true; ImGui.OpenPopup("##rp_item");   pendingCreateModal    = false; }
        if (pendingEditModal)      { itemModalOpen   = true; ImGui.OpenPopup("##rp_item");   pendingEditModal      = false; }
        if (pendingCreateBag)      { bagModalOpen    = true; ImGui.OpenPopup("##rp_bag");    pendingCreateBag      = false; }
        if (pendingRenameBag)      { renameModalOpen = true; ImGui.OpenPopup("##rp_rename"); pendingRenameBag      = false; }
        if (pendingAmountModal)    { amountModalOpen = true; ImGui.OpenPopup("##rp_amount"); pendingAmountModal    = false; }
        if (_pendingOpenBagPopup)  { ImGui.OpenPopup("##rp_bagitem");                        _pendingOpenBagPopup  = false; }
        // Item ctx and tab ctx are opened here (parent scope) to avoid child-window popup scoping issues
        if (pendingCtxMenu)        { ImGui.OpenPopup("##rp_ctx");    pendingCtxMenu    = false; }
        if (pendingTabCtxMenu)     { ImGui.OpenPopup("##rp_tabctx"); pendingTabCtxMenu = false; }

        var bags = plugin.Configuration.Bags;
        if (selectedTab >= bags.Count) selectedTab = Math.Max(0, bags.Count - 1);

        DrawTabBar(bags);
        ImGui.Separator();

        // Reserve: gil row + separator + button row + separator
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float frameH  = ImGui.GetFrameHeightWithSpacing();
        float sepH    = spacing + 1f;
        float reserved = frameH + sepH + frameH + sepH;
        float gridH   = ImGui.GetContentRegionAvail().Y - reserved;

        using (var gridChild = ImRaii.Child("##rpgrid", new Vector2(0, gridH)))
        {
            if (gridChild)
            {
                if (bags.Count > 0)
                    DrawGrid(bags[selectedTab]);
            }
        }

        ImGui.Separator();
        DrawGilRow();
        ImGui.Separator();

        if (ImGui.Button("Create new item", new Vector2(-1, 0)))
            OpenCreate(selectedTab);

        DrawItemContextMenu(bags);
        DrawTabContextMenu(bags);
        DrawItemModal(bags);
        DrawCreateBagModal(bags);
        DrawRenameBagModal(bags);
        DrawAmountModal(bags);
        DrawBagItemPopup(bags);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tab bar
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawTabBar(List<RpBag> bags)
    {
        // The version suffix forces ImGui to rebuild its internal tab order when bags are reordered.
        // Without this, ImGui preserves its own visual ordering regardless of submission order.
        using var tabBar = ImRaii.TabBar($"##rptabs{_tabBarVersion}");
        if (!tabBar) return;

        for (int i = 0; i < bags.Count; i++)
        {
            var bag = bags[i];

            // Force-select the previously-selected bag on the first frame after a reorder
            var flags = (_pendingSelectId != Guid.Empty && bag.Id == _pendingSelectId)
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;
            if (flags == ImGuiTabItemFlags.SetSelected)
                _pendingSelectId = Guid.Empty;

            bool active = ImGui.BeginTabItem($"{bag.Name}##rpt{bag.Id}", flags);

            if (ImGui.IsItemHovered())
            {
                // Right-click → context popup
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    ctxTabBag         = bag;
                    ctxTabBagIdx      = i;
                    pendingTabCtxMenu = true;
                }

                // Tooltip for shared bags
                if (bag.IsShared)
                {
                    ImGui.BeginTooltip();
                    string ownerDisplay = bag.SharedOwner?.Split('@')[0] ?? "Unknown";
                    bool isOwner = bag.SharedOwner == plugin.LocalPlayerId;
                    if (isOwner)
                    {
                        ImGui.TextUnformatted("Shared bag (you are the owner)");
                        plugin.BagParticipants.TryGetValue(bag.Id, out var parts);
                        if (parts != null && parts.Count > 0)
                        {
                            ImGui.TextDisabled("Shared with:");
                            foreach (var kv in parts)
                                ImGui.TextUnformatted($"  {kv.Value}");
                        }
                        else
                        {
                            ImGui.TextDisabled("No participants yet");
                        }
                    }
                    else
                    {
                        ImGui.TextUnformatted($"Shared bag — Owner: {ownerDisplay}");
                    }
                    ImGui.EndTooltip();
                }
            }

            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            {
                byte[] dndPayload = BitConverter.GetBytes(i);
                ImGui.SetDragDropPayload("RPTAB"u8, dndPayload);
                ImGui.TextUnformatted(bag.Name);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var accepted = ImGui.AcceptDragDropPayload("RPTAB"u8);
                    if (!accepted.IsNull && accepted.Handle->DataSize == sizeof(int))
                    {
                        int srcIdx = BitConverter.ToInt32(accepted.Handle->DataSpan);
                        if (srcIdx != i)
                        {
                            _pendingSelectId = bags[selectedTab].Id;
                            var moved = bags[srcIdx];
                            bags.RemoveAt(srcIdx);
                            bags.Insert(i, moved);
                            selectedTab = bags.FindIndex(b => b.Id == _pendingSelectId);
                            if (selectedTab < 0) selectedTab = 0;
                            _tabBarVersion++;
                            plugin.Configuration.Save();
                        }
                    }
                }
                ImGui.EndDragDropTarget();
            }

            if (active)
            {
                selectedTab = i;
                ImGui.EndTabItem();
            }
        }

        if (ImGui.TabItemButton("+##rpaddtab",
            ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip))
        {
            newBagName = string.Empty;
            pendingCreateBag = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Gil row
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawGilRow()
    {
        var bags = plugin.Configuration.Bags;
        if (bags.Count == 0 || selectedTab >= bags.Count) return;
        var bag = bags[selectedTab];

        int gil = bag.Gil;
        float labelW = ImGui.CalcTextSize("Gil").X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - labelW);
        if (ImGui.InputInt("Gil##rpgil", ref gil, 0, 0))
        {
            if (gil < 0) gil = 0;
            bag.Gil = gil;
            plugin.Configuration.Save();
            if (bag.IsShared)
                plugin.PublishBagOp(bag.Id, RPFramework.Models.Net.BagOpType.SetGil, gil: gil);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Item grid
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawGrid(RpBag bag)
    {
        float sz  = SlotPx * ImGuiHelpers.GlobalScale;
        float pad = PadPx  * ImGuiHelpers.GlobalScale;

        int count = bag.Items.Count;
        // Always show at least MinSlots; expand in full-row increments,
        // with at least one empty slot after the last item.
        int total = Math.Max(MinSlots, ((count + Cols) / Cols) * Cols);

        for (int i = 0; i < total; i++)
        {
            if (i % Cols != 0) ImGui.SameLine(0, pad);

            if (i < count)
                DrawItemSlot(bag, i, sz);
            else
                DrawEmptySlot(sz);
        }
    }

    private void DrawItemSlot(RpBag bag, int idx, float sz)
    {
        var item = bag.Items[idx];
        var pos  = ImGui.GetCursorScreenPos();

        ImGui.PushID($"##rps{item.Id}");

        if (item.IconId > 0)
        {
            var tex  = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId));
            var wrap = tex.GetWrapOrEmpty();
            ImGui.ImageButton(wrap.Handle, new Vector2(sz, sz), Vector2.Zero, Vector2.One, 0, Vector4.Zero);
        }
        else
        {
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(pos, pos + new Vector2(sz, sz), 0xFF3D3D3D);
            dl.AddRect(pos,       pos + new Vector2(sz, sz), 0xFF606060);
            ImGui.InvisibleButton($"##rpib{item.Id}", new Vector2(sz, sz));
        }

        if (item.Type == RpItemType.Bag)
        {
            // Gold border + slot-usage badge for bag items
            var    dl    = ImGui.GetWindowDrawList();
            dl.AddRect(pos, pos + new Vector2(sz, sz), 0xFFD4AA00, 0, ImDrawFlags.None, 2f);
            string usage = $"{item.Contents.Count}/{item.Capacity}";
            var    tSz   = ImGui.CalcTextSize(usage);
            var    tPos  = pos + new Vector2(sz - tSz.X - 2f, sz - tSz.Y - 1f);
            dl.AddText(tPos + new Vector2(1, 1), 0xCC000000, usage);
            dl.AddText(tPos, 0xFFD4AA00, usage);
        }
        else if (item.Amount > 1)
        {
            // Amount badge (bottom-right corner) for normal items
            var    dl    = ImGui.GetWindowDrawList();
            string amt   = item.Amount.ToString();
            var    tSz   = ImGui.CalcTextSize(amt);
            var    tPos  = pos + new Vector2(sz - tSz.X - 2f, sz - tSz.Y - 1f);
            dl.AddText(tPos + new Vector2(1, 1), 0xCC000000, amt);
            dl.AddText(tPos, 0xFFFFFFFF, amt);
        }

        // Hover tooltip
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(item.Name);
            if (item.Type == RpItemType.Bag)
                ImGui.TextDisabled($"Bag  {item.Contents.Count}/{item.Capacity} slots used");
            else
                ImGui.TextDisabled($"x{item.Amount}");
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                ImGui.Separator();
                ImGui.PushTextWrapPos(240f * ImGuiHelpers.GlobalScale);
                ImGui.TextUnformatted(item.Description);
                ImGui.PopTextWrapPos();
            }
            ImGui.EndTooltip();
        }

        // Right-click → set pending flag; popup is opened at parent scope to avoid
        // child-window popup scoping issues.
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            ctxItem        = item;
            ctxBagIdx      = selectedTab;
            pendingCtxMenu = true;
        }

        ImGui.PopID();
    }

    private static void DrawEmptySlot(float sz)
    {
        var pos = ImGui.GetCursorScreenPos();
        var dl  = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(sz, sz), 0xFF2A2A2A);
        dl.AddRect(pos,       pos + new Vector2(sz, sz), 0xFF484848);
        ImGui.Dummy(new Vector2(sz, sz));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Item context menu  (opened at parent scope — see Draw())
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawItemContextMenu(List<RpBag> bags)
    {
        if (ctxItem == null) return;
        if (!ImGui.BeginPopup("##rp_ctx")) return;

        ImGui.TextDisabled(ctxItem.Name);
        ImGui.Separator();

        // "Open" only for bag items
        if (ctxItem.Type == RpItemType.Bag)
        {
            if (ImGui.MenuItem("Open##rpctxopen"))
            {
                _openBagItem        = ctxItem;
                _openBagParent      = bags[ctxBagIdx];
                _openBagItemPos     = ImGui.GetMousePos();
                _pendingOpenBagPopup = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.Separator();
        }

        if (ImGui.MenuItem("Edit"))
        {
            OpenEdit(ctxItem, ctxBagIdx);
            ImGui.CloseCurrentPopup();
        }

        // Amount only makes sense for normal items
        if (ctxItem.Type == RpItemType.Normal && ImGui.MenuItem("Amount"))
        {
            editAmount = ctxItem.Amount;
            pendingAmountModal = true;
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.MenuItem("Copy"))
        {
            var copyBag    = bags[ctxBagIdx];
            var copiedItem = new RpItem
            {
                Name        = ctxItem.Name,
                Description = ctxItem.Description,
                IconId      = ctxItem.IconId,
                Amount      = ctxItem.Amount,
                Type        = ctxItem.Type,
                Capacity    = ctxItem.Capacity,
                Contents    = ctxItem.Contents.ConvertAll(c => new RpItem
                {
                    Name        = c.Name,
                    Description = c.Description,
                    IconId      = c.IconId,
                    Amount      = c.Amount,
                }),
            };
            copyBag.Items.Add(copiedItem);
            plugin.Configuration.Save();
            if (copyBag.IsShared)
                plugin.PublishBagOp(copyBag.Id, RPFramework.Models.Net.BagOpType.AddItem, item: Plugin.ItemToDto(copiedItem));
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.BeginMenu("Move to"))
        {
            bool any = false;
            for (int i = 0; i < bags.Count; i++)
            {
                if (i == ctxBagIdx) continue;
                any = true;
                if (ImGui.MenuItem(bags[i].Name))
                {
                    var srcBag    = bags[ctxBagIdx];
                    var destBag   = bags[i];
                    var movedItem = ctxItem!;
                    srcBag.Items.Remove(movedItem);
                    destBag.Items.Add(movedItem);
                    plugin.Configuration.Save();
                    if (srcBag.IsShared)
                        plugin.PublishBagOp(srcBag.Id, RPFramework.Models.Net.BagOpType.RemoveItem, itemId: movedItem.Id);
                    if (destBag.IsShared)
                        plugin.PublishBagOp(destBag.Id, RPFramework.Models.Net.BagOpType.AddItem, item: Plugin.ItemToDto(movedItem));
                    ctxItem = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            if (!any) ImGui.TextDisabled("No other bags");
            ImGui.EndMenu();
        }

        // "Put in bag" — for normal items only, when a bag item with free slots exists
        if (ctxItem?.Type == RpItemType.Normal)
        {
            var bagItemsWithSpace = bags[ctxBagIdx].Items
                .FindAll(b => b.Type == RpItemType.Bag && b.Contents.Count < b.Capacity && b != ctxItem);
            if (bagItemsWithSpace.Count > 0 && ImGui.BeginMenu("Put in bag##rpputinbag"))
            {
                foreach (var bagItem in bagItemsWithSpace)
                {
                    if (ImGui.MenuItem($"{bagItem.Name}  ({bagItem.Contents.Count}/{bagItem.Capacity})##rpputin{bagItem.Id}"))
                    {
                        var srcBag    = bags[ctxBagIdx];
                        var movedItem = ctxItem!;
                        srcBag.Items.Remove(movedItem);
                        bagItem.Contents.Add(movedItem);
                        plugin.Configuration.Save();
                        if (srcBag.IsShared)
                        {
                            plugin.PublishBagOp(srcBag.Id, RPFramework.Models.Net.BagOpType.RemoveItem, itemId: movedItem.Id);
                            plugin.PublishBagOp(srcBag.Id, RPFramework.Models.Net.BagOpType.UpdateItem, item: Plugin.ItemToDto(bagItem));
                        }
                        ctxItem = null;
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.EndMenu();
            }
        }

        ImGui.Separator();

        DrawTradeMenuItems(bags);

        ImGui.Separator();

        if (ImGui.MenuItem("Discard"))
        {
            var discardBag = bags[ctxBagIdx];
            var discardId  = ctxItem!.Id;
            discardBag.Items.Remove(ctxItem!);
            plugin.Configuration.Save();
            if (discardBag.IsShared)
                plugin.PublishBagOp(discardBag.Id, RPFramework.Models.Net.BagOpType.RemoveItem, itemId: discardId);
            ctxItem = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawTradeMenuItems(List<RpBag> bags)
    {
        bool connected = plugin.Network.IsConnected;
        string? targetId = GetTargetPlayerId();
        bool canTrade = connected && targetId != null && ctxItem != null;

        if (!canTrade) ImGui.BeginDisabled();

        if (ImGui.MenuItem("Trade##rptrade"))
        {
            var item = ctxItem!;
            var dto  = Plugin.ItemToDto(item);
            Task.Run(() => plugin.Network.SendTradeOffer(targetId!, dto, isCopy: false));
            // Optimistically mark as pending removal — actual removal happens on OnTradeAccepted
            ctxItem   = null;
            ImGui.CloseCurrentPopup();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canTrade ? $"Trade to {targetId}" : GetTradeTooltip(connected, targetId));

        if (ImGui.MenuItem("Send a Copy##rpsendcopy"))
        {
            var item = ctxItem!;
            var dto  = Plugin.ItemToDto(item);
            Task.Run(() => plugin.Network.SendTradeOffer(targetId!, dto, isCopy: true));
            ctxItem   = null;
            ImGui.CloseCurrentPopup();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canTrade ? $"Send copy to {targetId}" : GetTradeTooltip(connected, targetId));

        if (!canTrade) ImGui.EndDisabled();
    }

    private static string GetTradeTooltip(bool connected, string? targetId)
    {
        if (!connected)           return "Not connected to server";
        if (targetId == null)     return "Target a player with RPFramework";
        return string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Bag tab context menu  (Rename / Delete)
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawTabContextMenu(List<RpBag> bags)
    {
        if (ctxTabBag == null) return;
        if (!ImGui.BeginPopup("##rp_tabctx")) return;

        // Capture the bag object locally. Use IndexOf to get its CURRENT position in the
        // list — ctxTabBagIdx may be stale if the list changed since the right-click.
        var bag = ctxTabBag;
        int bagIdx = bags.IndexOf(bag);
        if (bagIdx < 0) { ImGui.EndPopup(); ctxTabBag = null; return; }

        ImGui.TextDisabled(bag.Name);
        ImGui.Separator();

        bool canMoveLeft  = bagIdx > 0;
        bool canMoveRight = bagIdx < bags.Count - 1;

        if (!canMoveLeft) ImGui.BeginDisabled();
        if (ImGui.MenuItem("Move left##rptableft"))
        {
            _pendingSelectId = bags[selectedTab].Id;
            (bags[bagIdx - 1], bags[bagIdx]) = (bags[bagIdx], bags[bagIdx - 1]);
            if (selectedTab == bagIdx) selectedTab = bagIdx - 1;
            else if (selectedTab == bagIdx - 1) selectedTab = bagIdx;
            _tabBarVersion++;
            plugin.Configuration.Save();
            ImGui.CloseCurrentPopup();
        }
        if (!canMoveLeft) ImGui.EndDisabled();

        if (!canMoveRight) ImGui.BeginDisabled();
        if (ImGui.MenuItem("Move right##rptabright"))
        {
            _pendingSelectId = bags[selectedTab].Id;
            (bags[bagIdx], bags[bagIdx + 1]) = (bags[bagIdx + 1], bags[bagIdx]);
            if (selectedTab == bagIdx) selectedTab = bagIdx + 1;
            else if (selectedTab == bagIdx + 1) selectedTab = bagIdx;
            _tabBarVersion++;
            plugin.Configuration.Save();
            ImGui.CloseCurrentPopup();
        }
        if (!canMoveRight) ImGui.EndDisabled();

        ImGui.Separator();

        if (ImGui.MenuItem("Rename"))
        {
            renamingBag      = bag;
            renameBagName    = bag.Name;
            pendingRenameBag = true;
            ImGui.CloseCurrentPopup();
        }

        ImGui.Separator();

        // Sharing — only for non-shared bags
        if (!bag.IsShared)
        {
            bool connected   = plugin.Network.IsConnected;
            string? targetId = GetTargetPlayerId();
            bool canShare    = connected && targetId != null;

            if (!canShare) ImGui.BeginDisabled();
            if (ImGui.MenuItem("Share with target##rpbagshare"))
            {
                var dto = new SharedBagDto(
                    bag.Id,
                    bag.Name,
                    plugin.LocalPlayerId ?? string.Empty,
                    bag.Items.ConvertAll(Plugin.ItemToDto),
                    0L,
                    bag.Gil);
                bag.SharedOwner = plugin.LocalPlayerId;
                plugin.Configuration.SharedBags.Add(new SharedBagRef
                    { BagId = bag.Id, OwnerPlayerId = plugin.LocalPlayerId ?? string.Empty, IsOwner = true });
                plugin.Configuration.Save();
                Task.Run(() => plugin.Network.ShareBag(targetId!, dto));
                ImGui.CloseCurrentPopup();
            }
            if (!canShare && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!connected ? "Not connected to server" : "Target a player with RPFramework");
            if (!canShare) ImGui.EndDisabled();
        }
        else
        {
            bool isOwner = plugin.LocalPlayerId == bag.SharedOwner;
            if (isOwner)
            {
                if (ImGui.MenuItem("Dissolve shared bag##rpbagdissolve"))
                {
                    Task.Run(() => plugin.Network.DissolveBag(bag.Id));
                    // OnBagDissolved handler will remove it from config
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Removes bag for all participants");
            }
            else
            {
                if (ImGui.MenuItem("Disconnect from shared bag##rpbagdisconn"))
                {
                    bags.RemoveAll(b => b.Id == bag.Id);
                    plugin.Configuration.SharedBags.RemoveAll(r => r.BagId == bag.Id);
                    if (selectedTab >= bags.Count) selectedTab = bags.Count - 1;
                    plugin.Configuration.Save();
                    Task.Run(() => plugin.Network.DisconnectFromBag(bag.Id));
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.Separator();

        bool canDeleteBag = bags.Count > 1 && !bag.IsShared;
        if (!canDeleteBag) ImGui.BeginDisabled();
        if (ImGui.MenuItem("Delete"))
        {
            bags.Remove(bag);
            if (selectedTab >= bags.Count) selectedTab = bags.Count - 1;
            plugin.Configuration.Save();
            ImGui.CloseCurrentPopup();
        }
        if (!canDeleteBag) ImGui.EndDisabled();
        if (!canDeleteBag && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(bags.Count <= 1 ? "Cannot delete the last bag" : "Dissolve or disconnect a shared bag instead");

        ImGui.EndPopup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Create / Edit item modal
    // ─────────────────────────────────────────────────────────────────────────

    private void OpenCreate(int bagIdx)
    {
        isEditMode     = false;
        modalBagIdx    = bagIdx;
        modalName      = string.Empty;
        modalDesc      = string.Empty;
        modalIconId    = 0;
        modalItemType  = RpItemType.Normal;
        modalCapacity  = 10;
        ResetIconSearch();
        pendingCreateModal = true;
    }

    private void OpenEdit(RpItem item, int bagIdx)
    {
        isEditMode     = true;
        modalBagIdx    = bagIdx;
        modalName      = item.Name;
        modalDesc      = item.Description;
        modalIconId    = item.IconId;
        modalItemType  = item.Type;
        modalCapacity  = item.Capacity;
        ResetIconSearch();
        pendingEditModal = true;
    }

    private void ResetIconSearch()
    {
        iconQuery     = string.Empty;
        lastIconQuery = string.Empty;
        iconResults.Clear();
    }

    private void DrawItemModal(List<RpBag> bags)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        float wItem = 420 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSizeConstraints(new Vector2(wItem, 0), new Vector2(wItem, float.MaxValue));

        if (!ImGui.BeginPopupModal("##rp_item", ref itemModalOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        ImGui.TextUnformatted(isEditMode ? "Edit Item" : "Create New Item");
        ImGui.Separator();

        // Type selector
        ImGui.TextUnformatted("Type");
        ImGui.SetNextItemWidth(-1);
        int typeIdx = (int)modalItemType;
        if (ImGui.Combo("##rpmodaltype", ref typeIdx, new[] { "Normal", "Bag" }, 2))
            modalItemType = (RpItemType)typeIdx;

        if (modalItemType == RpItemType.Bag)
        {
            ImGui.TextUnformatted("Capacity");
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("##rpmodalcap", ref modalCapacity, 1, 4);
            if (modalCapacity < 1)   modalCapacity = 1;
            if (modalCapacity > 256) modalCapacity = 256;
        }

        ImGui.Separator();

        // Name
        ImGui.TextUnformatted("Name");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##rpmodname", ref modalName, 64);

        // Description
        ImGui.TextUnformatted("Description");
        ImGui.InputTextMultiline("##rpmoddesc", ref modalDesc, 512,
            new Vector2(-1, 72 * ImGuiHelpers.GlobalScale));

        ImGui.Separator();

        // Current icon preview
        ImGui.TextUnformatted("Icon");
        if (modalIconId > 0)
        {
            var previewTex  = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(modalIconId));
            var previewWrap = previewTex.GetWrapOrEmpty();
            ImGui.Image(previewWrap.Handle, new Vector2(32, 32) * ImGuiHelpers.GlobalScale);
            ImGui.SameLine();
            ImGui.TextUnformatted($"#{modalIconId}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                modalIconId = 0;
        }
        else
        {
            ImGui.TextDisabled("No icon selected");
        }

        // Icon search box
        ImGui.TextUnformatted("Search icons by item name:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##rpiconsearch", ref iconQuery, 64))
        {
            if (iconQuery != lastIconQuery)
            {
                lastIconQuery = iconQuery;
                iconResults   = SearchIcons(iconQuery);
            }
        }

        // Icon results grid
        float iconSz = 28f * ImGuiHelpers.GlobalScale;
        using (var results = ImRaii.Child("##rpiconresults",
               new Vector2(-1, 130 * ImGuiHelpers.GlobalScale), true))
        {
            if (results)
            {
                if (string.IsNullOrEmpty(iconQuery))
                {
                    ImGui.TextDisabled("Type to search...");
                }
                else if (iconResults.Count == 0)
                {
                    ImGui.TextDisabled("No results");
                }
                else
                {
                    for (int i = 0; i < iconResults.Count; i++)
                    {
                        var (name, iconId) = iconResults[i];
                        if (i % 9 != 0) ImGui.SameLine(0, 2);

                        var tex  = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
                        var wrap = tex.GetWrapOrEmpty();

                        bool selected = modalIconId == iconId;
                        if (selected)
                            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.46f, 0.88f, 1f));

                        ImGui.PushID($"##rpicn{i}");
                        if (ImGui.ImageButton(wrap.Handle, new Vector2(iconSz, iconSz)))
                            modalIconId = iconId;
                        ImGui.PopID();

                        if (selected) ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(name);
                    }
                }
            }
        }

        ImGui.Separator();

        bool canSubmit = !string.IsNullOrWhiteSpace(modalName);

        if (!canSubmit) ImGui.BeginDisabled();
        if (ImGui.Button(isEditMode ? "Save##rpmodsave" : "Create##rpmodcreate", new Vector2(120, 0)))
        {
            var modalBag = bags[modalBagIdx];
            if (isEditMode && ctxItem != null)
            {
                ctxItem.Name        = modalName.Trim();
                ctxItem.Description = modalDesc.Trim();
                ctxItem.IconId      = modalIconId;
                ctxItem.Type        = modalItemType;
                ctxItem.Capacity    = modalCapacity;
                plugin.Configuration.Save();
                if (modalBag.IsShared)
                    plugin.PublishBagOp(modalBag.Id, RPFramework.Models.Net.BagOpType.UpdateItem, item: Plugin.ItemToDto(ctxItem));
            }
            else
            {
                var newItem = new RpItem
                {
                    Name        = modalName.Trim(),
                    Description = modalDesc.Trim(),
                    IconId      = modalIconId,
                    Amount      = 1,
                    Type        = modalItemType,
                    Capacity    = modalCapacity,
                };
                modalBag.Items.Add(newItem);
                plugin.Configuration.Save();
                if (modalBag.IsShared)
                    plugin.PublishBagOp(modalBag.Id, RPFramework.Models.Net.BagOpType.AddItem, item: Plugin.ItemToDto(newItem));
            }
            ImGui.CloseCurrentPopup();
        }
        if (!canSubmit) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel##rpmodcancel", new Vector2(120, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Create bag modal
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawCreateBagModal(List<RpBag> bags)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        float wBag = 280 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSizeConstraints(new Vector2(wBag, 0), new Vector2(wBag, float.MaxValue));

        if (!ImGui.BeginPopupModal("##rp_bag", ref bagModalOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        ImGui.TextUnformatted("New Bag Name");
        ImGui.Separator();

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        bool enter = ImGui.InputText("##rpbagname", ref newBagName, 32,
            ImGuiInputTextFlags.EnterReturnsTrue);

        bool canCreate = !string.IsNullOrWhiteSpace(newBagName);
        if (!canCreate) ImGui.BeginDisabled();
        if ((ImGui.Button("Create##rpbagcreate", new Vector2(96, 0)) || enter) && canCreate)
        {
            bags.Add(new RpBag { Name = newBagName.Trim() });
            selectedTab = bags.Count - 1;
            plugin.Configuration.Save();
            ImGui.CloseCurrentPopup();
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel##rpbagcancel", new Vector2(96, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rename bag modal
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawRenameBagModal(List<RpBag> bags)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        float wRename = 280 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSizeConstraints(new Vector2(wRename, 0), new Vector2(wRename, float.MaxValue));

        if (!ImGui.BeginPopupModal("##rp_rename", ref renameModalOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        ImGui.TextUnformatted("Rename Bag");
        ImGui.Separator();

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        bool enter = ImGui.InputText("##rprenameval", ref renameBagName, 32,
            ImGuiInputTextFlags.EnterReturnsTrue);

        bool canRename = !string.IsNullOrWhiteSpace(renameBagName);
        if (!canRename) ImGui.BeginDisabled();
        if ((ImGui.Button("Rename##rprenameok", new Vector2(96, 0)) || enter) && canRename && renamingBag != null)
        {
            renamingBag.Name = renameBagName.Trim();
            plugin.Configuration.Save();
            ImGui.CloseCurrentPopup();
        }
        if (!canRename) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel##rprenamecancel", new Vector2(96, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Amount modal
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawAmountModal(List<RpBag> bags)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        float wAmount = 220 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSizeConstraints(new Vector2(wAmount, 0), new Vector2(wAmount, float.MaxValue));

        if (!ImGui.BeginPopupModal("##rp_amount", ref amountModalOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        ImGui.TextUnformatted("Set Amount");
        ImGui.Separator();

        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("##rpamount", ref editAmount, 1, 10);
        if (editAmount < 1)    editAmount = 1;
        if (editAmount > 9999) editAmount = 9999;

        if (ImGui.Button("OK##rpamountok", new Vector2(80, 0)))
        {
            if (ctxItem != null)
            {
                ctxItem.Amount = editAmount;
                plugin.Configuration.Save();
                var amtBag = bags[ctxBagIdx];
                if (amtBag.IsShared)
                    plugin.PublishBagOp(amtBag.Id, RPFramework.Models.Net.BagOpType.UpdateItem, item: Plugin.ItemToDto(ctxItem));
            }
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##rpamountcancel", new Vector2(80, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Icon search
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the "Name@World" identity of the player currently targeted in-game,
    /// or null if no valid player target is found.
    /// </summary>
    private static string? GetTargetPlayerId()
    {
        var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
        if (target == null) return null;
        if (target is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc) return null;
        string? world = null;
        if (Plugin.DataManager.GetExcelSheet<World>().TryGetRow(pc.HomeWorld.RowId, out var row))
            world = row.Name.ToString();
        return world == null ? null : $"{pc.Name}@{world}";
    }

    private static List<(string Name, uint IconId)> SearchIcons(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var results = new List<(string, uint)>();
        var sheet   = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var row in sheet)
        {
            if (results.Count >= 54) break;
            if (row.Icon == 0) continue;
            var name = row.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                results.Add((name, row.Icon));
        }
        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Bag item popup  (sub-inventory for RpItemType.Bag items)
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawBagItemPopup(List<RpBag> bags)
    {
        if (_openBagItem == null) return;

        float scale    = ImGuiHelpers.GlobalScale;
        float sz       = SlotPx * scale;
        float pad      = PadPx  * scale;
        int   capacity = Math.Max(1, _openBagItem.Capacity);
        int   rows     = (capacity + Cols - 1) / Cols;
        float gridH    = Math.Min(rows, 7) * (sz + pad) + pad;
        float popupW   = Cols * (sz + pad) + ImGui.GetStyle().WindowPadding.X * 2 + pad;

        ImGui.SetNextWindowPos(_openBagItemPos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupW, 0), ImGuiCond.Appearing);

        if (!ImGui.BeginPopup("##rp_bagitem", ImGuiWindowFlags.AlwaysAutoResize)) return;

        // Header
        ImGui.TextUnformatted(_openBagItem.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"  {_openBagItem.Contents.Count}/{capacity} slots");
        ImGui.Separator();

        // Scrollable slot grid
        using (var child = ImRaii.Child("##rpbagpopupgrid", new Vector2(-1, gridH), false,
               ImGuiWindowFlags.NoScrollbar | (rows > 7 ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar)))
        {
            if (child)
            {
                for (int i = 0; i < capacity; i++)
                {
                    if (i % Cols != 0) ImGui.SameLine(0, pad);
                    if (i < _openBagItem.Contents.Count)
                        DrawBagContentSlot(i, sz);
                    else
                        DrawEmptySlot(sz);
                }
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled("Right-click items to take out or discard.");

        // Context menu for items inside this bag — must be opened inside the popup scope
        if (_pendingBagCtxMenu) { ImGui.OpenPopup("##rp_bagitemctx"); _pendingBagCtxMenu = false; }
        DrawBagItemContextMenu(bags);

        ImGui.EndPopup();
    }

    private void DrawBagContentSlot(int idx, float sz)
    {
        var item = _openBagItem!.Contents[idx];
        var pos  = ImGui.GetCursorScreenPos();

        ImGui.PushID($"##rpbcs{idx}");

        if (item.IconId > 0)
        {
            var tex  = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId));
            var wrap = tex.GetWrapOrEmpty();
            ImGui.ImageButton(wrap.Handle, new Vector2(sz, sz), Vector2.Zero, Vector2.One, 0, Vector4.Zero);
        }
        else
        {
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(pos, pos + new Vector2(sz, sz), 0xFF3D3D3D);
            dl.AddRect(pos,       pos + new Vector2(sz, sz), 0xFF606060);
            ImGui.InvisibleButton($"##rpbcib{idx}", new Vector2(sz, sz));
        }

        if (item.Amount > 1)
        {
            var    dl   = ImGui.GetWindowDrawList();
            string amt  = item.Amount.ToString();
            var    tSz  = ImGui.CalcTextSize(amt);
            var    tPos = pos + new Vector2(sz - tSz.X - 2f, sz - tSz.Y - 1f);
            dl.AddText(tPos + new Vector2(1, 1), 0xCC000000, amt);
            dl.AddText(tPos, 0xFFFFFFFF, amt);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(item.Name);
            ImGui.TextDisabled($"x{item.Amount}");
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                ImGui.Separator();
                ImGui.PushTextWrapPos(240f * ImGuiHelpers.GlobalScale);
                ImGui.TextUnformatted(item.Description);
                ImGui.PopTextWrapPos();
            }
            ImGui.EndTooltip();

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                _bagCtxItem       = item;
                _pendingBagCtxMenu = true;
            }
        }

        ImGui.PopID();
    }

    private void DrawBagItemContextMenu(List<RpBag> bags)
    {
        if (_bagCtxItem == null) return;
        if (!ImGui.BeginPopup("##rp_bagitemctx")) return;

        ImGui.TextDisabled(_bagCtxItem.Name);
        ImGui.Separator();

        if (ImGui.MenuItem("Take out##rpbagctxtakeout"))
        {
            if (_openBagItem != null && _openBagParent != null)
            {
                _openBagItem.Contents.Remove(_bagCtxItem!);
                _openBagParent.Items.Add(_bagCtxItem!);
                plugin.Configuration.Save();
                if (_openBagParent.IsShared)
                    plugin.PublishBagOp(_openBagParent.Id, RPFramework.Models.Net.BagOpType.UpdateItem,
                        item: Plugin.ItemToDto(_openBagItem));
            }
            _bagCtxItem = null;
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.MenuItem("Discard##rpbagctxdiscard"))
        {
            if (_openBagItem != null && _openBagParent != null)
            {
                _openBagItem.Contents.Remove(_bagCtxItem!);
                plugin.Configuration.Save();
                if (_openBagParent.IsShared)
                    plugin.PublishBagOp(_openBagParent.Id, RPFramework.Models.Net.BagOpType.UpdateItem,
                        item: Plugin.ItemToDto(_openBagItem));
            }
            _bagCtxItem = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
