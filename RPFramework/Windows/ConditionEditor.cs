using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Shared editor for a single <see cref="SkillCondition"/> row, used by both the skill editor and the
/// item-effects editor. Mirrors <see cref="EffectEditor"/>: the caller owns the table, the per-row
/// <c>PushID</c>, <c>TableNextRow</c>, and the trailing delete column; this fills the Field / Op /
/// Value / % cells (columns 0-3) and returns true if the condition changed this frame.
/// </summary>
public static class ConditionEditor
{
    private static readonly string[] OpNames = { "<", "≤", "=", "≥", ">" }; // matches ConditionOp order

    /// <summary>Fields a condition may test: numeric stats/pools plus proficiencies.</summary>
    public static List<SheetField> TargetFields(SheetTemplate template)
        => template.Groups.SelectMany(g => g.Fields)
                   .Where(f => f.Type is FieldType.Number or FieldType.Bar or FieldType.Dot or FieldType.Checkbox)
                   .ToList();

    public static bool DrawRow(SkillCondition c, List<SheetField> fields, string[] names)
    {
        bool changed = false;

        ImGui.TableSetColumnIndex(0); ImGui.SetNextItemWidth(-1);
        int fi = Math.Max(0, fields.FindIndex(f => f.Id == c.FieldId));
        if (ImGui.Combo("##cfld", ref fi, names, names.Length)) { c.FieldId = fields[fi].Id; changed = true; }

        ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(-1);
        int opI = (int)c.Op;
        if (ImGui.Combo("##cop", ref opI, OpNames, OpNames.Length)) { c.Op = (ConditionOp)opI; changed = true; }

        ImGui.TableSetColumnIndex(2); ImGui.SetNextItemWidth(-1);
        float v = c.Value;
        if (ImGui.InputFloat("##cval", ref v, 0f, 0f, "%.0f")) { c.Value = v; changed = true; }

        ImGui.TableSetColumnIndex(3);
        bool p = c.IsPercentage;
        if (ImGui.Checkbox("##cpct", ref p)) { c.IsPercentage = p; changed = true; }

        return changed;
    }
}
