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

    /// <summary>Synthetic "On Turn End" target (an event marker, not a state predicate).</summary>
    private static SheetField TurnEndTarget() => new()
    {
        Id   = StatMath.OnTurnEndId,
        Name = "On Turn End",
        Type = FieldType.Number,
    };

    /// <summary>
    /// Fields a condition may test: numeric stats/pools plus proficiencies. When
    /// <paramref name="includeTurnEnd"/> is set, the synthetic "On Turn End" trigger is offered too
    /// (skill blocks only - it makes a block fire once per turn-end rather than continuously).
    /// </summary>
    public static List<SheetField> TargetFields(SheetTemplate template, bool includeTurnEnd = false)
    {
        var list = template.Groups.SelectMany(g => g.Fields)
                           .Where(f => f.Type is FieldType.Number or FieldType.Bar or FieldType.Dot or FieldType.Checkbox)
                           .ToList();
        if (includeTurnEnd) list.Add(TurnEndTarget());
        return list;
    }

    public static bool DrawRow(SkillCondition c, List<SheetField> fields, string[] names)
    {
        bool changed = false;
        var  field   = fields.FirstOrDefault(f => f.Id == c.FieldId);
        bool isCheck = field?.Type == FieldType.Checkbox;
        bool isEvent = c.FieldId == StatMath.OnTurnEndId;

        ImGui.TableSetColumnIndex(0); ImGui.SetNextItemWidth(-1);
        int fi = Math.Max(0, fields.FindIndex(f => f.Id == c.FieldId));
        if (ImGui.Combo("##cfld", ref fi, names, names.Length))
        {
            var picked = fields[fi];
            c.FieldId = picked.Id;
            // Reset to sensible defaults so a proficiency reads "is true", not "< 50%".
            if (picked.Type == FieldType.Checkbox)
            { c.Op = ConditionOp.Equal; c.Value = 1f; c.IsPercentage = false; }
            changed = true;
            isCheck = picked.Type == FieldType.Checkbox;
            isEvent = picked.Id == StatMath.OnTurnEndId;
        }

        // "On Turn End" is a pure event marker: no operator, value, or percentage.
        if (isEvent)
        {
            ImGui.TableSetColumnIndex(1); ImGui.TextDisabled("(event)");
            return changed;
        }

        if (isCheck)
        {
            // Proficiency test: a plain is true/false selector instead of a numeric comparison.
            if (c.Op != ConditionOp.Equal) { c.Op = ConditionOp.Equal; changed = true; }
            if (c.IsPercentage)            { c.IsPercentage = false;   changed = true; }
            ImGui.TableSetColumnIndex(1); ImGui.TextDisabled("is");
            ImGui.TableSetColumnIndex(2);
            bool on = c.Value >= 1f;
            if (ImGui.Checkbox("##cbool", ref on)) { c.Value = on ? 1f : 0f; changed = true; }
            ImGui.TableSetColumnIndex(3); // percentage N/A for a boolean
            return changed;
        }

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
