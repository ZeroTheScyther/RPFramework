using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// RP Inventory. Tabs are "inventories" (server BagDto); RpItemType.Bag items are nested "bags"
/// that open as their own windows (see <see cref="BagItemWindow"/>). An inventory is personal
/// (owned by a player, shareable) or DM (OwnerPlayerId == "", auto-shared with the campaign's
/// DMs/Co-DMs). Server-first: everything is read from the store + sent as intents. The slot grid
/// itself lives in <see cref="InventoryGridView"/> and is shared with the bag windows.
/// </summary>
public class InventoryWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly InventoryGridView _grid;

    private int _selectedTab;
    private int _tabBarVersion;  // bumped on reorder so ImGui rebuilds its cached tab order

    // Tab context menu (deferred so it opens at the parent scope, never clipped)
    private Guid _tabCtxBag;
    private bool _pendingTabCtx;

    // Create-inventory modal
    private bool   _pendingCreateInv, _createInvOpen;
    private string _createInvName = "";
    private bool   _createInvDm;

    // Rename-inventory modal
    private bool _pendingRename, _renameOpen;
    private Guid _renameBag;
    private string _renameName = "";

    public InventoryWindow(Plugin plugin)
        : base("RP Inventory##RPFramework", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        _grid = new InventoryGridView(plugin);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(256, 360), MaximumSize = new Vector2(900, 1200) };
        Size          = new Vector2(300, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Flush deferred popup opens at the top of the frame.
        if (_pendingTabCtx)    { ImGui.OpenPopup("##rptabctx"); _pendingTabCtx = false; }
        if (_pendingCreateInv) { _createInvOpen = true; ImGui.OpenPopup("##rpcreateinv"); _pendingCreateInv = false; }
        if (_pendingRename)    { _renameOpen = true; ImGui.OpenPopup("##rprename"); _pendingRename = false; }
        _grid.BeginFrame();

        string? code = plugin.ActiveCampaign;
        if (code == null) { ImGui.TextDisabled("Connect to a server to use the inventory."); return; }

        var bags = Ordered(code, plugin.Store.BagsIn(code));
        if (_selectedTab >= bags.Count) _selectedTab = Math.Max(0, bags.Count - 1);

        DrawTabBar(bags, code);
        ImGui.Separator();

        if (bags.Count > 0 && _selectedTab < bags.Count)
        {
            var bag = bags[_selectedTab];
            using var grid = ImRaii.Child("##rpgrid", new Vector2(-1, -1), false);
            if (grid) _grid.DrawGrid(bag, Array.Empty<Guid>(), bag.Items, bags, code);
        }
        else ImGui.TextDisabled("No inventories. Use the + tab to create one.");

        DrawTabContextMenu(bags, code);
        DrawCreateInvModal(code);
        DrawRenameModal();
        _grid.DrawModals();
    }

    // ── Tab bar ───────────────────────────────────────────────────────────────

    /// <summary>Applies the player's saved tab order; unknown (new) bags are appended DM-first then by name.</summary>
    private List<BagDto> Ordered(string code, IEnumerable<BagDto> source)
    {
        var byId = source.ToDictionary(b => b.BagId);
        var result = new List<BagDto>();
        if (plugin.Configuration.InventoryOrder.TryGetValue(code, out var order))
            foreach (var id in order)
                if (byId.Remove(id, out var b)) result.Add(b);
        result.AddRange(byId.Values
            .OrderBy(b => string.IsNullOrEmpty(b.OwnerPlayerId) ? 0 : 1)
            .ThenBy(b => b.Name));
        return result;
    }

    private void SaveOrder(string code, List<BagDto> ordered)
    {
        plugin.Configuration.InventoryOrder[code] = ordered.Select(b => b.BagId).ToList();
        plugin.Configuration.Save();
    }

    private void DrawTabBar(List<BagDto> bags, string code)
    {
        // The version suffix forces ImGui to rebuild its internal tab order after a reorder.
        using var tabBar = ImRaii.TabBar($"##rptabs{_tabBarVersion}");
        if (!tabBar) return;

        for (int i = 0; i < bags.Count; i++)
        {
            var  bag = bags[i];
            bool dm  = string.IsNullOrEmpty(bag.OwnerPlayerId);
            bool open = ImGui.BeginTabItem($"{bag.Name}##rpt{bag.BagId}");

            if (ImGui.IsItemHovered())
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { _tabCtxBag = bag.BagId; _pendingTabCtx = true; }
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(dm ? "DM Inventory (shared with DMs/Co-DMs)" : "Personal Inventory");
                if (bag.ParticipantIds.Count > 1) ImGui.TextDisabled($"Shared with {bag.ParticipantIds.Count - 1} other(s)");
                ImGui.TextDisabled("Drag to reorder · right-click for options");
                ImGui.EndTooltip();
            }

            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            {
                ImGui.SetDragDropPayload("RPTAB"u8, BitConverter.GetBytes(i));
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
                        int src = BitConverter.ToInt32(accepted.Handle->DataSpan);
                        if (src != i && src >= 0 && src < bags.Count)
                        {
                            var moved = bags[src];
                            bags.RemoveAt(src);
                            bags.Insert(i, moved);
                            _selectedTab = i;
                            _tabBarVersion++;
                            SaveOrder(code, bags);
                        }
                    }
                }
                ImGui.EndDragDropTarget();
            }

            if (open) { _selectedTab = i; ImGui.EndTabItem(); }
        }

        if (ImGui.TabItemButton("+##rpaddtab", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip))
        { _createInvName = ""; _createInvDm = false; _pendingCreateInv = true; }
    }

    private void DrawTabContextMenu(List<BagDto> bags, string code)
    {
        if (!ImGui.BeginPopup("##rptabctx")) return;
        var bag = bags.FirstOrDefault(b => b.BagId == _tabCtxBag);
        if (bag == null) { ImGui.EndPopup(); return; }

        bool dm      = string.IsNullOrEmpty(bag.OwnerPlayerId);
        bool isOwner = bag.OwnerPlayerId == plugin.LocalPlayerId;

        ImGui.TextDisabled(bag.Name);
        ImGui.Separator();

        if (ImGui.MenuItem("Rename")) { _renameBag = bag.BagId; _renameName = bag.Name; _pendingRename = true; ImGui.CloseCurrentPopup(); }

        // Personal inventories can be shared with other players.
        if (!dm && isOwner)
        {
            var members = plugin.Store.Party(code)?.Members.Where(m => m.PlayerId != plugin.LocalPlayerId).ToList() ?? new();
            if (members.Count > 0 && ImGui.BeginMenu("Share with"))
            {
                foreach (var m in members)
                    if (ImGui.MenuItem($"{m.DisplayName}##sh_{m.PlayerId}")) _ = plugin.Network.BagShareInvite(bag.BagId, m.PlayerId);
                ImGui.EndMenu();
            }
        }

        if (ImGui.MenuItem("Copy code")) ImGui.SetClipboardText(bag.BagId.ToString());
        ImGui.Separator();
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.9f, 0.3f, 0.3f, 1f)))
            if (ImGui.MenuItem("Delete")) { _ = plugin.Network.BagDelete(bag.BagId); ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    // ── Modals ────────────────────────────────────────────────────────────────

    private void DrawCreateInvModal(string code)
    {
        if (!_createInvOpen) return;
        ImGui.SetNextWindowSize(new Vector2(320 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("##rpcreateinv", ref _createInvOpen, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted("New Inventory");
        ImGui.Separator(); ImGui.Spacing();
        ImGui.TextUnformatted("Name:"); ImGui.SetNextItemWidth(-1); ImGui.InputText("##invname", ref _createInvName, 64);

        ImGui.Spacing();
        bool isDm = plugin.IsDm(code);
        int kind = _createInvDm ? 1 : 0;
        if (ImGui.RadioButton("Personal", ref kind, 0)) _createInvDm = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Yours; you can share it with other players.");
        ImGui.SameLine();
        using (ImRaii.Disabled(!isDm))
            if (ImGui.RadioButton("DM Inventory", ref kind, 1)) _createInvDm = true;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(isDm ? "Shared automatically with all DMs and Co-DMs in this campaign."
                                  : "Only a DM can create a DM inventory.");

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_createInvName)))
            if (ImGui.Button("Create##doinv", new Vector2(bw, 0)))
            {
                _ = plugin.Network.BagCreate(code, _createInvName.Trim(), _createInvDm && isDm);
                _createInvOpen = false; ImGui.CloseCurrentPopup();
            }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##caninv", new Vector2(bw, 0))) { _createInvOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private void DrawRenameModal()
    {
        if (!_renameOpen) return;
        ImGui.SetNextWindowSize(new Vector2(300 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("##rprename", ref _renameOpen, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted("Rename Inventory");
        ImGui.Separator(); ImGui.Spacing();
        ImGui.SetNextItemWidth(-1); ImGui.InputText("##rnname", ref _renameName, 64);

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_renameName)))
            if (ImGui.Button("OK##dorn", new Vector2(bw, 0)))
            {
                _ = plugin.Network.BagRename(_renameBag, _renameName.Trim());
                _renameOpen = false; ImGui.CloseCurrentPopup();
            }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##canrn", new Vector2(bw, 0))) { _renameOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
