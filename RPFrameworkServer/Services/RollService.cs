using RPFramework.Contracts;

namespace RPFrameworkServer.Services;

/// <summary>
/// Server-authoritative dice + stat math. The server owns the RNG and applies every
/// rule (stat modifier, specialization advantage, AP-exhaustion penalty, advantage /
/// disadvantage) so clients cannot fudge results and the math lives in exactly one place.
/// </summary>
public sealed class RollService
{
    /// <summary>
    /// Rolls a die for a character and returns the authoritative result.
    /// <paramref name="statFieldId"/> / <paramref name="specFieldId"/> may be empty for a
    /// plain "quick roll" (no modifier, no AP penalty).
    /// </summary>
    public DiceRollResultDto Roll(
        string         campaignCode,
        string         playerId,
        string         displayName,
        int            die,
        RollMode       requestedMode,
        string?        statFieldId,
        string?        specFieldId,
        CharacterState state,
        SheetTemplate  template)
    {
        die = Math.Clamp(die, 2, 1000);
        bool hasStat = !string.IsNullOrEmpty(statFieldId) && template.FindField(statFieldId!) != null;

        // ── Specialization proficiency adjusts the effective mode ──────────────
        // Proficiency grants advantage; it cancels a disadvantage to a normal roll.
        RollMode mode = requestedMode;
        // Effective proficiency folds in gear/spell grants, not just the player's own base checkbox.
        bool proficient = !string.IsNullOrEmpty(specFieldId)
                          && StatMath.EffectiveCheck(state, specFieldId!, template);
        if (proficient)
            mode = requestedMode switch
            {
                RollMode.Disadvantage => RollMode.Normal,     // cancelled
                _                     => RollMode.Advantage,  // normal/advantage → advantage
            };

        // ── Roll ────────────────────────────────────────────────────────────────
        var rolls = new List<int>();
        rolls.Add(RollOne(die));
        if (mode != RollMode.Normal)
            rolls.Add(RollOne(die));

        int kept = mode switch
        {
            RollMode.Advantage    => rolls.Max(),
            RollMode.Disadvantage => rolls.Min(),
            _                     => rolls[0],
        };

        // ── Modifier (only for stat rolls) ───────────────────────────────────────
        int modifier = 0;
        if (hasStat)
            modifier += StatMath.StatMod(StatMath.EffectiveStat(state, statFieldId!, template))
                      + StatMath.ApPenalty(state, template);

        int total = kept + modifier;

        return new DiceRollResultDto(
            campaignCode, playerId, displayName,
            die, rolls, kept, modifier, total, mode,
            FormatMessage(die, rolls, kept, modifier, total, mode,
                          hasStat ? template.FindField(statFieldId!)?.Name : null,
                          proficient ? template.FindField(specFieldId!)?.Name : null));
    }

    private static int RollOne(int die) => Random.Shared.Next(1, die + 1);

    private static string FormatMessage(
        int die, List<int> rolls, int kept, int modifier, int total,
        RollMode mode, string? statName, string? profSpecName)
    {
        string keptStr = mode == RollMode.Normal
            ? kept.ToString()
            : $"{kept} ({mode.ToString().ToLowerInvariant()}: {string.Join(", ", rolls)})";

        // FFXIV chat only renders a limited glyph set — avoid emoji / fancy brackets (they show
        // as blank squares). Stick to plain ASCII.
        string modStr  = modifier == 0 ? "" : modifier > 0 ? $" +{modifier}" : $" {modifier}";
        string statStr = statName == null ? "" : $" [{statName}]";
        string profStr = profSpecName == null ? "" : $" ({profSpecName})";

        return modifier == 0 && mode == RollMode.Normal
            ? $"d{die}{statStr}{profStr}: {keptStr}"
            : $"d{die}{statStr}{profStr}: {keptStr}{modStr} = {total}";
    }
}
