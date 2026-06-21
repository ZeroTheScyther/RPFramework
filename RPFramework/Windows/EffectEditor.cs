using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Shared editor for a single <see cref="SkillEffect"/> row, used by both the skill editor and the
/// item-effects editor. Numeric fields (Number/Bar/Dot) get the full op set (+ - = x /) plus a
/// percentage toggle; proficiency fields (Checkbox) collapse to a Set-only true/false grant, since
/// the stat engine only honours Set on a checkbox. Text fields are never effect targets.
/// </summary>
public static class EffectEditor
{
    private static readonly string[] OpNames = { "+", "-", "=", "x", "/" };

    /// <summary>
    /// Fields an effect may target: numeric stats, dots, proficiencies, and pools. Each bar is split
    /// into two explicit targets to remove the current-vs-max ambiguity: "HP" (current value) and
    /// "MaxHP" (the cap). <paramref name="includeBarCurrent"/> is false for equipment, whose always-on
    /// effects only ever raise the max — a permanent change to a live current value is meaningless.
    /// </summary>
    public static List<SheetField> TargetFields(SheetTemplate template, bool includeBarCurrent = true)
    {
        var list = new List<SheetField>();
        foreach (var f in template.Groups.SelectMany(g => g.Fields))
        {
            switch (f.Type)
            {
                case FieldType.Bar:
                    if (includeBarCurrent) list.Add(f);  // current: bare id, name as-is ("HP")
                    list.Add(MaxTarget(f));              // max: "{id}:max", name "MaxHP"
                    break;
                case FieldType.Number:
                case FieldType.Dot:
                case FieldType.Checkbox:
                    list.Add(f);
                    break;
            }
        }
        return list;
    }

    /// <summary>Synthetic target representing a bar's maximum (id "{barId}:max", name "Max{barName}").</summary>
    private static SheetField MaxTarget(SheetField bar) => new()
    {
        Id   = bar.Id + StatMath.MaxSuffix,
        Name = "Max" + bar.Name,
        Type = FieldType.Bar,
    };

    /// <summary>
    /// Draws the Field / Op / Value / % cells (columns 0-3) for one effect row. The caller owns the
    /// table, the per-row <c>PushID</c>, <c>TableNextRow</c>, and the trailing delete column. Returns
    /// true if the effect changed this frame.
    /// </summary>
    public static bool DrawRow(SkillEffect fx, List<SheetField> fields, string[] names)
    {
        bool changed = false;
        var  field   = fields.FirstOrDefault(f => f.Id == fx.FieldId);
        bool isCheck = field?.Type == FieldType.Checkbox;

        ImGui.TableSetColumnIndex(0); ImGui.SetNextItemWidth(-1);
        int fi = System.Math.Max(0, fields.FindIndex(f => f.Id == fx.FieldId));
        if (ImGui.Combo("##efld", ref fi, names, names.Length))
        {
            var picked = fields[fi];
            fx.FieldId = picked.Id;
            if (picked.Type == FieldType.Checkbox) { fx.Op = EffectOp.Set; fx.IsPercentage = false; } // proficiency = grant
            changed = true;
            isCheck = picked.Type == FieldType.Checkbox;
        }

        if (isCheck)
        {
            // Proficiency grant: forced Set, value is a plain true/false. No op picker, no percentage.
            if (fx.Op != EffectOp.Set)   { fx.Op = EffectOp.Set;      changed = true; }
            if (fx.IsPercentage)         { fx.IsPercentage = false;   changed = true; }
            ImGui.TableSetColumnIndex(1); ImGui.TextDisabled("grant");
            ImGui.TableSetColumnIndex(2);
            bool grant = fx.Value >= 1f;
            if (ImGui.Checkbox("##egrant", ref grant)) { fx.Value = grant ? 1f : 0f; changed = true; }
            ImGui.TableSetColumnIndex(3); // percentage N/A for a boolean
        }
        else
        {
            ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(-1);
            int opI = (int)fx.Op;
            if (ImGui.Combo("##eop", ref opI, OpNames, OpNames.Length)) { fx.Op = (EffectOp)opI; changed = true; }
            ImGui.TableSetColumnIndex(2); ImGui.SetNextItemWidth(-1);
            float v = fx.Value;
            if (ImGui.InputFloat("##eval", ref v, 0f, 0f, "%.1f")) { fx.Value = v; changed = true; }
            ImGui.TableSetColumnIndex(3);
            bool p = fx.IsPercentage;
            if (ImGui.Checkbox("##epct", ref p)) { fx.IsPercentage = p; changed = true; }
        }
        return changed;
    }
}
