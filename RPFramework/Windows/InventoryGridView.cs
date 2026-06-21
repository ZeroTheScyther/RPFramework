using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// The FFXIV-style slot grid for one inventory container, shared by the main inventory window
/// (the tab's root) and by each open bag window (a nested RpItemType.Bag item's contents).
/// Opening a bag delegates to <see cref="Plugin.OpenBag"/>, which spawns a separate window —
/// bags are their own windows, not breadcrumb navigation inside one window.
///
/// All mutations go to the server as path-addressed intents; <c>path</c> is the chain of bag-item
/// ids that locates this container within its inventory (empty = the inventory root).
/// </summary>
public sealed class InventoryGridView
{
    private readonly Plugin plugin;
    private readonly string _uid = Guid.NewGuid().ToString("N")[..8]; // unique popup ids per host

    private const int   Cols     = 5;
    private const float SlotPx   = 40f;
    private const float PadPx    = 2f;
    private const int   MinSlots = 35; // 5 × 7

    // Item create/edit modal
    private bool       _pendingItem, _itemOpen;
    private Guid?      _editItemId;
    private Guid       _itemBag;
    private Guid[]     _itemPath = Array.Empty<Guid>();
    private string     _itemCode = "";
    private string     _name = "", _desc = "";
    private uint       _iconId = 26;
    private int        _amount = 1;
    private RpItemType _type = RpItemType.Normal;
    private int        _capacity = 10;
    private string     _iconQuery = "", _lastIconQuery = "";
    private readonly List<(string Name, uint IconId)> _iconResults = new();
    private readonly List<SkillEffect>    _effects    = new();
    private readonly List<SkillCondition> _conditions = new();

    // Amount prompt, shared by Split and stackable trades
    private bool        _pendingAmt, _amtOpen;
    private string      _amtTitle = "";
    private int         _amtMax = 1, _amtValue = 1;
    private Action<int>? _amtAction;

    public InventoryGridView(Plugin plugin) => this.plugin = plugin;

    /// <summary>Flush deferred popup opens. Call once at the top of the host's Draw, outside any child.</summary>
    public void BeginFrame()
    {
        if (_pendingItem) { _itemOpen = true; ImGui.OpenPopup($"##rpitem{_uid}"); _pendingItem = false; }
        if (_pendingAmt)  { _amtOpen  = true; ImGui.OpenPopup($"##rpamt{_uid}");  _pendingAmt  = false; }
    }

    /// <summary>Offer an item for trade. Stackables go through an amount prompt; everything else (bags,
    /// single items) is offered whole right away.</summary>
    private void OfferTrade(string code, string toId, string toName, Guid bagId, Guid[] path, RpItemDto item, bool isCopy, bool stack)
    {
        string verb = isCopy ? "Copy" : "Give";
        if (stack)
            OpenAmountModal($"{verb} {item.Name} to {toName}", item.Amount, item.Amount,
                n => plugin.Network.TradeOffer(code, toId, bagId, path, item.Id, n, isCopy));
        else
            _ = plugin.Network.TradeOffer(code, toId, bagId, path, item.Id, 1, isCopy);
    }

    private void OpenAmountModal(string title, int max, int def, Action<int> action)
    {
        _amtTitle  = title;
        _amtMax    = Math.Max(1, max);
        _amtValue  = Math.Clamp(def, 1, _amtMax);
        _amtAction = action;
        _pendingAmt = true;
    }

    /// <summary>Draws a container's slot grid. <paramref name="capacity"/> (a nested bag's slot count)
    /// fixes the number of slots; the inventory root passes null and grows in rows of <see cref="Cols"/>.</summary>
    public void DrawGrid(BagDto bag, Guid[] path, List<RpItemDto> items, List<BagDto> allBags, string code, int? capacity = null)
    {
        float sz  = SlotPx * ImGuiHelpers.GlobalScale;
        float pad = PadPx  * ImGuiHelpers.GlobalScale;
        int   count = items.Count;
        int   total = capacity is int cap
            ? Math.Max(count, Math.Max(1, cap))                        // exactly `cap` slots (more only if over-full)
            : Math.Max(MinSlots, ((count + Cols) / Cols) * Cols);

        for (int i = 0; i < total; i++)
        {
            if (i % Cols != 0) ImGui.SameLine(0, pad);
            if (i < count) DrawItemSlot(bag, path, items[i], items, allBags, code, sz);
            else           DrawEmptySlot(bag, path, code, sz);
        }
    }

