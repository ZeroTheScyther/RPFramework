using System.Collections.Generic;
using System.Linq;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Shared formatting for item stat effects (equipment = always-on, consumables = applied on use).
/// Produces a compact human summary like "+2 STR, +10 HP, x2 DEX" for tooltips across the inventory,
/// equipment slots, and the character sheet.
/// </summary>
public static class ItemEffects
{
    public static string Summary(IEnumerable<SkillEffect> effects, SheetTemplate? template)
    {
        var parts = new List<string>();
        foreach (var fx in effects)
        {
            var (baseId, targetMax) = StatMath.SplitBarTarget(fx.FieldId);
            var    sf    = template?.FindField(baseId);
            string field = sf == null ? fx.FieldId : targetMax ? "Max" + sf.Name : sf.Name;

            // Proficiency (Checkbox) effects are a plain grant/remove, not arithmetic.
            if (sf?.Type == FieldType.Checkbox)
            {
                parts.Add(fx.Value >= 1f ? $"Grants {field}" : $"Removes {field}");
                continue;
            }

            string val   = fx.Value.ToString("0.##");
            string pct   = fx.IsPercentage ? "%" : "";
            string text = fx.Op switch
            {
                EffectOp.Add      => $"+{val}{pct}",
                EffectOp.Subtract => $"-{val}{pct}",
                EffectOp.Set      => $"={val}{pct}",
                EffectOp.Multiply => $"x{val}",
                EffectOp.Divide   => $"/{val}",
                _                 => val,
            };
            parts.Add(string.IsNullOrEmpty(field) ? text : $"{text} {field}");
        }
        return string.Join(", ", parts);
    }

    private static readonly string[] CondOps = { "<", "<=", "=", ">=", ">" };

    /// <summary>
    /// Compact human summary of an equipped item's gate conditions, e.g. "HP &lt;= 50%, STR &gt;= 12".
    /// Returns "" when there are none (item is always-on).
    /// </summary>
    public static string ConditionSummary(IEnumerable<SkillCondition>? conditions, SheetTemplate? template)
    {
        if (conditions == null) return "";
        var parts = new List<string>();
        foreach (var c in conditions)
        {
            if (c.FieldId == StatMath.OnTurnEndId) { parts.Add("On Turn End"); continue; }

            var    sf    = template?.FindField(c.FieldId);
            string field = sf?.Name ?? c.FieldId;

            // Proficiency (Checkbox) tests read "X is true/false", not a numeric comparison.
            if (sf?.Type == FieldType.Checkbox)
            {
                parts.Add($"{field} is {(c.Value >= 1f ? "true" : "false")}");
                continue;
            }

            string op  = c.Op is >= 0 and <= ConditionOp.Greater ? CondOps[(int)c.Op] : "?";
            string pct = c.IsPercentage ? "%" : "";
            parts.Add($"{field} {op} {c.Value:0.##}{pct}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// One human line per conditional block, e.g. "If HP &lt;= 50%: +20 STR" (or "Always: ..." when a
    /// block has no conditions). Empty when there are no blocks. Used to extend item tooltips.
    /// </summary>
    public static List<string> BlockLines(IEnumerable<EffectBlock>? blocks, SheetTemplate? template)
    {
        var lines = new List<string>();
        if (blocks == null) return lines;
        foreach (var b in blocks)
        {
            string fx = Summary(b.Effects, template);
            if (string.IsNullOrEmpty(fx)) continue;
            string cond = ConditionSummary(b.Conditions, template);
            lines.Add(string.IsNullOrEmpty(cond) ? $"Always: {fx}" : $"If {cond}: {fx}");
        }
        return lines;
    }
}
