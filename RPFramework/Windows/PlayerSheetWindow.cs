using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Read-only character window for a remote player, presented with the same tabbed RPCHARACTER look
/// (Profile / Stats / Skills / Equipment) as the local character window — just non-editable. Reads
/// straight from the store: the server hydrates every campaign member's character, so no fetch is needed.
/// One instance per viewed player (raised again if reopened).
/// </summary>
public class PlayerSheetWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _playerId;
    private readonly string? _code;
    private readonly Action<string> _onClosed;

    private RpCharacterWindow.Tab _pendingTab;
    private bool _forceTab = true;
    private int  _skillSel = -1;

    public PlayerSheetWindow(Plugin plugin, string playerId, Action<string> onClosed,
                             RpCharacterWindow.Tab initialTab = RpCharacterWindow.Tab.Profile)
        : base($"Character Sheet##rpcs_{playerId}",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin     = plugin;
        _playerId   = playerId;
        _code       = plugin.ActiveCampaign;
        _onClosed   = onClosed;
        _pendingTab = initialTab;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 520),
            MaximumSize = new Vector2(700, 1000),
        };
        Size          = new Vector2(540, 740);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Jump an already-open window to a specific tab (used when "Open Skills" hits an open sheet).</summary>
    public void ShowTab(RpCharacterWindow.Tab tab) { _pendingTab = tab; _forceTab = true; }

    public override void OnClose() => _onClosed(_playerId);
    public void Dispose() { }

    /// <summary>Title the window after the character's name, like the local RPCHARACTER window.</summary>
    public override void PreDraw()
    {
        var ch = _code != null ? _plugin.Store.Character(_code, _playerId) : null;
        string title = ch?.DisplayName ?? "Character Sheet";
        if (ch != null && ch.State.TextValues.TryGetValue(WellKnownIds.Name, out string? nm) && !string.IsNullOrWhiteSpace(nm))
            title = nm!;
        WindowName = $"{title}##rpcs_{_playerId}";
    }

    public override void Draw()
    {
        var ch = _code != null ? _plugin.Store.Character(_code, _playerId) : null;
        if (ch == null)
        {
            ImGui.TextDisabled("This player is not in your active campaign.");
            return;
        }

        var   st       = ch.State;
        var   template = _plugin.Store.TemplateOrDefault(_code);
        float scale    = ImGuiHelpers.GlobalScale;

        using var tabs = ImRaii.TabBar("##rpcsremote_tabs");
        if (!tabs) return;
        DrawTab(RpCharacterWindow.Tab.Profile,   "Profile",   st, template, scale);
        DrawTab(RpCharacterWindow.Tab.Stats,     "Stats",     st, template, scale);
        DrawTab(RpCharacterWindow.Tab.Skills,    "Skills",    st, template, scale);
        DrawTab(RpCharacterWindow.Tab.Equipment, "Equipment", st, template, scale);
    }

    private void DrawTab(RpCharacterWindow.Tab tab, string label, CharacterState st, SheetTemplate template, float scale)
    {
        var flags = ImGuiTabItemFlags.None;
        if (_forceTab && _pendingTab == tab) flags |= ImGuiTabItemFlags.SetSelected;
        if (!ImGui.BeginTabItem($"{label}##rpcsremote_{tab}", flags)) return;
        if (_forceTab && _pendingTab == tab) _forceTab = false;

        // AlwaysVerticalScrollbar reserves the gutter so the content width can't oscillate (toggling the
        // vertical bar would otherwise re-trigger the horizontal bar every frame — the "flickering" loop).
        using (var body = ImRaii.Child($"##rpcsremote_body{tab}", new Vector2(-1, -1), false,
                                       ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.AlwaysVerticalScrollbar))
            if (body)
                switch (tab)
                {
                    case RpCharacterWindow.Tab.Profile:
                        DrawGroups(st, template, scale, CharacterSheetWindow.IsProfileGroup);
                        break;
                    case RpCharacterWindow.Tab.Stats:
                        DrawGroups(st, template, scale, g => !CharacterSheetWindow.IsProfileGroup(g));
                        break;
                    case RpCharacterWindow.Tab.Skills:
                        var granted = st.Equipment.Values
                            .Where(it => it.GrantedPassives is { Count: > 0 })
                            .SelectMany(it => it.GrantedPassives!.Select(p => (it.Name, p)))
                            .ToList();
                        // DM vault skills are author-only: hidden from players viewing someone else's sheet.
                        var visibleSkills = _plugin.IsDm(_code) ? st.Skills : st.Skills.Where(s => !s.IsDmSkill).ToList();
                        PlayerSkillsWindow.DrawReadOnly(template, visibleSkills, ref _skillSel, scale, granted);
                        break;
                    case RpCharacterWindow.Tab.Equipment:
                        DrawEquipmentReadOnly(st, template, scale);
                        break;
                }
        ImGui.EndTabItem();
    }

    // ── Read-only equipment paper-doll (mirrors RpCharacterWindow, no unequip controls) ──

    private static void DrawEquipmentReadOnly(CharacterState st, SheetTemplate template, float scale)
    {
        bool any = ItemSlots.EquipOrder.Any(s => st.Equipment.ContainsKey(s));
        if (!any) { ImGui.TextDisabled("Nothing equipped."); return; }

        float sz = 40 * scale;
        using var table = ImRaii.Table("##rpcsremote_equip", 2, ImGuiTableFlags.SizingFixedFit);
        if (!table) return;
        foreach (var slot in ItemSlots.EquipOrder)
        {
            ImGui.TableNextColumn();
            DrawEquipSlot(st, template, slot, sz);
        }
    }

    private static void DrawEquipSlot(CharacterState st, SheetTemplate template, RpItemType slot, float sz)
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
            if (item.Blocks is { Count: > 0 })
                foreach (var b in item.Blocks)
                {
                    string fx = ItemEffects.Summary(b.Effects, template);
                    if (string.IsNullOrEmpty(fx)) continue;
                    string cond = ItemEffects.ConditionSummary(b.Conditions, template);
                    bool   met  = StatMath.ConditionsMet(b.Conditions, st, template);
                    ImGui.TextColored(met ? new Vector4(0.45f, 0.85f, 0.45f, 1f) : new Vector4(0.65f, 0.65f, 0.65f, 1f),
                                      string.IsNullOrEmpty(cond) ? $"Always: {fx}" : $"If {cond}: {fx}");
                }
            if (item.GrantedPassives is { Count: > 0 })
                ImGui.TextColored(new Vector4(0.65f, 0.75f, 1f, 1f), $"Grants passive: {ItemEffects.GrantedPassivesSummary(item.GrantedPassives)}");
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                ImGui.PushTextWrapPos(260 * ImGuiHelpers.GlobalScale);
                ImGui.TextDisabled(item.Description);
                ImGui.PopTextWrapPos();
            }
            ImGui.EndTooltip();
        }

        ImGui.SameLine();
        using var g = ImRaii.Group();
        ImGui.TextDisabled(slot.Label());
        if (item != null) ImGui.TextUnformatted(item.Name);
        else              ImGui.TextDisabled("(empty)");
    }

    /// <summary>Renders a read-only sheet: each group (optionally filtered) that has any visible content.
    /// A group with only empty text fields is skipped entirely, and empty text fields are hidden, so a
    /// sparsely-filled profile shows only what's actually set. Reused by the Companion tab.</summary>
    public static void DrawGroups(CharacterState st, SheetTemplate template, float scale, Func<SheetGroup, bool>? filter = null)
    {
        foreach (var group in template.Groups)
        {
            if (filter != null && !filter(group)) continue;
            if (!HasContent(group, st)) continue;
            DrawGroup(group, st, template, scale);
            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    /// <summary>Whether a group has anything worth showing read-only: any non-text field, or any text
    /// field with a non-empty value.</summary>
    private static bool HasContent(SheetGroup g, CharacterState st)
    {
        foreach (var f in g.Fields)
        {
            if (f.Type != FieldType.Text) return true;
            if (st.TextValues.TryGetValue(f.Id, out var v) && !string.IsNullOrWhiteSpace(v)) return true;
        }
        return false;
    }

    public static void DrawGroup(SheetGroup group, CharacterState p, SheetTemplate template, float scale)
    {
        CharacterSheetWindow.DrawSectionHeader(group.Name, scale);

        var bars    = group.Fields.Where(f => f.Type == FieldType.Bar).ToList();
        var dots    = group.Fields.Where(f => f.Type == FieldType.Dot).ToList();
        var numbers = group.Fields.Where(f => f.Type == FieldType.Number).ToList();
        var checks  = group.Fields.Where(f => f.Type == FieldType.Checkbox).ToList();
        var texts   = group.Fields.Where(f => f.Type == FieldType.Text).ToList();

        foreach (var f in bars)
        {
            DrawBarField(f, p, template, scale);
            ImGui.Spacing();
        }

        var apField = bars.FirstOrDefault(bf => bf.IsApBar);
        if (apField != null)
        {
            int pen = StatMath.ApPenalty(p, template);
            if (pen < 0)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), $"Exhausted: {pen} to all stat rolls");
        }

        foreach (var f in dots)
        {
            DrawDotField(f, p, scale);
            ImGui.Spacing();
        }

        if (numbers.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0) ImGui.Spacing();
            if (ImGui.BeginTable($"##pnums_{group.Id}", 6, ImGuiTableFlags.None))
            {
                ImGui.TableSetupColumn("##sl1", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
                ImGui.TableSetupColumn("##sv1", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("##sm1", ImGuiTableColumnFlags.WidthFixed, 58 * scale);
                ImGui.TableSetupColumn("##sl2", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
                ImGui.TableSetupColumn("##sv2", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("##sm2", ImGuiTableColumnFlags.WidthFixed, 58 * scale);

                for (int i = 0; i < numbers.Count; i += 2)
                    DrawNumberRow(numbers[i], i + 1 < numbers.Count ? numbers[i + 1] : null, p, template, scale);

                ImGui.EndTable();
            }
        }

        if (checks.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0 || numbers.Count > 0) ImGui.Spacing();
            using var child = ImRaii.Child($"##pchecks_{group.Id}",
                new Vector2(-1, checks.Count <= 12 ? 0 : 150 * scale), false);
            foreach (var f in checks)
            {
                p.CheckValues.TryGetValue(f.Id, out bool baseVal);
                var  sources    = StatMath.CheckSources(p, f.Id, template);
                bool eff        = sources.Count > 0 ? sources[^1].Grant : baseVal;
                bool overridden = sources.Count > 0 && eff != baseVal;
                bool v = eff;
                using var _d = ImRaii.Disabled();
                using (ImRaii.PushColor(ImGuiCol.Text, CharacterSheetWindow.StatModified, overridden))
                    ImGui.Checkbox($"{f.Name}##pck_{f.Id}", ref v);
                if (sources.Count > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    CharacterSheetWindow.DrawCheckBreakdown(baseVal, sources);
                else MaybeTooltip(f);
            }
        }

        // Hide empty text fields entirely (only show what's actually filled in).
        var filledTexts = texts.Where(f => p.TextValues.TryGetValue(f.Id, out var v) && !string.IsNullOrWhiteSpace(v)).ToList();
        if (filledTexts.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0 || numbers.Count > 0 || checks.Count > 0) ImGui.Spacing();
            using var _ind = ImRaii.PushIndent(8f * scale, false);
            foreach (var f in filledTexts)
            {
                p.TextValues.TryGetValue(f.Id, out string? val);
                // Styled like the Profile tab: muted label; single-line values align inline, notes stack.
                ImGui.TextColored(CharacterSheetWindow.LabelMuted, f.Name); MaybeTooltip(f);
                if (!f.Multiline)
                {
                    ImGui.SameLine(118f * scale);
                    ImGui.TextUnformatted(val);
                }
                else
                {
                    ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
                    ImGui.TextUnformatted(val);
                    ImGui.PopTextWrapPos();
                }
                ImGuiHelpers.ScaledDummy(3f);
            }
        }
    }

    private static void DrawDotField(SheetField f, CharacterState p, float scale)
    {
        p.StatValues.TryGetValue(f.Id + ":cur", out int cur);
        int dotMax = Math.Max(1, f.Max);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();

        var  drawList = ImGui.GetWindowDrawList();
        float r       = 6f * scale;
        float gap     = 3f * scale;

        float dotH    = 2f * r;
        float baseY   = ImGui.GetCursorPosY();
        float yOffset = MathF.Max(0f, (ImGui.GetTextLineHeight() - dotH) * 0.5f);

        for (int i = 0; i < dotMax; i++)
        {
            if (i > 0) ImGui.SameLine(0f, gap);
            ImGui.SetCursorPosY(baseY + yOffset);
            var startPos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(2f * r, 2f * r));

            bool filled   = i < cur;
            var  center   = new Vector2(startPos.X + r, startPos.Y + r);
            var  fillColor = filled
                ? new Vector4(0.20f, 0.72f, 0.20f, 1f)
                : new Vector4(0.28f, 0.28f, 0.28f, 0.90f);

            drawList.AddCircleFilled(center, r - 1f, ImGui.ColorConvertFloat4ToU32(fillColor));
            drawList.AddCircle(center, r - 1f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.55f, 0.55f, 0.65f)),
                0, 1.2f * scale);
        }
    }

    private static void MaybeTooltip(SheetField f)
    {
        if (string.IsNullOrWhiteSpace(f.Tooltip) || !ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(320f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(f.Tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static void DrawBarField(SheetField f, CharacterState p, SheetTemplate template, float scale)
    {
        p.StatValues.TryGetValue(f.Id + ":cur", out int cur);

        int   effectiveMax = StatMath.EffectiveBarMax(p, f, template); // stored max + stat bonus + equipped gear
        float fraction     = effectiveMax > 0 ? Math.Clamp((float)cur / effectiveMax, 0f, 1f) : 0f;
        var   color        = f.IsHpBar  ? new Vector4(0.20f, 0.70f, 0.20f, 1f)
                           : f.IsApBar ? new Vector4(0.20f, 0.50f, 0.90f, 1f)
                                       : new Vector4(0.60f, 0.40f, 0.80f, 1f);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, color))
            ImGui.ProgressBar(fraction, new Vector2(-1, 14 * scale), "");

        p.StatValues.TryGetValue(f.Id + ":max", out int storedMax);
        bool lifted = effectiveMax != storedMax;
        ImGui.TextDisabled($"{cur} / "); ImGui.SameLine(0f, 0f);
        if (lifted) ImGui.TextColored(CharacterSheetWindow.StatModified, $"{effectiveMax}");
        else        ImGui.TextDisabled($"{effectiveMax}");
        if (lifted && ImGui.IsItemHovered()) CharacterSheetWindow.DrawBarMaxBreakdown(p, f, template);
    }

    private static void DrawNumberRow(SheetField f1, SheetField? f2, CharacterState p, SheetTemplate template, float scale)
    {
        ImGui.TableNextRow();

        p.StatValues.TryGetValue(f1.Id, out int v1);
        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(f1.Name); MaybeTooltip(f1);
        ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{v1}");
        if (ImGui.IsItemHovered()) CharacterSheetWindow.DrawStatBreakdown(p, f1, v1, template);
        if (f1.ShowModifier) { ImGui.TableSetColumnIndex(2); CharacterSheetWindow.DrawModifier(p, f1, v1, template); }

        if (f2 == null) return;

        p.StatValues.TryGetValue(f2.Id, out int v2);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(f2.Name); MaybeTooltip(f2);
        ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted($"{v2}");
        if (ImGui.IsItemHovered()) CharacterSheetWindow.DrawStatBreakdown(p, f2, v2, template);
        if (f2.ShowModifier) { ImGui.TableSetColumnIndex(5); CharacterSheetWindow.DrawModifier(p, f2, v2, template); }
    }
}
