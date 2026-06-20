using System;
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
    public void OpenTemplateEditor(string code)
    {
        _plugin.SetActiveCampaign(code);
        _draft     = CloneTemplate(_plugin.Store.TemplateOrDefault(code));
        _draftCode = code;
        _editMode  = true;
        IsOpen     = true;
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

        if (_editMode && _draft != null && _draftCode == code)
            DrawTemplateEditor(code);
        else
            DrawSheet(code);
    }

    private void DrawCampaignSelector(string code)
    {
        var parties = _plugin.Store.Parties.ToList();
        var party   = _plugin.Store.Party(code);

        bool isDm = _plugin.IsDm(code) && party is { IsPersonal: false };
        if (isDm)
        {
            ImGui.AlignTextToFramePadding();
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Lock().ImFont))
                ImGui.TextUnformatted(FontAwesomeIcon.PencilAlt.ToIconString());
            if (ImGui.IsItemClicked()) { _draft = CloneTemplate(_plugin.Store.TemplateOrDefault(code)); _draftCode = code; _editMode = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit sheet template (DM)");
            ImGui.SameLine();
        }

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

    private void DrawSheet(string code)
    {
        var template = _plugin.Store.TemplateOrDefault(code);
        var ch       = _plugin.Store.Character(code, _plugin.LocalPlayerId!);
        if (ch == null) { ImGui.TextDisabled("No character in this campaign yet."); return; }
        var   st    = ch.State;
        float scale = ImGuiHelpers.GlobalScale;

        using var scroll = ImRaii.Child("##sheetscroll", new Vector2(-1, -1), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!scroll) return;

        foreach (var group in template.Groups)
        {
            DrawGroup(group, st, template, code, scale);
            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private void DrawGroup(SheetGroup group, CharacterState st, SheetTemplate template, string code, float scale)
    {
        ImGui.TextUnformatted(group.Name);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

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
                ImGui.TableSetupColumn("##sm1", ImGuiTableColumnFlags.WidthFixed, 34 * scale);
                ImGui.TableSetupColumn("##sl2", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
                ImGui.TableSetupColumn("##sv2", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("##sm2", ImGuiTableColumnFlags.WidthFixed, 34 * scale);
                for (int i = 0; i < numbers.Count; i += 2)
                    DrawNumberRow(numbers[i], i + 1 < numbers.Count ? numbers[i + 1] : null, st, code, scale);
                ImGui.EndTable();
            }
        }

        if (checks.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0 || numbers.Count > 0) ImGui.Spacing();
            using var child = ImRaii.Child($"##checks_{group.Id}", new Vector2(-1, checks.Count <= 12 ? 0 : 150 * scale), false);
            foreach (var f in checks)
            {
                st.CheckValues.TryGetValue(f.Id, out bool val);
                bool v = val;
                if (ImGui.Checkbox($"{f.Name}##ck_{f.Id}", ref v))
                    _ = _plugin.Network.CharacterEditCheck(code, f.Id, v);
                MaybeTooltip(f);
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

        int bonus = 0; string bonusLabel = "";
        if (f.BonusSourceFieldId != null)
        {
            var bonusField = template.FindField(f.BonusSourceFieldId);
            if (bonusField != null && st.StatValues.TryGetValue(f.BonusSourceFieldId, out int bonusSrc))
            { bonus = StatMath.StatMod(bonusSrc); bonusLabel = bonusField.Name; }
        }

        int   effectiveMax = max + bonus;
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
        if (ImGui.InputInt($"##rc_{f.Id}", ref cur, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
            _ = _plugin.Network.CharacterEditStat(code, curKey, Math.Clamp(cur, 0, effectiveMax));
        ImGui.SameLine(); ImGui.TextUnformatted("/"); ImGui.SameLine();
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputInt($"##rm_{f.Id}", ref max, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
            _ = _plugin.Network.CharacterEditStat(code, maxKey, Math.Clamp(max, 0, 9999));
        if (bonus != 0 && bonusLabel.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(bonus > 0 ? $"(+{bonus} {bonusLabel})" : $"({bonus} {bonusLabel})");
        }
    }

    private void DrawNumberRow(SheetField f1, SheetField? f2, CharacterState st, string code, float scale)
    {
        ImGui.TableNextRow();

        st.StatValues.TryGetValue(f1.Id, out int v1);
        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(f1.Name); MaybeTooltip(f1);
        ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(48 * scale);
        if (ImGui.InputInt($"##rn_{f1.Id}", ref v1, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
            _ = _plugin.Network.CharacterEditStat(code, f1.Id, Math.Clamp(v1, f1.Min, f1.Max));
        if (f1.ShowModifier)
        {
            int m = StatMath.StatMod(v1);
            ImGui.TableSetColumnIndex(2); ImGui.TextDisabled(m >= 0 ? $"+{m}" : $"{m}");
        }

        if (f2 == null) return;

        st.StatValues.TryGetValue(f2.Id, out int v2);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(f2.Name); MaybeTooltip(f2);
        ImGui.TableSetColumnIndex(4); ImGui.SetNextItemWidth(48 * scale);
        if (ImGui.InputInt($"##rn_{f2.Id}", ref v2, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
            _ = _plugin.Network.CharacterEditStat(code, f2.Id, Math.Clamp(v2, f2.Min, f2.Max));
        if (f2.ShowModifier)
        {
            int m = StatMath.StatMod(v2);
            ImGui.TableSetColumnIndex(5); ImGui.TextDisabled(m >= 0 ? $"+{m}" : $"{m}");
        }
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

    private void DrawTemplateEditor(string code)
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
