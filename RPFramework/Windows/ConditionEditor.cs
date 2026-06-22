using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Shared editor for a single <see cref="SkillCondition"/> row. Mirrors <see cref="EffectEditor"/>'s
/// Category / Field / Op / Value / % layout: Stats test a numeric comparison, Specializations test a
/// plain is true/false. The "On Turn End" trigger is no longer a condition — it is a block-level Trigger
/// selector (see <see cref="BlockListEditor"/>). The caller owns the table, the per-row <c>PushID</c>,
/// <c>TableNextRow</c>, and the trailing delete column.
/// </summary>
public static class ConditionEditor
{
    private static readonly string[] OpNames = { "<", "≤", "=", "≥", ">" }; // matches ConditionOp order

    /// <summary>Numeric condition targets: Number / Bar / Dot fields.</summary>
    public static List<SheetField> StatFields(SheetTemplate template)
        => template.Groups.SelectMany(g => g.Fields)
                   .Where(f => f.Type is FieldType.Number or FieldType.Bar or FieldType.Dot).ToList();

    /// <summary>Every field a condition may test (numeric + proficiencies) — flat list for summaries.</summary>
    public static List<SheetField> TargetFields(SheetTemplate template)
    {
        var list = StatFields(template);
        list.AddRange(EffectEditor.SpecFields(template));
        return list;
    }

    /// <summary>A fresh condition targeting the first numeric field (used by "+ condition").</summary>
    public static SkillCondition NewCondition(SheetTemplate template)
        => new() { FieldId = StatFields(template).FirstOrDefault()?.Id ?? "" };

    public static bool DrawRow(SkillCondition c, SheetTemplate template)
    {
        bool changed = false;
        var  stats   = StatFields(template);
        var  specs   = EffectEditor.SpecFields(template);
        bool isSpec  = specs.Any(f => f.Id == c.FieldId);

        // Column 0 — Category (Stat / Spec).
        ImGui.TableSetColumnIndex(0); ImGui.SetNextItemWidth(-1);
        int ci = isSpec ? 1 : 0;
        if (ImGui.Combo("##ccat", ref ci, new[] { "Stat", "Spec." }, 2))
        {
            bool wantSpec = ci == 1;
            if (wantSpec != isSpec)
            {
                if (wantSpec) { c.FieldId = specs.FirstOrDefault()?.Id ?? ""; c.Op = ConditionOp.Equal; c.Value = 1f; c.IsPercentage = false; }
                else          { c.FieldId = stats.FirstOrDefault()?.Id ?? ""; c.Op = ConditionOp.LessEqual; c.Value = 50f; c.IsPercentage = true; }
                changed = true; isSpec = wantSpec;
            }
        }

        if (isSpec)
        {
            if (c.Op != ConditionOp.Equal) { c.Op = ConditionOp.Equal; changed = true; }
            if (c.IsPercentage)            { c.IsPercentage = false;   changed = true; }
            var names = specs.Select(f => f.Name).ToArray();
            ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(-1);
            int fi = Math.Max(0, specs.FindIndex(f => f.Id == c.FieldId));
            if (names.Length > 0 && ImGui.Combo("##cspec", ref fi, names, names.Length)) { c.FieldId = specs[fi].Id; changed = true; }
            ImGui.TableSetColumnIndex(2); ImGui.TextDisabled("is");
            ImGui.TableSetColumnIndex(3);
            bool on = c.Value >= 1f;
            if (ImGui.Checkbox("##cbool", ref on)) { c.Value = on ? 1f : 0f; changed = true; }
            ImGui.TableSetColumnIndex(4);
            return changed;
        }

        var statNames = stats.Select(f => f.Name).ToArray();
        ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(-1);
        int si = Math.Max(0, stats.FindIndex(f => f.Id == c.FieldId));
        if (statNames.Length > 0 && ImGui.Combo("##cfld", ref si, statNames, statNames.Length)) { c.FieldId = stats[si].Id; changed = true; }

        ImGui.TableSetColumnIndex(2); ImGui.SetNextItemWidth(-1);
        int opI = (int)c.Op;
        if (ImGui.Combo("##cop", ref opI, OpNames, OpNames.Length)) { c.Op = (ConditionOp)opI; changed = true; }
        ImGui.TableSetColumnIndex(3); ImGui.SetNextItemWidth(-1);
        float v = c.Value;
        if (ImGui.InputFloat("##cval", ref v, 0f, 0f, "%.0f")) { c.Value = v; changed = true; }
        ImGui.TableSetColumnIndex(4);
        bool p = c.IsPercentage;
        if (ImGui.Checkbox("##cpct", ref p)) { c.IsPercentage = p; changed = true; }
        return changed;
    }
}
