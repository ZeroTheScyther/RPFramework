using System;

namespace RPFramework.Models;

public static class SkillHelpers
{
    /// D&D-style stat modifier: floor((stat − 10) / 2).
    public static int StatMod(int stat) => (int)Math.Floor((stat - 10) / 2.0);

    /// AP exhaustion penalty applied to ALL stat rolls.
    /// Thresholds: ≤40% → -1, ≤30% → -2, ≤20% → -4, ≤10% → -5.
    public static int ApPenalty(RpCharacter ch)
    {
        if (ch.ApMax <= 0) return 0;
        float pct = (float)ch.ApCurrent / ch.ApMax;
        return pct switch
        {
            <= 0.10f => -5,
            <= 0.20f => -4,
            <= 0.30f => -2,
            <= 0.40f => -1,
            _        =>  0,
        };
    }

    /// Sum of raw stat deltas applied to dice rolls for the given stat.
    /// Sources: condition-based passives whose conditions are currently met,
    ///          and any skill with DurationRemaining > 0 (timed buffs/debuffs).
    /// Applied to the raw stat value BEFORE the StatMod formula.
    public static int PassiveStatAdjust(RpCharacter ch, SkillStat stat)
    {
        int total = 0;
        foreach (var skill in ch.Skills)
        {
            bool conditionActive = skill.Type == SkillType.Passive
                                   && skill.Conditions.Count > 0
                                   && ConditionsMet(skill, ch);
            bool durationActive  = skill.DurationRemaining > 0;

            if (!conditionActive && !durationActive) continue;

            foreach (var fx in skill.Effects)
            {
                if (fx.Target != stat) continue;
                float amt = fx.Value;   // % on raw stats has no clear meaning — treat as flat
                total += fx.Op switch
                {
                    EffectOp.Add      =>  (int)Math.Round(amt),
                    EffectOp.Subtract => -(int)Math.Round(amt),
                    _                 =>  0,
                };
            }
        }
        return total;
    }

    /// Returns true when every condition on the skill is satisfied by the character's current state.
    public static bool ConditionsMet(RpSkill skill, RpCharacter ch)
    {
        foreach (var c in skill.Conditions)
            if (!EvalCondition(c, ch)) return false;
        return true;
    }

    static bool EvalCondition(SkillCondition c, RpCharacter ch)
    {
        float raw = GetStatRaw(c.Stat, ch);
        float max = GetStatMax(c.Stat, ch);
        float cmp = c.IsPercentage && max > 0 ? raw / max * 100f : raw;
        return c.Op switch
        {
            ConditionOp.Less         => cmp <  c.Value,
            ConditionOp.LessEqual    => cmp <= c.Value,
            ConditionOp.Equal        => MathF.Abs(cmp - c.Value) < 0.5f,
            ConditionOp.GreaterEqual => cmp >= c.Value,
            ConditionOp.Greater      => cmp >  c.Value,
            _                        => false,
        };
    }

    /// Activates a skill's effects.
    /// - HP/AP effects are always applied immediately (instant, regardless of Duration).
    /// - Stat effects (Str/Dex/etc.): if Duration == 0, applied permanently; if Duration > 0,
    ///   skipped here — they take effect via PassiveStatAdjust while DurationRemaining > 0.
    /// Sets DurationRemaining and CooldownRemaining when applicable.
    public static void ApplyEffects(RpSkill skill, RpCharacter ch)
    {
        bool timed = skill.Duration > 0;

        foreach (var fx in skill.Effects)
        {
            // For timed skills, stat effects are handled as temporary modifiers — skip permanent write
            if (timed && fx.Target != SkillStat.Hp && fx.Target != SkillStat.Ap)
                continue;

            float maxVal = GetStatMax(fx.Target, ch);
            float amount = fx.IsPercentage && maxVal > 0 ? maxVal * fx.Value / 100f : fx.Value;
            int   delta  = fx.Op switch
            {
                EffectOp.Add      =>  (int)Math.Round(amount),
                EffectOp.Subtract => -(int)Math.Round(amount),
                _                 =>  0,
            };

            switch (fx.Target)
            {
                case SkillStat.Hp:
                    int effHpMax = ch.HpMax + StatMod(ch.Con);
                    ch.HpCurrent = fx.Op == EffectOp.Set
                        ? Math.Clamp((int)Math.Round(amount), 0, effHpMax)
                        : Math.Clamp(ch.HpCurrent + delta,   0, effHpMax);
                    break;
                case SkillStat.Ap:
                    ch.ApCurrent = fx.Op == EffectOp.Set
                        ? Math.Clamp((int)Math.Round(amount), 0, ch.ApMax)
                        : Math.Clamp(ch.ApCurrent + delta,   0, ch.ApMax);
                    break;
                // Raw stats — NOT clamped; skills can break the 8–20 UI cap
                case SkillStat.Str: ch.Str = fx.Op == EffectOp.Set ? (int)Math.Round(amount) : ch.Str + delta; break;
                case SkillStat.Dex: ch.Dex = fx.Op == EffectOp.Set ? (int)Math.Round(amount) : ch.Dex + delta; break;
                case SkillStat.Spd: ch.Spd = fx.Op == EffectOp.Set ? (int)Math.Round(amount) : ch.Spd + delta; break;
                case SkillStat.Con: ch.Con = fx.Op == EffectOp.Set ? (int)Math.Round(amount) : ch.Con + delta; break;
                case SkillStat.Mem: ch.Mem = fx.Op == EffectOp.Set ? (int)Math.Round(amount) : ch.Mem + delta; break;
                case SkillStat.Mtl: ch.Mtl = fx.Op == EffectOp.Set ? (int)Math.Round(amount) : ch.Mtl + delta; break;
                case SkillStat.Int: ch.Int = fx.Op == EffectOp.Set ? (int)Math.Round(amount) : ch.Int + delta; break;
                case SkillStat.Cha: ch.Cha = fx.Op == EffectOp.Set ? (int)Math.Round(amount) : ch.Cha + delta; break;
            }
        }

        if (skill.Duration > 0)
            skill.DurationRemaining = skill.Duration;
        if (skill.Cooldown > 0)
            skill.CooldownRemaining = skill.Cooldown;
    }

    // ── Internal helpers ────────────────────────────────────────────────────────

    static float GetStatRaw(SkillStat s, RpCharacter ch) => s switch
    {
        SkillStat.Hp  => ch.HpCurrent,
        SkillStat.Ap  => ch.ApCurrent,
        SkillStat.Str => ch.Str,
        SkillStat.Dex => ch.Dex,
        SkillStat.Spd => ch.Spd,
        SkillStat.Con => ch.Con,
        SkillStat.Mem => ch.Mem,
        SkillStat.Mtl => ch.Mtl,
        SkillStat.Int => ch.Int,
        SkillStat.Cha => ch.Cha,
        _             => 0,
    };

    static float GetStatMax(SkillStat s, RpCharacter ch) => s switch
    {
        SkillStat.Hp => ch.HpMax > 0 ? ch.HpMax : 1,
        SkillStat.Ap => ch.ApMax > 0 ? ch.ApMax : 1,
        _            => 20f,   // raw stats: percentage conditions reference 20 as max
    };
}