    /// <summary>Draw the modals. Call at the end of the host's Draw, outside any child.</summary>
    public void DrawModals() { DrawItemModal(); DrawAmountModal(); }

    private void DrawAmountModal()
    {
        if (!_amtOpen) return;
        ImGui.SetNextWindowSize(new Vector2(260 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal($"##rpamt{_uid}", ref _amtOpen, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted(_amtTitle);
        ImGui.Separator(); ImGui.Spacing();
        ImGui.TextUnformatted($"Amount (max {_amtMax}):");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputInt("##amtval", ref _amtValue); _amtValue = Math.Clamp(_amtValue, 1, _amtMax);

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;
        if (ImGui.Button("OK##doamt", new Vector2(bw, 0))) { _amtAction?.Invoke(_amtValue); _amtOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##canamt", new Vector2(bw, 0))) { _amtOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    // ── Slots ───────────────────────────────────────────────────────────────────

    private void DrawEmptySlot(BagDto bag, Guid[] path, string code, float sz)
    {
        var pos = ImGui.GetCursorScreenPos();
        var dl  = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(sz, sz), 0xFF2A2A2A);
        dl.AddRect(pos,       pos + new Vector2(sz, sz), 0xFF505050);
        ImGui.InvisibleButton($"##empty_{bag.BagId}_{pos.X}_{pos.Y}", new Vector2(sz, sz));
        if (ImGui.IsItemClicked()) OpenItemModal(bag.BagId, path, code, null);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Empty - click to add an item");
    }

    private void DrawItemSlot(BagDto bag, Guid[] path, RpItemDto item, List<RpItemDto> siblings, List<BagDto> allBags, string code, float sz)
    {
        var pos = ImGui.GetCursorScreenPos();
        var dl  = ImGui.GetWindowDrawList();

        if (item.IconId > 0)
        {
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(sz, sz));
        }
        else
        {
            dl.AddRectFilled(pos, pos + new Vector2(sz, sz), 0xFF3D3D3D);
            ImGui.InvisibleButton($"##ib_{item.Id}", new Vector2(sz, sz));
        }

        bool isBag = item.Type == RpItemType.Bag;
        if (isBag)
        {
            dl.AddRect(pos, pos + new Vector2(sz, sz), 0xFFD4AA00, 0, ImDrawFlags.None, 2f);
            string usage = $"{item.Contents?.Count ?? 0}/{item.Capacity}";
            DrawBadge(dl, pos, sz, usage, 0xFFD4AA00);
        }
        else if (item.Amount > 1)
        {
            DrawBadge(dl, pos, sz, item.Amount.ToString(), 0xFFFFFFFF);
        }

        // Left-click a bag opens it as its own window.
        if (isBag && ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            plugin.OpenBag(bag.BagId, path.Append(item.Id).ToArray(), item.Name);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(item.Name);
            if (isBag) ImGui.TextDisabled($"Bag - {item.Contents?.Count ?? 0}/{item.Capacity} slots used (click to open)");
            else if (item.Type.IsEquippable()) ImGui.TextDisabled(item.Type.Label());
            else if (item.Type == RpItemType.Consumable) ImGui.TextDisabled(item.Amount > 1 ? $"Consumable x{item.Amount} (right-click to Use)" : "Consumable (right-click to Use)");
            else if (item.Amount > 1) ImGui.TextDisabled($"Amount: {item.Amount}");
            if (item.Effects is { Count: > 0 })
                ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), ItemEffects.Summary(item.Effects, plugin.Store.TemplateOrDefault(code)));
            if (item.Conditions is { Count: > 0 })
                ImGui.TextColored(new Vector4(0.70f, 0.70f, 0.45f, 1f), $"While: {ItemEffects.ConditionSummary(item.Conditions, plugin.Store.TemplateOrDefault(code))}");
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                ImGui.PushTextWrapPos(260 * ImGuiHelpers.GlobalScale);
                ImGui.TextDisabled(item.Description);
                ImGui.PopTextWrapPos();
            }
            ImGui.EndTooltip();
        }

        if (ImGui.BeginPopupContextItem($"##ictx_{item.Id}"))
        {
            if (isBag && ImGui.MenuItem("Open")) plugin.OpenBag(bag.BagId, path.Append(item.Id).ToArray(), item.Name);
            if (item.Type == RpItemType.Consumable && ImGui.MenuItem("Use")) _ = plugin.Network.UseItem(bag.BagId, path, item.Id);
            if (item.Type.IsEquippable() && ImGui.MenuItem($"Equip ({item.Type.Label()})")) _ = plugin.Network.EquipItem(bag.BagId, path, item.Id);
            if (ImGui.MenuItem("Edit")) OpenItemModal(bag.BagId, path, code, item);
            if (ImGui.MenuItem("Copy")) _ = plugin.Network.ItemAdd(bag.BagId, path, item with { Id = Guid.NewGuid() });

            // Split a stack into a new stack in this same container.
            if (item.Type.IsStackable() && item.Amount > 1 && ImGui.MenuItem("Split"))
                OpenAmountModal($"Split {item.Name}", item.Amount - 1, 1, n => plugin.Network.ItemSplit(bag.BagId, path, item.Id, n));

            // Take an item out of this nested bag into the container one level up (e.g. back to the inventory root).
            if (path.Length > 0 && ImGui.MenuItem("Take out"))
            {
                var parent = path.Take(path.Length - 1).ToArray();
                _ = plugin.Network.ItemMove(bag.BagId, path, bag.BagId, parent, item.Id);
            }

            // Drop a (non-bag) item into a sibling bag in this same container. Bags can't nest, so only
            // normal items get this option.
            var siblingBags = isBag ? new List<RpItemDto>() : siblings.Where(s => s.Type == RpItemType.Bag && s.Id != item.Id).ToList();
            if (siblingBags.Count > 0 && ImGui.BeginMenu("Put into"))
            {
                foreach (var sb in siblingBags)
                {
                    var into = path.Append(sb.Id).ToArray();
                    if (ImGui.MenuItem($"{sb.Name}##into_{sb.Id}")) _ = plugin.Network.ItemMove(bag.BagId, path, bag.BagId, into, item.Id);
                }
                ImGui.EndMenu();
            }

            if (allBags.Count > 1 && ImGui.BeginMenu("Move to inventory"))
            {
                foreach (var other in allBags.Where(b => b.BagId != bag.BagId))
                    if (ImGui.MenuItem($"{other.Name}##mv_{other.BagId}")) _ = plugin.Network.ItemMove(bag.BagId, path, other.BagId, Array.Empty<Guid>(), item.Id);
                ImGui.EndMenu();
            }

            var members = plugin.Store.Party(code)?.Members.Where(m => m.PlayerId != plugin.LocalPlayerId).ToList() ?? new();
            if (members.Count > 0 && ImGui.BeginMenu("Offer to"))
            {
                bool stack = item.Type.IsStackable() && item.Amount > 1;
                foreach (var m in members)
                {
                    string mid = m.PlayerId, dn = m.DisplayName;
                    if (ImGui.MenuItem($"{dn} (copy)##oc_{mid}")) OfferTrade(code, mid, dn, bag.BagId, path, item, true, stack);
                    if (ImGui.MenuItem($"{dn} (give)##og_{mid}")) OfferTrade(code, mid, dn, bag.BagId, path, item, false, stack);
                }
                ImGui.EndMenu();
            }

            ImGui.Separator();
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.9f, 0.3f, 0.3f, 1f)))
                if (ImGui.MenuItem("Discard")) _ = plugin.Network.ItemRemove(bag.BagId, path, item.Id);
            ImGui.EndPopup();
        }
    }

    private static void DrawBadge(ImDrawListPtr dl, Vector2 pos, float sz, string text, uint color)
    {
        var tSz  = ImGui.CalcTextSize(text);
        var tPos = pos + new Vector2(sz - tSz.X - 2f, sz - tSz.Y - 1f);
        dl.AddText(tPos + new Vector2(1, 1), 0xCC000000, text);
        dl.AddText(tPos, color, text);
    }

    // ── Item modal ──────────────────────────────────────────────────────────────

    private void OpenItemModal(Guid bagId, Guid[] path, string code, RpItemDto? item)
    {
        _itemBag    = bagId;
        _itemPath   = path;
        _itemCode   = code;
        _editItemId = item?.Id;
        _name       = item?.Name ?? "";
        _desc       = item?.Description ?? "";
        _iconId     = item?.IconId ?? 26;
        _amount     = item?.Amount ?? 1;
        _type       = item?.Type ?? RpItemType.Normal;
        _capacity   = item?.Capacity ?? 10;
        _iconQuery  = _lastIconQuery = "";
        _iconResults.Clear();
        _effects.Clear();
        if (item?.Effects != null)
            _effects.AddRange(item.Effects.Select(e => new SkillEffect { FieldId = e.FieldId, Op = e.Op, Value = e.Value, IsPercentage = e.IsPercentage }));
        _conditions.Clear();
        if (item?.Conditions != null)
            _conditions.AddRange(item.Conditions.Select(c => new SkillCondition { FieldId = c.FieldId, Op = c.Op, Value = c.Value, IsPercentage = c.IsPercentage }));
        _pendingItem = true;
    }

    private void DrawItemModal()
    {
        if (!_itemOpen) return;
        ImGui.SetNextWindowSize(new Vector2(360 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal($"##rpitem{_uid}", ref _itemOpen, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted(_editItemId == null ? "New Item" : "Edit Item");
        ImGui.Separator(); ImGui.Spacing();

        float scale = ImGuiHelpers.GlobalScale;
        ImGui.TextUnformatted("Name:");        ImGui.SetNextItemWidth(-1); ImGui.InputText("##itname", ref _name, 64);
        ImGui.TextUnformatted("Description:"); ImGui.SetNextItemWidth(-1); ImGui.InputTextMultiline("##itdesc", ref _desc, 512, new Vector2(-1, 50 * scale));

        // Type — full taxonomy. Bag is only offered at the inventory root (no bag-in-bag).
        var allowed   = AllowedTypes();
        var typeNames = allowed.Select(t => t.Label()).ToArray();
        if (!allowed.Contains(_type)) _type = RpItemType.Normal;
        int ti = Math.Max(0, Array.IndexOf(allowed, _type));
        ImGui.TextUnformatted("Type:"); ImGui.SameLine(); ImGui.SetNextItemWidth(150 * scale);
        if (ImGui.Combo("##ittype", ref ti, typeNames, typeNames.Length)) _type = allowed[ti];

        if (_type.IsStackable())
        { ImGui.SameLine(); ImGui.TextUnformatted("Amount:"); ImGui.SameLine(); ImGui.SetNextItemWidth(90 * scale); ImGui.InputInt("##itamt", ref _amount); _amount = Math.Max(1, _amount); }
        else _amount = 1;

        if (_type == RpItemType.Bag)
        { ImGui.SameLine(); ImGui.TextUnformatted("Capacity:"); ImGui.SameLine(); ImGui.SetNextItemWidth(70 * scale); ImGui.InputInt("##itcap", ref _capacity); _capacity = Math.Clamp(_capacity, 1, 1000); }

        if (_type.IsEquippable() || _type == RpItemType.Consumable)
        {
            ImGui.Spacing();
            // Conditional gear only contributes its effects while ALL conditions hold (like a passive);
            // with no conditions the effects are always-on. Consumables are point-of-use, so no gate.
            if (_type.IsEquippable())
            {
                ImGui.TextDisabled(_conditions.Count == 0
                    ? "Conditions (none = effects always active while equipped):"
                    : "Conditions (effects apply only while ALL are true):");
                DrawItemConditions(scale);
                ImGui.Spacing();
            }

            ImGui.TextDisabled(_type == RpItemType.Consumable ? "Effects (applied on Use):" : "Effects (while equipped):");
            DrawItemEffects(scale);
        }

        ImGui.Spacing();
        var curTex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(_iconId)).GetWrapOrEmpty();
        ImGui.Image(curTex.Handle, new Vector2(SlotPx, SlotPx));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##iconsearch", "Search item icons…", ref _iconQuery, 64);
        if (_iconQuery != _lastIconQuery) { _lastIconQuery = _iconQuery; RefreshIcons(); }

        if (_iconResults.Count > 0)
            using (var ic = ImRaii.Child("##iconresults", new Vector2(-1, 110 * ImGuiHelpers.GlobalScale), true))
            {
                if (ic)
                {
                    int col = 0;
                    foreach (var (iname, iicon) in _iconResults)
                    {
                        var t = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iicon)).GetWrapOrEmpty();
                        ImGui.Image(t.Handle, new Vector2(32, 32));
                        if (ImGui.IsItemClicked()) _iconId = iicon;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(iname);
                        if (++col % 8 != 0) ImGui.SameLine();
                    }
                }
            }

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_name)))
            if (ImGui.Button(_editItemId == null ? "Add##doit" : "Save##doit", new Vector2(bw, 0)))
            {
                List<SkillEffect>? fx = (_type.IsEquippable() || _type == RpItemType.Consumable) && _effects.Count > 0
                    ? _effects.Select(e => new SkillEffect { FieldId = e.FieldId, Op = e.Op, Value = e.Value, IsPercentage = e.IsPercentage }).ToList()
                    : null;
                List<SkillCondition>? conds = _type.IsEquippable() && _conditions.Count > 0
                    ? _conditions.Select(c => new SkillCondition { FieldId = c.FieldId, Op = c.Op, Value = c.Value, IsPercentage = c.IsPercentage }).ToList()
                    : null;
                var dto = new RpItemDto(_editItemId ?? Guid.NewGuid(), _name.Trim(), _desc, _iconId, _amount, _type, _capacity, null, fx, conds);
                if (_editItemId == null) _ = plugin.Network.ItemAdd(_itemBag, _itemPath, dto);
                else                     _ = plugin.Network.ItemUpdate(_itemBag, _itemPath, dto);
                _itemOpen = false; ImGui.CloseCurrentPopup();
            }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##canit", new Vector2(bw, 0))) { _itemOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    /// <summary>Item types selectable in the modal: Misc, Bag (root only), the equip slots, then Consumable.</summary>
    private RpItemType[] AllowedTypes()
    {
        var list = new List<RpItemType> { RpItemType.Normal };
        if (_itemPath.Length == 0) list.Add(RpItemType.Bag); // bags only at the inventory root
        list.AddRange(ItemSlots.EquipOrder);
        list.Add(RpItemType.Consumable);
        return list.ToArray();
    }

    private void DrawItemEffects(float scale)
    {
        var template = plugin.Store.TemplateOrDefault(_itemCode);
        // Equipment effects only raise a pool's max; consumables can also touch the current value.
        var fields    = EffectEditor.TargetFields(template, includeBarCurrent: _type == RpItemType.Consumable);
        var names     = fields.Select(f => f.Name).ToArray();
        if (fields.Count == 0) { ImGui.TextDisabled("Publish a sheet template with stats to add effects."); return; }

        if (ImGui.BeginTable("##itefx", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 96 * scale);
            ImGui.TableSetupColumn("Op",    ImGuiTableColumnFlags.WidthFixed, 44 * scale);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 56 * scale);
            ImGui.TableSetupColumn("%",     ImGuiTableColumnFlags.WidthFixed, 22 * scale);
            ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 22 * scale);
            ImGui.TableHeadersRow();
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                ImGui.TableNextRow(); ImGui.PushID($"##itfx{i}");
                EffectEditor.DrawRow(_effects[i], fields, names);
                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton($"X##d{i}")) _effects.RemoveAt(i);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (ImGui.SmallButton("+ Add effect")) _effects.Add(new SkillEffect { FieldId = fields.FirstOrDefault()?.Id ?? "" });
    }

    private void DrawItemConditions(float scale)
    {
        var template = plugin.Store.TemplateOrDefault(_itemCode);
        var fields   = ConditionEditor.TargetFields(template);
        var names    = fields.Select(f => f.Name).ToArray();
        if (fields.Count == 0) { ImGui.TextDisabled("Publish a sheet template with stats to add conditions."); return; }

        if (_conditions.Count > 0 && ImGui.BeginTable("##itcond", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 96 * scale);
            ImGui.TableSetupColumn("Op",    ImGuiTableColumnFlags.WidthFixed, 44 * scale);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 56 * scale);
            ImGui.TableSetupColumn("%",     ImGuiTableColumnFlags.WidthFixed, 22 * scale);
            ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 22 * scale);
            ImGui.TableHeadersRow();
            for (int i = _conditions.Count - 1; i >= 0; i--)
            {
                ImGui.TableNextRow(); ImGui.PushID($"##itcd{i}");
                ConditionEditor.DrawRow(_conditions[i], fields, names);
                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton($"X##cd{i}")) _conditions.RemoveAt(i);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (ImGui.SmallButton("+ Add condition")) _conditions.Add(new SkillCondition { FieldId = fields.FirstOrDefault()?.Id ?? "" });
    }

    private void RefreshIcons()
    {
        _iconResults.Clear();
        if (_iconQuery.Length < 2) return;
        foreach (var row in Plugin.DataManager.GetExcelSheet<Item>())
        {
            if (row.Icon == 0) continue;
            string n = row.Name.ToString();
            if (n.Length == 0 || !n.Contains(_iconQuery, StringComparison.OrdinalIgnoreCase)) continue;
            _iconResults.Add((n, row.Icon));
            if (_iconResults.Count >= 48) break;
        }
    }
}
