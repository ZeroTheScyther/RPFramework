using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// The editable character sheet for the active campaign + the DM template editor. Reads the
/// authoritative character from the store; field edits are sent as intents. The template editor
/// works on a local draft and publishes it via TemplatePublish.
/// </summary>
public class CharacterSheetWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    private bool          _editMode;
    private SheetTemplate? _draft;     // working copy in the template editor
    private string?       _draftCode;

    public CharacterSheetWindow(Plugin plugin)
        : base("RP Character Sheet##RPFramework.CharSheet",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 480),
            MaximumSize = new Vector2(700, 1000),
        };
        Size          = new Vector2(400, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    /// <summary>Opens the window straight into the template editor for a campaign (DM entry from the Hub).</summary>
    /// <summary>DM entry from the Hub: switch campaign, enter template-edit mode, and open the unified window.</summary>
    public void OpenTemplateEditor(string code)
    {
        _plugin.SetActiveCampaign(code);
        EnterTemplateEditor(code);
        _plugin.RpCharacterWindow.IsOpen = true;
    }

    /// <summary>Enters template-edit mode without opening this (headless) window — the editor renders inside RpCharacterWindow.</summary>
    internal void EnterTemplateEditor(string code)
    {
        _draft     = CloneTemplate(_plugin.Store.TemplateOrDefault(code));
        _draftCode = code;
        _editMode  = true;
    }

    public override void Draw()
    {
        string? code = _plugin.ActiveCampaign;
        if (_plugin.LocalPlayerId == null || code == null)
        {
            ImGui.TextDisabled("Connect to a server to view your character sheet.");
            return;
        }

        DrawCampaignSelector(code);
        ImGui.Separator();

        // The live UI is RpCharacterWindow (which calls DrawProfile/DrawStats/DrawTemplateEditor
        // directly); this standalone window is kept only as the logic host. If opened on its own it
        // falls back to the stats view.
        if (_editMode && _draft != null && _draftCode == code)
            DrawTemplateEditor(code);
        else
            DrawStats(code);
    }

    /// <summary>True when the DM is editing this campaign's template (the Sheet tab shows the editor).</summary>
    internal bool IsEditingTemplate(string code) => _editMode && _draft != null && _draftCode == code;

    internal void DrawCampaignSelector(string code)
    {
        var parties = _plugin.Store.Parties.ToList();
        var party   = _plugin.Store.Party(code);

        // The DM template-edit entry now lives as a title-bar cog button on RpCharacterWindow.
        ImGui.SetNextItemWidth(-1);
        string preview = party?.Name ?? code;
        if (ImGui.BeginCombo("##campaignsel", preview))
        {
            foreach (var p in parties)
            {
                bool sel = p.Code == code;
                string label = p.IsPersonal ? $"{p.Name} (solo)" : p.Name;
                if (ImGui.Selectable($"{label}##camp_{p.Code}", sel)) _plugin.SetActiveCampaign(p.Code);
            }
            ImGui.EndCombo();
        }
    }

    // ── Sheet ───────────────────────────────────────────────────────────────────

    /// <summary>A group is "profile" content when every field is free-form Text (Name/Race/Job,
    /// Personality, Background, …); those render on the Profile tab. Everything else is the Stats tab.</summary>
    private static bool IsProfileGroup(SheetGroup g) => g.Fields.Count > 0 && g.Fields.All(f => f.Type == FieldType.Text);

    /// <summary>The Stats tab: pools, stats, specializations — every non-Text group. Always editable.
    /// The final group fills the leftover height so long lists (e.g. Specializations) don't cut off.</summary>
    internal void DrawStats(string code)
    {
        var template = _plugin.Store.TemplateOrDefault(code);
        var ch       = _plugin.Store.Character(code, _plugin.LocalPlayerId!);
        if (ch == null) { ImGui.TextDisabled("No character in this campaign yet."); return; }
        var   st    = ch.State;
        float scale = ImGuiHelpers.GlobalScale;

        using var scroll = ImRaii.Child("##sheet_stats", new Vector2(-1, -1), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!scroll) return;

        var groups = template.Groups.Where(g => !IsProfileGroup(g)).ToList();
        for (int i = 0; i < groups.Count; i++)
        {
            bool last = i == groups.Count - 1;
            DrawGroup(groups[i], st, template, code, scale, fillLast: last);
            if (!last) ImGuiHelpers.ScaledDummy(4f);
        }
    }

    // Profile / section styling.
    private static readonly Vector4 Accent     = new(0.55f, 0.78f, 1.00f, 1f); // section headers + accent stripe
    private static readonly Vector4 LabelMuted = new(0.60f, 0.64f, 0.72f, 1f); // field labels
    private static readonly Vector4 EmptyMuted = new(0.45f, 0.45f, 0.50f, 1f); // unset values

    /// <summary>A styled section header: a faint accent-tinted bar with a left accent stripe and the
    /// group name in the accent colour. Shared by the Stats groups and the Profile sections.</summary>
    private static void DrawSectionHeader(string name, float scale)
    {
        var   dl   = ImGui.GetWindowDrawList();
        var   p0   = ImGui.GetCursorScreenPos();
        float w    = ImGui.GetContentRegionAvail().X;
        float padY = 4f * scale;
        float h    = ImGui.GetTextLineHeight() + padY * 2;

        dl.AddRectFilled(p0, p0 + new Vector2(w, h),        ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f)), 4f * scale);
        dl.AddRectFilled(p0, p0 + new Vector2(3f * scale, h), ImGui.ColorConvertFloat4ToU32(Accent), 4f * scale);
        dl.AddText(p0 + new Vector2(10f * scale, padY), ImGui.ColorConvertFloat4ToU32(Accent), name);
        ImGui.Dummy(new Vector2(w, h));
        ImGuiHelpers.ScaledDummy(3f);
    }

    /// <summary>The Profile tab: the free-form Text groups. Read-only by default (like viewing another
    /// player); the title-bar pen toggles <paramref name="editable"/> so the owner can edit their fluff.</summary>
    internal void DrawProfile(string code, bool editable)
    {
        var template = _plugin.Store.TemplateOrDefault(code);
        var ch       = _plugin.Store.Character(code, _plugin.LocalPlayerId!);
        if (ch == null) { ImGui.TextDisabled("No character in this campaign yet."); return; }
        var   st    = ch.State;
        float scale = ImGuiHelpers.GlobalScale;

        if (editable) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Editing - click the pen again when done.");

        using var scroll = ImRaii.Child("##sheet_profile", new Vector2(-1, -1), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!scroll) return;

        foreach (var group in template.Groups.Where(IsProfileGroup))
        {
            DrawSectionHeader(group.Name, scale);
            using (ImRaii.PushIndent(8f * scale, false))
            {
                foreach (var f in group.Fields)
                {
                    if (editable) DrawTextField(f, st, code, scale);
                    else          DrawProfileValue(f, st, scale);
                    ImGuiHelpers.ScaledDummy(3f);
                }
            }
            ImGuiHelpers.ScaledDummy(8f);
        }
    }

    /// <summary>Read-only label/value for a profile Text field: muted label, bright value. Single-line
    /// fields (Name/Race/Job) align inline; multiline notes stack the value beneath the label.</summary>
    private void DrawProfileValue(SheetField f, CharacterState st, float scale)
    {
        st.TextValues.TryGetValue(f.Id, out string? val);
        bool empty = string.IsNullOrWhiteSpace(val);

        if (!f.Multiline)
        {
            ImGui.TextColored(LabelMuted, f.Name); MaybeTooltip(f);
            ImGui.SameLine(118f * scale);
            if (empty) ImGui.TextColored(EmptyMuted, "Not set");
            else       ImGui.TextUnformatted(val);
        }
        else
        {
            ImGui.TextColored(LabelMuted, f.Name); MaybeTooltip(f);
            if (empty) ImGui.TextColored(EmptyMuted, "Not set");
            else
            {
                ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
                ImGui.TextUnformatted(val);
                ImGui.PopTextWrapPos();
            }
        }
    }

    private void DrawGroup(SheetGroup group, CharacterState st, SheetTemplate template, string code, float scale, bool fillLast = false)
    {
        DrawSectionHeader(group.Name, scale);

        var bars    = group.Fields.Where(f => f.Type == FieldType.Bar).ToList();
        var dots    = group.Fields.Where(f => f.Type == FieldType.Dot).ToList();
        var numbers = group.Fields.Where(f => f.Type == FieldType.Number).ToList();
        var checks  = group.Fields.Where(f => f.Type == FieldType.Checkbox).ToList();
        var texts   = group.Fields.Where(f => f.Type == FieldType.Text).ToList();

        foreach (var f in bars) { DrawBarField(f, st, template, code, scale); ImGui.Spacing(); }

        if (bars.Any(f => f.IsApBar))
        {
            int apPen = StatMath.ApPenalty(st, template);
            if (apPen < 0) ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), $"Exhausted: {apPen} to all stat rolls");
        }

        foreach (var f in dots) { DrawDotField(f, st, code, scale); ImGui.Spacing(); }

        if (numbers.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0) ImGui.Spacing();
            if (ImGui.BeginTable($"##nums_{group.Id}", 6, ImGuiTableFlags.None))
            {
                ImGui.TableSetupColumn("##sl1", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
                ImGui.TableSetupColumn("##sv1", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("##sm1", ImGuiTableColumnFlags.WidthFixed, 58 * scale);
                ImGui.TableSetupColumn("##sl2", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
                ImGui.TableSetupColumn("##sv2", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("##sm2", ImGuiTableColumnFlags.WidthFixed, 58 * scale);
                for (int i = 0; i < numbers.Count; i += 2)
                    DrawNumberRow(numbers[i], i + 1 < numbers.Count ? numbers[i + 1] : null, st, code, template, scale);
                ImGui.EndTable();
            }
        }

        if (checks.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0 || numbers.Count > 0) ImGui.Spacing();
            // The last group on a tab fills the leftover height so a long checkbox list scrolls cleanly
            // instead of being boxed into a stub. Otherwise: auto-size when short, capped scroll when long.
            float checksH = fillLast ? -1 : (checks.Count <= 12 ? 0 : 150 * scale);
            using var child = ImRaii.Child($"##checks_{group.Id}", new Vector2(-1, checksH), false);
            foreach (var f in checks)
            {
                st.CheckValues.TryGetValue(f.Id, out bool baseVal);
                var  sources    = StatMath.CheckSources(st, f.Id, template);
                bool eff        = sources.Count > 0 ? sources[^1].Grant : baseVal;
                bool overridden = sources.Count > 0 && eff != baseVal;

                // The box shows the EFFECTIVE state; a click still toggles the player's own base value
                // (a gear/spell override keeps the box forced until that source is removed).
                bool v = eff;
                using (ImRaii.PushColor(ImGuiCol.Text, StatModified, overridden))
                    if (ImGui.Checkbox($"{f.Name}##ck_{f.Id}", ref v))
                        _ = _plugin.Network.CharacterEditCheck(code, f.Id, !baseVal);

                if (sources.Count > 0 && ImGui.IsItemHovered()) DrawCheckBreakdown(baseVal, sources);
                else MaybeTooltip(f);
            }
        }

        if (texts.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0 || numbers.Count > 0 || checks.Count > 0) ImGui.Spacing();
            foreach (var f in texts) DrawTextField(f, st, code, scale);
        }
    }

    /// <summary>A Text/notes field bound to CharacterState.TextValues, committed to the server on focus loss / Enter.</summary>
    private void DrawTextField(SheetField f, CharacterState st, string code, float scale)
    {
        st.TextValues.TryGetValue(f.Id, out string? cur);
        string buf = cur ?? "";
        ImGui.TextUnformatted(f.Name); MaybeTooltip(f);
        if (f.Multiline)
            ImGui.InputTextMultiline($"##txt_{f.Id}", ref buf, 4096, new Vector2(-1, 60 * scale));
        else
        { ImGui.SetNextItemWidth(-1); ImGui.InputText($"##txt_{f.Id}", ref buf, 256); }
        if (ImGui.IsItemDeactivatedAfterEdit())
            _ = _plugin.Network.CharacterEditText(code, f.Id, buf);
    }

    private void DrawBarField(SheetField f, CharacterState st, SheetTemplate template, string code, float scale)
    {
        string curKey = f.Id + ":cur", maxKey = f.Id + ":max";
        st.StatValues.TryGetValue(curKey, out int cur);
        st.StatValues.TryGetValue(maxKey, out int max);

        int   effectiveMax = StatMath.EffectiveBarMax(st, f, template); // stored max + stat bonus + equipped gear
        float fraction     = effectiveMax > 0 ? Math.Clamp((float)cur / effectiveMax, 0f, 1f) : 0f;
        var   color        = f.IsHpBar ? new Vector4(0.20f, 0.70f, 0.20f, 1f)
                           : f.IsApBar ? new Vector4(0.20f, 0.50f, 0.90f, 1f)
                                       : new Vector4(0.60f, 0.40f, 0.80f, 1f);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, color))
            ImGui.ProgressBar(fraction, new Vector2(-1, 14 * scale), "");

        float fieldW = 60 * scale;
        ImGui.SetNextItemWidth(fieldW);
        ImGui.InputInt($"##rc_{f.Id}", ref cur, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _ = _plugin.Network.CharacterEditStat(code, curKey, Math.Clamp(cur, 0, effectiveMax));
        ImGui.SameLine(); ImGui.TextUnformatted("/"); ImGui.SameLine();
        ImGui.SetNextItemWidth(fieldW);
        ImGui.InputInt($"##rm_{f.Id}", ref max, 0, 0);
        bool maxHover = ImGui.IsItemHovered();
        if (ImGui.IsItemDeactivatedAfterEdit())
            _ = _plugin.Network.CharacterEditStat(code, maxKey, Math.Clamp(max, 0, 9999));
        if (maxHover) DrawBarMaxBreakdown(st, f, template);

        // When gear/stat bonuses lift the cap above the stored max, show the effective max in orange
        // (same language as a modified stat); the full per-source breakdown is on hover.
        if (effectiveMax != max)
        {
            ImGui.SameLine(0f, 4f * scale);
            ImGui.TextColored(StatModified, $"({effectiveMax})");
            if (ImGui.IsItemHovered()) DrawBarMaxBreakdown(st, f, template);
        }
    }

    /// Tooltip listing what raises a bar's max: stored base, each stat/gear source, effective max.
    /// No-op when nothing lifts it.
    internal static void DrawBarMaxBreakdown(CharacterState st, SheetField f, SheetTemplate template)
    {
        var sources = StatMath.BarMaxSources(st, f, template);
        if (sources.Count == 0) return;

        st.StatValues.TryGetValue(f.Id + ":max", out int storedMax);
        ImGui.BeginTooltip();
        ImGui.TextDisabled($"Base {storedMax}");
        foreach (var (name, d) in sources)
            ImGui.TextColored(d > 0 ? StatBuff : StatDebuff, $"[{name}] {(d > 0 ? "+" : "")}{d}");
        ImGui.Separator();
        ImGui.TextUnformatted($"Max {StatMath.EffectiveBarMax(st, f, template)}");
        ImGui.EndTooltip();
    }

    private void DrawNumberRow(SheetField f1, SheetField? f2, CharacterState st, string code, SheetTemplate template, float scale)
    {
        ImGui.TableNextRow();

        st.StatValues.TryGetValue(f1.Id, out int v1);
        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(f1.Name); MaybeTooltip(f1);
        ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(48 * scale);
        ImGui.InputInt($"##rn_{f1.Id}", ref v1, 0, 0);
        bool h1 = ImGui.IsItemHovered();
        if (ImGui.IsItemDeactivatedAfterEdit())
            _ = _plugin.Network.CharacterEditStat(code, f1.Id, Math.Clamp(v1, f1.Min, f1.Max));
        if (h1) DrawStatBreakdown(st, f1, v1, template);
        if (f1.ShowModifier) { ImGui.TableSetColumnIndex(2); DrawModifier(st, f1, v1, template); }

        if (f2 == null) return;

        st.StatValues.TryGetValue(f2.Id, out int v2);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(f2.Name); MaybeTooltip(f2);
        ImGui.TableSetColumnIndex(4); ImGui.SetNextItemWidth(48 * scale);
        ImGui.InputInt($"##rn_{f2.Id}", ref v2, 0, 0);
        bool h2 = ImGui.IsItemHovered();
        if (ImGui.IsItemDeactivatedAfterEdit())
            _ = _plugin.Network.CharacterEditStat(code, f2.Id, Math.Clamp(v2, f2.Min, f2.Max));
        if (h2) DrawStatBreakdown(st, f2, v2, template);
        if (f2.ShowModifier) { ImGui.TableSetColumnIndex(5); DrawModifier(st, f2, v2, template); }
    }

    // Buff/debuff/override colors for stat math, shared by both sheets.
    internal static readonly Vector4 StatModified = new(1f,    0.65f, 0.2f,  1f); // orange: overwritten by effects
    private  static readonly Vector4 StatBuff     = new(0.45f, 0.85f, 0.45f, 1f);
    private  static readonly Vector4 StatDebuff   = new(0.85f, 0.45f, 0.45f, 1f);

    /// Renders a stat's roll contribution. When equipped gear / active passives change the stat, the
    /// EFFECTIVE value is shown in orange (signalling the base is overwritten) followed by the D&D
    /// roll modifier; otherwise just the modifier. Hover shows the per-source breakdown.
    internal static void DrawModifier(CharacterState st, SheetField f, int raw, SheetTemplate template)
    {
        int eff = StatMath.EffectiveStat(st, f.Id, template);
        int m   = StatMath.StatMod(eff);
        if (eff != raw)
        {
            ImGui.TextColored(StatModified, $"({eff})");
            ImGui.SameLine(0f, 4f * ImGuiHelpers.GlobalScale);
            ImGui.TextDisabled(m >= 0 ? $"+{m}" : $"{m}");
            if (ImGui.IsItemHovered()) DrawStatBreakdown(st, f, raw, template);
        }
        else
        {
            ImGui.TextDisabled(m >= 0 ? $"+{m}" : $"{m}");
        }
    }

    /// Tooltip listing every source contributing to a stat: base value, each gear/skill delta, total.
    /// No-op when nothing modifies the stat (so a plain hover over an unmodified field shows nothing).
    internal static void DrawStatBreakdown(CharacterState st, SheetField f, int raw, SheetTemplate template)
    {
        var sources = StatMath.StatSources(st, f.Id, template);
        if (sources.Count == 0) return;

        ImGui.BeginTooltip();
        ImGui.TextDisabled($"Base {raw}");
        foreach (var (name, d) in sources)
            ImGui.TextColored(d > 0 ? StatBuff : StatDebuff, $"[{name}] {(d > 0 ? "+" : "")}{d}");
        ImGui.Separator();
        int eff = StatMath.EffectiveStat(st, f.Id, template);
        int m   = StatMath.StatMod(eff);
        ImGui.TextUnformatted($"Total {eff} ({(m >= 0 ? "+" : "")}{m})");
        ImGui.EndTooltip();
    }

    /// Tooltip for a checkbox/proficiency that gear or a spell is forcing on/off: shows the player's
    /// own base setting and each source that grants or removes the proficiency.
    internal static void DrawCheckBreakdown(bool baseVal, List<(string Name, bool Grant)> sources)
    {
        ImGui.BeginTooltip();
        ImGui.TextDisabled(baseVal ? "Base: on" : "Base: off");
        foreach (var (name, grant) in sources)
            ImGui.TextColored(grant ? StatBuff : StatDebuff, $"[{name}] {(grant ? "grants" : "removes")}");
        ImGui.EndTooltip();
    }

    private void DrawDotField(SheetField f, CharacterState st, string code, float scale)
    {
        string curKey = f.Id + ":cur";
        st.StatValues.TryGetValue(curKey, out int cur);
        int dotMax = Math.Max(1, f.Max);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();

        var  drawList = ImGui.GetWindowDrawList();
        float r = 6f * scale, gap = 3f * scale;
        int?  newVal = null;
        float baseY = ImGui.GetCursorPosY();
        float yOffset = MathF.Max(0f, (ImGui.GetTextLineHeight() - 2f * r) * 0.5f);

        for (int i = 0; i < dotMax; i++)
        {
            if (i > 0) ImGui.SameLine(0f, gap);
            ImGui.SetCursorPosY(baseY + yOffset);
            ImGui.InvisibleButton($"##dot_{f.Id}_{i}", new Vector2(2f * r, 2f * r));
            if (ImGui.IsItemClicked()) newVal = (i + 1 == cur) ? i : i + 1;

            bool filled = i < cur, hovered = ImGui.IsItemHovered();
            var  center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
            var  fill   = filled ? new Vector4(0.20f, 0.72f, 0.20f, 1f) : new Vector4(0.28f, 0.28f, 0.28f, 0.90f);
            if (hovered) fill = new Vector4(Math.Min(1f, fill.X + 0.15f), Math.Min(1f, fill.Y + 0.15f), Math.Min(1f, fill.Z + 0.15f), 1f);
            drawList.AddCircleFilled(center, r - 1f, ImGui.ColorConvertFloat4ToU32(fill));
            drawList.AddCircle(center, r - 1f, ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.55f, 0.55f, 0.65f)), 0, 1.2f * scale);
        }

        if (newVal.HasValue) _ = _plugin.Network.CharacterEditStat(code, curKey, newVal.Value);
    }

    // ── Template editor (draft → publish) ────────────────────────────────────────

    internal void DrawTemplateEditor(string code)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var   template = _draft!;

        using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Lock().ImFont))
            ImGui.TextUnformatted(FontAwesomeIcon.ArrowLeft.ToIconString());
        if (ImGui.IsItemClicked()) { _editMode = false; _draft = null; return; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Discard changes & back to sheet");
        ImGui.SameLine();
        ImGui.TextUnformatted("Edit Sheet Template");

        ImGui.SameLine();
        float pubW = 130 * scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - pubW);
        using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.18f, 0.45f, 0.75f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.60f, 0.90f, 1f)))
            if (ImGui.Button("Publish ▶ Party##tmpl_pub", new Vector2(pubW, 0)))
            {
                _ = _plugin.Network.TemplatePublish(code, template);
                _editMode = false; _draft = null;
                return;
            }

        ImGui.Separator();
        using var scroll = ImRaii.Child("##tmpl_scroll", new Vector2(-1, -1), false);
        if (!scroll) return;

        if (ImGui.Button("+ Add Group##tmpl_addgrp")) template.Groups.Add(new SheetGroup());
        ImGuiHelpers.ScaledDummy(4f);

        int delGrp = -1;
        for (int gi = 0; gi < template.Groups.Count; gi++)
        {
            var g = template.Groups[gi];
            ImGui.PushID($"g{gi}");

            ImGui.SetNextItemWidth(140 * scale);
            string gname = g.Name;
            if (ImGui.InputText("##gn", ref gname, 64)) g.Name = gname;
            ImGui.SameLine();
            if (gi > 0) { if (ImGui.ArrowButton("##gu", ImGuiDir.Up)) (template.Groups[gi - 1], template.Groups[gi]) = (g, template.Groups[gi - 1]); ImGui.SameLine(); }
            if (gi < template.Groups.Count - 1) { if (ImGui.ArrowButton("##gd", ImGuiDir.Down)) (template.Groups[gi], template.Groups[gi + 1]) = (template.Groups[gi + 1], g); ImGui.SameLine(); }
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.65f, 0.12f, 0.12f, 1f)))
                if (ImGui.SmallButton("X##gdel")) delGrp = gi;

            using (ImRaii.PushIndent(10f * scale, false))
                DrawFieldEditorTable(g, template, scale);

            if (ImGui.Button($"+ Add Field##fadd{gi}")) g.Fields.Add(new SheetField());
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
            ImGui.PopID();
        }
        if (delGrp >= 0) template.Groups.RemoveAt(delGrp);
    }

    private void DrawFieldEditorTable(SheetGroup group, SheetTemplate template, float scale)
    {
        if (group.Fields.Count == 0) { ImGui.TextDisabled("(no fields)"); ImGui.Spacing(); return; }

        var typeNames    = new[] { "Num", "Box", "Bar", "Dot", "Txt" };
        var numberFields = template.Groups.SelectMany(g => g.Fields).Where(f => f.Type == FieldType.Number).ToList();
        var bonusOptions = new[] { "None" }.Concat(numberFields.Select(f => f.Name)).ToArray();

        int delFld = -1;
        for (int fi = 0; fi < group.Fields.Count; fi++)
        {
            var f = group.Fields[fi];
            ImGui.PushID($"f{fi}");

            ImGui.SetNextItemWidth(100 * scale);
            string fname = f.Name;
            if (ImGui.InputText("##fn", ref fname, 32)) f.Name = fname;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(50 * scale);
            int typeI = (int)f.Type;
            if (ImGui.Combo("##ft", ref typeI, typeNames, typeNames.Length)) f.Type = (FieldType)typeI;
            ImGui.SameLine();

            switch (f.Type)
            {
                case FieldType.Number:
                    ImGui.SetNextItemWidth(38 * scale);
                    int fmin = f.Min; if (ImGui.InputInt("##fmn", ref fmin, 0, 0)) f.Min = fmin;
                    ImGui.SameLine(); ImGui.TextUnformatted("-"); ImGui.SameLine();
                    ImGui.SetNextItemWidth(38 * scale);
                    int fmax = f.Max; if (ImGui.InputInt("##fmx", ref fmax, 0, 0)) f.Max = fmax;
                    ImGui.SameLine();
                    bool showMod = f.ShowModifier; if (ImGui.Checkbox("Mod##fm", ref showMod)) f.ShowModifier = showMod;
                    ImGui.SameLine();
                    bool isInit = f.IsInitiativeStat;
                    if (ImGui.Checkbox("Init##fi", ref isInit))
                    {
                        if (isInit) foreach (var g2 in template.Groups) foreach (var o in g2.Fields) if (o.Id != f.Id) o.IsInitiativeStat = false;
                        f.IsInitiativeStat = isInit;
                    }
                    break;

                case FieldType.Dot:
                    ImGui.SetNextItemWidth(38 * scale);
                    int dotMaxEd = f.Max > 0 ? f.Max : 5;
                    if (ImGui.InputInt("##fdotmax", ref dotMaxEd, 0, 0)) f.Max = Math.Clamp(dotMaxEd, 1, 20);
                    break;

                case FieldType.Bar:
                    int bonusIdx = 0;
                    for (int bi = 0; bi < numberFields.Count; bi++)
                        if (numberFields[bi].Id == f.BonusSourceFieldId) { bonusIdx = bi + 1; break; }
                    ImGui.SetNextItemWidth(68 * scale);
                    if (ImGui.Combo("##fb", ref bonusIdx, bonusOptions, bonusOptions.Length))
                        f.BonusSourceFieldId = bonusIdx > 0 ? numberFields[bonusIdx - 1].Id : null;
                    ImGui.SameLine();
                    bool isHp = f.IsHpBar;
                    if (ImGui.Checkbox("HP##fhp", ref isHp))
                    {
                        if (isHp) foreach (var g2 in template.Groups) foreach (var o in g2.Fields) if (o.Id != f.Id) o.IsHpBar = false;
                        f.IsHpBar = isHp;
                    }
                    ImGui.SameLine();
                    bool isAp = f.IsApBar;
                    if (ImGui.Checkbox("AP##fap", ref isAp))
                    {
                        if (isAp) foreach (var g2 in template.Groups) foreach (var o in g2.Fields) if (o.Id != f.Id) o.IsApBar = false;
                        f.IsApBar = isAp;
                    }
                    break;

                case FieldType.Text:
                    bool multi = f.Multiline;
                    if (ImGui.Checkbox("Multiline##ftxt", ref multi)) f.Multiline = multi;
                    break;
            }

            ImGui.SameLine();
            if (fi > 0) { if (ImGui.ArrowButton("##fu", ImGuiDir.Up)) (group.Fields[fi - 1], group.Fields[fi]) = (f, group.Fields[fi - 1]); ImGui.SameLine(); }
            if (fi < group.Fields.Count - 1) { if (ImGui.ArrowButton("##fd", ImGuiDir.Down)) (group.Fields[fi], group.Fields[fi + 1]) = (group.Fields[fi + 1], f); ImGui.SameLine(); }
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.65f, 0.12f, 0.12f, 1f)))
                if (ImGui.SmallButton("X##fdel")) delFld = fi;

            ImGui.TextDisabled("Tip:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            string ftt = f.Tooltip;
            if (ImGui.InputText("##ftt", ref ftt, 256)) f.Tooltip = ftt;

            ImGui.PopID();
        }
        if (delFld >= 0) group.Fields.RemoveAt(delFld);
        ImGui.Spacing();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static SheetTemplate CloneTemplate(SheetTemplate t)
        => JsonSerializer.Deserialize<SheetTemplate>(JsonSerializer.Serialize(t))!;

    private static void MaybeTooltip(SheetField f)
    {
        if (string.IsNullOrWhiteSpace(f.Tooltip) || !ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(320f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(f.Tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
