using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models;
using RPFramework.Models.Net;

namespace RPFramework.Windows;

public class CharacterSheetWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private bool _editMode = false;

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

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon        = FontAwesomeIcon.PencilAlt,
            ShowTooltip = () => ImGui.SetTooltip(_editMode ? "Exit template editor" : "Edit sheet template"),
            Click       = _ => _editMode = !_editMode,
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        string? pid = _plugin.LocalPlayerId;
        if (pid == null)
        {
            ImGui.TextDisabled("Log in to view your character sheet.");
            return;
        }

        var template = _plugin.Configuration.ActiveTemplate;

        if (_editMode)
        {
            DrawTemplateEditor(template, pid);
            return;
        }

        var   ch    = _plugin.GetOrCreateCharacter(pid);
        float scale = ImGuiHelpers.GlobalScale;
        bool  dirty = false;

        using var scroll = ImRaii.Child("##sheetscroll", new Vector2(-1, -1), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!scroll) goto save;

        foreach (var group in template.Groups)
        {
            DrawGroup(group, ch, template, scale, ref dirty);
            ImGuiHelpers.ScaledDummy(4f);
        }

        save:
        if (dirty)
        {
            _plugin.Configuration.Save();
            _plugin.PushLocalProfile();
        }
    }

    // ── Sheet rendering ────────────────────────────────────────────────────────

    private void DrawGroup(SheetGroup group, RpCharacter ch, SheetTemplate template, float scale, ref bool dirty)
    {
        ImGui.TextUnformatted(group.Name);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        var bars    = group.Fields.Where(f => f.Type == FieldType.Bar).ToList();
        var dots    = group.Fields.Where(f => f.Type == FieldType.Dot).ToList();
        var numbers = group.Fields.Where(f => f.Type == FieldType.Number).ToList();
        var checks  = group.Fields.Where(f => f.Type == FieldType.Checkbox).ToList();

        foreach (var f in bars)
        {
            DrawBarField(f, ch, template, scale, ref dirty);
            ImGui.Spacing();
        }

        if (bars.Any(f => f.IsApBar))
        {
            int apPen = SkillHelpers.ApPenalty(ch, template);
            if (apPen < 0)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), $"Exhausted: {apPen} to all stat rolls");
        }

        foreach (var f in dots)
        {
            DrawDotField(f, ch, scale, ref dirty);
            ImGui.Spacing();
        }

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
                    DrawNumberRow(numbers[i], i + 1 < numbers.Count ? numbers[i + 1] : null, ch, scale, ref dirty);

                ImGui.EndTable();
            }
        }

        if (checks.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0 || numbers.Count > 0) ImGui.Spacing();
            using var child = ImRaii.Child($"##checks_{group.Id}",
                new Vector2(-1, checks.Count <= 12 ? 0 : 150 * scale), false);
            foreach (var f in checks)
            {
                ch.CheckValues.TryGetValue(f.Id, out bool val);
                bool v = val;
                if (ImGui.Checkbox($"{f.Name}##ck_{f.Id}", ref v))
                { ch.CheckValues[f.Id] = v; dirty = true; }
                MaybeTooltip(f);
            }
        }
    }

    private static void DrawBarField(SheetField f, RpCharacter ch, SheetTemplate template,
                                     float scale, ref bool dirty)
    {
        string curKey = f.Id + ":cur";
        string maxKey = f.Id + ":max";
        ch.StatValues.TryGetValue(curKey, out int cur);
        ch.StatValues.TryGetValue(maxKey, out int max);

        int    bonus      = 0;
        string bonusLabel = "";
        if (f.BonusSourceFieldId != null)
        {
            var bonusField = template.FindField(f.BonusSourceFieldId);
            if (bonusField != null && ch.StatValues.TryGetValue(f.BonusSourceFieldId, out int bonusSrc))
            {
                bonus      = SkillHelpers.StatMod(bonusSrc);
                bonusLabel = bonusField.Name;
            }
        }

        int   effectiveMax = max + bonus;
        float fraction     = effectiveMax > 0 ? Math.Clamp((float)cur / effectiveMax, 0f, 1f) : 0f;
        var   color        = f.IsHpBar  ? new Vector4(0.20f, 0.70f, 0.20f, 1f)
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
        {
            ch.StatValues[curKey] = Math.Clamp(cur, 0, effectiveMax);
            dirty = true;
        }
        ImGui.SameLine(); ImGui.TextUnformatted("/"); ImGui.SameLine();
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputInt($"##rm_{f.Id}", ref max, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            max = Math.Clamp(max, 0, 9999);
            ch.StatValues[maxKey] = max;
            ch.StatValues[curKey] = Math.Clamp(cur, 0, max + bonus);
            dirty = true;
        }
        if (bonus != 0 && bonusLabel.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(bonus > 0 ? $"(+{bonus} {bonusLabel})" : $"({bonus} {bonusLabel})");
        }
    }

    private static void DrawNumberRow(SheetField f1, SheetField? f2, RpCharacter ch,
                                      float scale, ref bool dirty)
    {
        ImGui.TableNextRow();

        ch.StatValues.TryGetValue(f1.Id, out int v1);
        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(f1.Name); MaybeTooltip(f1);
        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(48 * scale);
        if (ImGui.InputInt($"##rn_{f1.Id}", ref v1, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
        { ch.StatValues[f1.Id] = Math.Clamp(v1, f1.Min, f1.Max); dirty = true; }
        if (f1.ShowModifier)
        {
            int m = SkillHelpers.StatMod(v1);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextDisabled(m >= 0 ? $"+{m}" : $"{m}");
        }

        if (f2 == null) return;

        ch.StatValues.TryGetValue(f2.Id, out int v2);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(f2.Name); MaybeTooltip(f2);
        ImGui.TableSetColumnIndex(4);
        ImGui.SetNextItemWidth(48 * scale);
        if (ImGui.InputInt($"##rn_{f2.Id}", ref v2, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
        { ch.StatValues[f2.Id] = Math.Clamp(v2, f2.Min, f2.Max); dirty = true; }
        if (f2.ShowModifier)
        {
            int m = SkillHelpers.StatMod(v2);
            ImGui.TableSetColumnIndex(5);
            ImGui.TextDisabled(m >= 0 ? $"+{m}" : $"{m}");
        }
    }

    private static void DrawDotField(SheetField f, RpCharacter ch, float scale, ref bool dirty)
    {
        string curKey = f.Id + ":cur";
        ch.StatValues.TryGetValue(curKey, out int cur);
        int dotMax = Math.Max(1, f.Max);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();

        var  drawList = ImGui.GetWindowDrawList();
        float r       = 6f * scale;
        float gap     = 3f * scale;
        int?  newVal  = null;

        // Vertically center the dots with the text line
        float dotH    = 2f * r;
        float baseY   = ImGui.GetCursorPosY();
        float yOffset = MathF.Max(0f, (ImGui.GetTextLineHeight() - dotH) * 0.5f);

        for (int i = 0; i < dotMax; i++)
        {
            if (i > 0) ImGui.SameLine(0f, gap);
            ImGui.SetCursorPosY(baseY + yOffset);
            ImGui.InvisibleButton($"##dot_{f.Id}_{i}", new Vector2(2f * r, 2f * r));

            if (ImGui.IsItemClicked())
                newVal = (i + 1 == cur) ? i : i + 1;

            bool filled  = i < cur;
            bool hovered = ImGui.IsItemHovered();
            var  center  = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;

            var fillColor = filled
                ? new Vector4(0.20f, 0.72f, 0.20f, 1f)
                : new Vector4(0.28f, 0.28f, 0.28f, 0.90f);
            if (hovered)
                fillColor = new Vector4(
                    Math.Min(1f, fillColor.X + 0.15f),
                    Math.Min(1f, fillColor.Y + 0.15f),
                    Math.Min(1f, fillColor.Z + 0.15f), 1f);

            drawList.AddCircleFilled(center, r - 1f, ImGui.ColorConvertFloat4ToU32(fillColor));
            drawList.AddCircle(center, r - 1f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.55f, 0.55f, 0.65f)),
                0, 1.2f * scale);
        }

        if (newVal.HasValue)
        { ch.StatValues[curKey] = newVal.Value; dirty = true; }
    }

    // ── Template editor ────────────────────────────────────────────────────────

    private void DrawTemplateEditor(SheetTemplate template, string pid)
    {
        float scale = ImGuiHelpers.GlobalScale;

        ImGui.TextUnformatted("Edit Sheet Template");
        ImGui.SameLine();

        bool isDm    = IsLocalPlayerDm(pid);
        float pubW   = 130 * scale;
        float availX = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availX - pubW);

        if (!isDm) ImGui.BeginDisabled();
        using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.18f, 0.45f, 0.75f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.60f, 0.90f, 1f)))
        {
            if (ImGui.Button("Publish ▶ Party##tmpl_pub", new Vector2(pubW, 0)))
                PublishTemplate(template, pid);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(isDm ? "Push this template to all party members."
                                  : "Only the DM can publish templates.");
        if (!isDm) ImGui.EndDisabled();

        ImGui.Separator();

        using var scroll = ImRaii.Child("##tmpl_scroll", new Vector2(-1, -1), false);
        if (!scroll) return;

        if (ImGui.Button("+ Add Group##tmpl_addgrp"))
        {
            template.Groups.Add(new SheetGroup());
            _plugin.Configuration.Save();
        }

        ImGuiHelpers.ScaledDummy(4f);

        int delGrp = -1;

        for (int gi = 0; gi < template.Groups.Count; gi++)
        {
            var g = template.Groups[gi];
            ImGui.PushID($"g{gi}");

            ImGui.SetNextItemWidth(140 * scale);
            string gname = g.Name;
            if (ImGui.InputText("##gn", ref gname, 64))
            { g.Name = gname; _plugin.Configuration.Save(); }

            ImGui.SameLine();
            if (gi > 0)
            {
                if (ImGui.ArrowButton("##gu", ImGuiDir.Up))
                { (template.Groups[gi - 1], template.Groups[gi]) = (g, template.Groups[gi - 1]); _plugin.Configuration.Save(); }
                ImGui.SameLine();
            }
            if (gi < template.Groups.Count - 1)
            {
                if (ImGui.ArrowButton("##gd", ImGuiDir.Down))
                { (template.Groups[gi], template.Groups[gi + 1]) = (template.Groups[gi + 1], g); _plugin.Configuration.Save(); }
                ImGui.SameLine();
            }
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.65f, 0.12f, 0.12f, 1f)))
            {
                if (ImGui.SmallButton("✕##gdel")) delGrp = gi;
            }

            using (ImRaii.PushIndent(10f * scale, false))
                DrawFieldEditorTable(g, template, scale);

            if (ImGui.Button($"+ Add Field##fadd{gi}"))
            { g.Fields.Add(new SheetField()); _plugin.Configuration.Save(); }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PopID();
        }

        if (delGrp >= 0)
        { template.Groups.RemoveAt(delGrp); _plugin.Configuration.Save(); }
    }

    private void DrawFieldEditorTable(SheetGroup group, SheetTemplate template, float scale)
    {
        if (group.Fields.Count == 0)
        {
            ImGui.TextDisabled("(no fields)");
            ImGui.Spacing();
            return;
        }

        var typeNames    = new[] { "Num", "Box", "Bar", "Dot" };
        var numberFields = template.Groups.SelectMany(g => g.Fields)
                                         .Where(f => f.Type == FieldType.Number)
                                         .ToList();
        var bonusOptions = new[] { "None" }.Concat(numberFields.Select(f => f.Name)).ToArray();

        int delFld = -1;

        for (int fi = 0; fi < group.Fields.Count; fi++)
        {
            var f = group.Fields[fi];
            ImGui.PushID($"f{fi}");

            // Name
            ImGui.SetNextItemWidth(100 * scale);
            string fname = f.Name;
            if (ImGui.InputText("##fn", ref fname, 32))
            { f.Name = fname; _plugin.Configuration.Save(); }

            ImGui.SameLine();

            // Type
            ImGui.SetNextItemWidth(50 * scale);
            int typeI = (int)f.Type;
            if (ImGui.Combo("##ft", ref typeI, typeNames, typeNames.Length))
            { f.Type = (FieldType)typeI; _plugin.Configuration.Save(); }

            ImGui.SameLine();

            switch (f.Type)
            {
                case FieldType.Number:
                    ImGui.SetNextItemWidth(38 * scale);
                    int fmin = f.Min;
                    if (ImGui.InputInt("##fmn", ref fmin, 0, 0))
                    { f.Min = fmin; _plugin.Configuration.Save(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Min");
                    ImGui.SameLine(); ImGui.TextUnformatted("-"); ImGui.SameLine();
                    ImGui.SetNextItemWidth(38 * scale);
                    int fmax = f.Max;
                    if (ImGui.InputInt("##fmx", ref fmax, 0, 0))
                    { f.Max = fmax; _plugin.Configuration.Save(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Max");
                    ImGui.SameLine();
                    bool showMod = f.ShowModifier;
                    if (ImGui.Checkbox("Mod##fm", ref showMod))
                    { f.ShowModifier = showMod; _plugin.Configuration.Save(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show D&D-style modifier");
                    ImGui.SameLine();
                    bool isInit = f.IsInitiativeStat;
                    if (ImGui.Checkbox("Init##fi", ref isInit))
                    {
                        if (isInit)
                            foreach (var g2 in template.Groups)
                                foreach (var other in g2.Fields)
                                    if (other.Id != f.Id) other.IsInitiativeStat = false;
                        f.IsInitiativeStat = isInit;
                        _plugin.Configuration.Save();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Used for initiative rolls");
                    break;

                case FieldType.Dot:
                    ImGui.SetNextItemWidth(38 * scale);
                    int dotMaxEd = f.Max > 0 ? f.Max : 5;
                    if (ImGui.InputInt("##fdotmax", ref dotMaxEd, 0, 0))
                    { f.Max = Math.Clamp(dotMaxEd, 1, 20); _plugin.Configuration.Save(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Number of dots (1–20)");
                    break;

                case FieldType.Bar:
                    int bonusIdx = 0;
                    for (int bi = 0; bi < numberFields.Count; bi++)
                        if (numberFields[bi].Id == f.BonusSourceFieldId) { bonusIdx = bi + 1; break; }
                    ImGui.SetNextItemWidth(68 * scale);
                    if (ImGui.Combo("##fb", ref bonusIdx, bonusOptions, bonusOptions.Length))
                    { f.BonusSourceFieldId = bonusIdx > 0 ? numberFields[bonusIdx - 1].Id : null; _plugin.Configuration.Save(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Bonus: StatMod of this field adds to bar max");
                    ImGui.SameLine();
                    bool isHp = f.IsHpBar;
                    if (ImGui.Checkbox("HP##fhp", ref isHp))
                    {
                        if (isHp) foreach (var g2 in template.Groups) foreach (var o in g2.Fields) if (o.Id != f.Id) o.IsHpBar = false;
                        f.IsHpBar = isHp; _plugin.Configuration.Save();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mark as HP bar (shown in initiative)");
                    ImGui.SameLine();
                    bool isAp = f.IsApBar;
                    if (ImGui.Checkbox("AP##fap", ref isAp))
                    {
                        if (isAp) foreach (var g2 in template.Groups) foreach (var o in g2.Fields) if (o.Id != f.Id) o.IsApBar = false;
                        f.IsApBar = isAp; _plugin.Configuration.Save();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mark as AP bar (used for exhaustion penalty)");
                    break;
            }

            ImGui.SameLine();
            if (fi > 0)
            {
                if (ImGui.ArrowButton("##fu", ImGuiDir.Up))
                { (group.Fields[fi - 1], group.Fields[fi]) = (f, group.Fields[fi - 1]); _plugin.Configuration.Save(); }
                ImGui.SameLine();
            }
            if (fi < group.Fields.Count - 1)
            {
                if (ImGui.ArrowButton("##fd", ImGuiDir.Down))
                { (group.Fields[fi], group.Fields[fi + 1]) = (group.Fields[fi + 1], f); _plugin.Configuration.Save(); }
                ImGui.SameLine();
            }
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.65f, 0.12f, 0.12f, 1f)))
            {
                if (ImGui.SmallButton("✕##fdel")) delFld = fi;
            }

            // Tooltip input on its own line
            using (ImRaii.PushIndent(8f * scale, false))
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f)))
            {
                ImGui.TextUnformatted("Tip:");
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            string ftt = f.Tooltip;
            if (ImGui.InputText("##ftt", ref ftt, 256))
            { f.Tooltip = ftt; _plugin.Configuration.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Tooltip shown to players when they hover over this field name.");

            ImGui.PopID();
        }

        if (delFld >= 0)
        { group.Fields.RemoveAt(delFld); _plugin.Configuration.Save(); }

        ImGui.Spacing();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void MaybeTooltip(SheetField f)
    {
        if (string.IsNullOrWhiteSpace(f.Tooltip) || !ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(320f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(f.Tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private bool IsLocalPlayerDm(string pid)
    {
        foreach (var (_, members) in _plugin.PartyMembers)
        {
            var me = members.FirstOrDefault(m => m.PlayerId == pid);
            if (me?.Role is PartyRole.Owner or PartyRole.CoDm) return true;
        }
        return false;
    }

    private void PublishTemplate(SheetTemplate template, string pid)
    {
        foreach (var party in _plugin.Configuration.Parties)
        {
            if (!_plugin.PartyMembers.TryGetValue(party.Code, out var members)) continue;
            var me = members.FirstOrDefault(m => m.PlayerId == pid);
            if (me?.Role is not (PartyRole.Owner or PartyRole.CoDm)) continue;
            Task.Run(() => _plugin.Network.PushSheetTemplateAsync(party.Code, template));
        }
    }
}
