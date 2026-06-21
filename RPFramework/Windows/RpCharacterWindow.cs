using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// The unified RPCHARACTER window: one window over a single CharacterState. Tabs (in order):
/// Profile (the free-form RP fluff), Stats (pools/stats/specializations), Skills, Equipment, and
/// Companion (future). Profile + Stats bodies come from <see cref="CharacterSheetWindow"/>, Skills
/// from <see cref="SkillsWindow"/>; Equipment is drawn here. The DM template editor (entered via the
/// header pencil) takes over the whole window while active.
/// </summary>
public class RpCharacterWindow : Window, IDisposable
{
    public enum Tab { Profile, Stats, Skills, Equipment, Companion }

    private readonly Plugin _plugin;
    private Tab  _pendingTab = Tab.Profile;
    private bool _forceTab;
    private Tab  _current = Tab.Profile;
    private bool _profileEdit;   // owner toggles their Profile tab between read-only and editable

    public RpCharacterWindow(Plugin plugin)
        : base("RP Character##RPFramework.Character",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoResize)
    {
        _plugin       = plugin;
        Size          = new Vector2(470, 740);   // fixed; window is non-resizable by request
        SizeCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    /// <summary>Set the title to the character's name and rebuild the title-bar buttons before the window draws.</summary>
    public override void PreDraw()
    {
        string? code  = _plugin.ActiveCampaign;
        var     ch    = code != null && _plugin.LocalPlayerId != null
                        ? _plugin.Store.Character(code, _plugin.LocalPlayerId) : null;

        string title = "RP Character";
        if (ch != null)
        {
            ch.State.TextValues.TryGetValue(WellKnownIds.Name, out string? nm);
            title = string.IsNullOrWhiteSpace(nm) ? ch.DisplayName : nm!;
        }
        WindowName = $"{title}##RPFramework.Character";

        // Pen = toggle profile edit (everyone, own profile). Cog = Edit Sheet Template (DM only).
        TitleBarButtons.Clear();
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon        = _profileEdit ? FontAwesomeIcon.Check : FontAwesomeIcon.Pen,
            ShowTooltip = () => ImGui.SetTooltip(_profileEdit ? "Finish editing profile" : "Edit your profile"),
            Click       = _ => { _profileEdit = !_profileEdit; if (_profileEdit) OpenTo(Tab.Profile); },
        });
        if (code != null && _plugin.IsDm(code) && _plugin.Store.Party(code) is { IsPersonal: false })
        {
            string c = code;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon        = FontAwesomeIcon.Cog,
                ShowTooltip = () => ImGui.SetTooltip("Edit sheet template (DM)"),
                Click       = _ => _plugin.CharacterSheetWindow.EnterTemplateEditor(c),
            });
        }
    }

    /// <summary>Opens the window straight to a given tab (used by the /rp* command shortcuts).</summary>
    public void OpenTo(Tab tab)
    {
        _pendingTab = tab;
        _forceTab   = true;
        IsOpen      = true;
    }

    public override void Draw()
    {
        string? code = _plugin.ActiveCampaign;
        if (_plugin.LocalPlayerId == null || code == null)
        {
            ImGui.TextDisabled("Connect to a server to view your character.");
            return;
        }

        // Shared header: campaign selector + DM template-edit pencil (owned by CharacterSheetWindow).
        _plugin.CharacterSheetWindow.DrawCampaignSelector(code);
        ImGui.Separator();

        // The template editor is a global mode: while active it replaces the whole tab area.
        if (_plugin.CharacterSheetWindow.IsEditingTemplate(code))
        {
            _plugin.CharacterSheetWindow.DrawTemplateEditor(code);
            return;
        }

        using var tabs = ImRaii.TabBar("##rpchartabs");
        if (!tabs) return;
        DrawTab(Tab.Profile,   "Profile",   code);
        DrawTab(Tab.Stats,     "Stats",     code);
        DrawTab(Tab.Skills,    "Skills",    code);
        DrawTab(Tab.Equipment, "Equipment", code);
        DrawTab(Tab.Companion, "Companion", code);
    }

    private void DrawTab(Tab tab, string label, string code)
    {
        var flags = ImGuiTabItemFlags.None;
        if (_forceTab && _pendingTab == tab) flags |= ImGuiTabItemFlags.SetSelected;

        if (!ImGui.BeginTabItem($"{label}##rpchartab{tab}", flags)) return;
        if (_forceTab && _pendingTab == tab) _forceTab = false;
        if (_current != tab) { _current = tab; OnEnterTab(tab); }

        using (var body = ImRaii.Child($"##rpcharbody{tab}", new Vector2(-1, -1), false))
            if (body) DrawTabBody(tab, code);
        ImGui.EndTabItem();
    }

    private void OnEnterTab(Tab tab)
    {
        if (tab == Tab.Skills) _plugin.SkillsWindow.SyncFromStore();
    }

    private void DrawTabBody(Tab tab, string code)
    {
        switch (tab)
        {
            case Tab.Profile:   _plugin.CharacterSheetWindow.DrawProfile(code, _profileEdit); break;
            case Tab.Stats:     _plugin.CharacterSheetWindow.DrawStats(code);   break;
            case Tab.Skills:
                if (_plugin.ActiveCharacter == null) ImGui.TextDisabled("No character in this campaign yet.");
                else _plugin.SkillsWindow.DrawBody(code);
                break;
            case Tab.Equipment: DrawEquipment(code); break;
            case Tab.Companion: DrawCompanion(code); break;
        }
    }

    // ── Equipment paper-doll ─────────────────────────────────────────────────────

    private void DrawEquipment(string code)
    {
        var ch = _plugin.Store.Character(code, _plugin.LocalPlayerId!);
        if (ch == null) { ImGui.TextDisabled("No character in this campaign yet."); return; }
        var   st       = ch.State;
        var   template = _plugin.Store.TemplateOrDefault(code);
        float scale    = ImGuiHelpers.GlobalScale;

        ImGui.TextDisabled("Right-click an item in your inventory to Equip it; right-click a slot to unequip.");
        ImGuiHelpers.ScaledDummy(4f);

        float sz = 40 * scale;
        // Two tight columns (SizingFixedFit keeps them packed to the left, no wide gap).
        using var table = ImRaii.Table("##equipslots", 2, ImGuiTableFlags.SizingFixedFit);
        if (!table) return;
        foreach (var slot in ItemSlots.EquipOrder)
        {
            ImGui.TableNextColumn();
            DrawEquipSlot(code, st, template, slot, sz);
        }
    }

    private void DrawEquipSlot(string code, CharacterState st, SheetTemplate template, RpItemType slot, float sz)
    {
        st.Equipment.TryGetValue(slot, out var item);
        var pos = ImGui.GetCursorScreenPos();
        var dl  = ImGui.GetWindowDrawList();

        if (item is { IconId: > 0 })
        {
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(sz, sz));
        }
        else
        {
            dl.AddRectFilled(pos, pos + new Vector2(sz, sz), 0xFF2A2A2A);
            dl.AddRect(pos,       pos + new Vector2(sz, sz), 0xFF505050);
            ImGui.Dummy(new Vector2(sz, sz));
        }

        if (item != null && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(item.Name);
            ImGui.TextDisabled(slot.Label());
            if (item.Effects is { Count: > 0 })
                ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), ItemEffects.Summary(item.Effects, template));
            if (item.Conditions is { Count: > 0 })
            {
                bool active = StatMath.ItemActive(item, st, template);
                ImGui.TextColored(new Vector4(0.70f, 0.70f, 0.45f, 1f), $"While: {ItemEffects.ConditionSummary(item.Conditions, template)}");
                ImGui.TextColored(active ? new Vector4(0.45f, 0.85f, 0.45f, 1f) : new Vector4(0.85f, 0.45f, 0.45f, 1f),
                                  active ? "(active)" : "(inactive)");
            }
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                ImGui.PushTextWrapPos(260 * ImGuiHelpers.GlobalScale);
                ImGui.TextDisabled(item.Description);
                ImGui.PopTextWrapPos();
            }
            ImGui.EndTooltip();
        }

        // Right-click an occupied slot to unequip it into one of the player's inventories.
        if (item != null && ImGui.BeginPopupContextItem($"##eqctx_{slot}"))
        {
            var bags = _plugin.Store.BagsIn(code).ToList();
            if (bags.Count == 0) ImGui.TextDisabled("No inventory to unequip into");
            else if (ImGui.BeginMenu("Unequip to"))
            {
                foreach (var b in bags)
                    if (ImGui.MenuItem($"{b.Name}##un_{b.BagId}")) _ = _plugin.Network.UnequipItem(code, slot, b.BagId);
                ImGui.EndMenu();
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        using var g = ImRaii.Group();
        ImGui.TextDisabled(slot.Label());
        if (item != null) ImGui.TextUnformatted(item.Name);
        else              ImGui.TextDisabled("(empty)");
    }

    // ── Companion (future) ───────────────────────────────────────────────────────

    private void DrawCompanion(string code)
    {
        ImGui.TextDisabled("Companion");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(8f);
        ImGui.TextWrapped("Not built yet. Companions will get their own Name, Kind, stats, and pools - " +
                          "a lightweight character of their own.");
    }
}
