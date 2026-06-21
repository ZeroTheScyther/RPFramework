using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// A nested bag (RpItemType.Bag item) opened as its own window — its contents are a sub-inventory.
/// Resolves the bag live from the store each frame by walking <see cref="_path"/> (the chain of
/// bag-item ids from the inventory root to this bag), so remote edits show instantly and the window
/// closes itself if the bag is moved/removed/traded away. Spawned + tracked by <see cref="Plugin.OpenBag"/>.
/// </summary>
public sealed class BagItemWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly Guid   _bagId;
    private readonly Guid[] _path;            // chain of bag-item ids; last element is this bag
    private readonly string _key;
    private readonly Action<string> _onClosed;
    private readonly InventoryGridView _grid;

    public BagItemWindow(Plugin plugin, Guid bagId, Guid[] path, string key, string name, Action<string> onClosed)
        : base($"{name}##rpbag_{key}", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin   = plugin;
        _bagId    = bagId;
        _path     = path;
        _key      = key;
        _onClosed = onClosed;
        _grid     = new InventoryGridView(plugin);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(256, 300), MaximumSize = new Vector2(900, 1200) };
        Size          = new Vector2(280, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnClose() => _onClosed(_key);
    public void Dispose() { }

    /// <summary>Resolves the opened bag item and the contents container it shows, or null if the path broke.</summary>
    private (RpItemDto Bag, List<RpItemDto> Items)? Resolve()
    {
        var bag = _plugin.Store.Bag(_bagId);
        if (bag == null) return null;
        var items = bag.Items;
        RpItemDto? current = null;
        foreach (var id in _path)
        {
            current = items.FirstOrDefault(i => i.Id == id);
            if (current == null || current.Type != RpItemType.Bag) return null;
            items = current.Contents ?? new List<RpItemDto>();
        }
        return current == null ? null : (current, items);
    }

    public override void Draw()
    {
        var bag = _plugin.Store.Bag(_bagId);
        var resolved = Resolve();
        if (bag == null || resolved == null)
        {
            ImGui.TextDisabled("This bag is no longer available.");
            return;
        }
        var (bagItem, items) = resolved.Value;
        string? code = bag.CampaignCode;
        var allBags = _plugin.Store.BagsIn(code).ToList();

        _grid.BeginFrame();

        ImGui.TextDisabled($"{bagItem.Contents?.Count ?? 0}/{bagItem.Capacity} slots used");
        ImGui.Separator();

        using (var grid = ImRaii.Child("##rpbaggrid", new Vector2(-1, -1), false))
            if (grid) _grid.DrawGrid(bag, _path, items, allBags, code, bagItem.Capacity);

        _grid.DrawModals();
    }
}
