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
            string field = template?.FindField(fx.FieldId)?.Name ?? fx.FieldId;
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
}
