using System;

namespace RPFramework.Models;

public static class SkillHelpers
{
    /// D&D-style stat modifier: floor((stat − 10) / 2).
    public static int StatMod(int stat) => (int)Math.Floor((stat - 10) / 2.0);

    // ── Legacy SkillStat → FieldId mapping ───────────────────────────────────
    // Used to resolve pre-refactor skill data that stored SkillStat enum values.

    public static string LegacyStatId(SkillStat s) => s switch
    {
        SkillStat.Hp  => WellKnownIds.Hp,
        SkillStat.Ap  => WellKnownIds.Ap,
        SkillStat.Str => WellKnownIds.Str,
        SkillStat.Dex => WellKnownIds.Dex,
        SkillStat.Spd => WellKnownIds.Spd,
        SkillStat.Con => WellKnownIds.Con,
        SkillStat.Mem => WellKnownIds.Mem,
        SkillStat.Mtl => WellKnownIds.Mtl,
        SkillStat.Int => WellKnownIds.Int,
        SkillStat.Cha => WellKnownIds.Cha,
        _             => WellKnownIds.Hp,
    };

    public static string EffectiveFieldId(SkillCondition c)
        => string.IsNullOrEmpty(c.FieldId) ? LegacyStatId(c.Stat) : c.FieldId;

    public static string EffectiveFieldId(SkillEffect fx)
        => string.IsNullOrEmpty(fx.FieldId) ? LegacyStatId(fx.Target) : fx.FieldId;

    // ── AP exhaustion penalty ─────────────────────────────────────────────────

    /// AP exhaustion penalty applied to ALL stat rolls.
    /// Thresholds: ≤40% → -1, ≤30% → -2, ≤20% → -4, ≤10% → -5.
    public static int ApPenalty(RpCharacter ch, SheetTemplate template)
    {
        var ap = template.FindApBar();
        if (ap == null) return 0;
        ch.StatValues.TryGetValue(ap.Id + ":cur", out int cur);
        ch.StatValues.TryGetValue(ap.Id + ":max", out int max);
        if (max <= 0) return 0;
        float pct = (float)cur / max;
        return pct switch
        {
            <= 0.10f => -5,
            <= 0.20f => -4,
            <= 0.30f => -2,
            <= 0.40f => -1,
            _        =>  0,
        };
    }

    // ── Passive stat adjust ───────────────────────────────────────────────────

    /// Sum of raw stat deltas from active passives for the given field ID.
    /// Applied BEFORE the StatMod formula.
    public static int PassiveStatAdjust(RpCharacter ch, string fieldId, SheetTemplate template)
    {
        int total = 0;
        foreach (var skill in ch.Skills)
        {
            bool condActive = skill.Type == SkillType.Passive
                              && skill.Conditions.Count > 0
                              && ConditionsMet(skill, ch, template);
            bool durActive  = skill.DurationRemaining > 0;
            if (!condActive && !durActive) continue;

            foreach (var fx in skill.Effects)
            {
                if (EffectiveFieldId(fx) != fieldId) continue;
                float amt = fx.Value;
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

    // ── Condition evaluation ──────────────────────────────────────────────────

    public static bool ConditionsMet(RpSkill skill, RpCharacter ch, SheetTemplate template)
    {
        foreach (var c in skill.Conditions)
            if (!EvalCondition(c, ch, template)) return false;
        return true;
    }

    static bool EvalCondition(SkillCondition c, RpCharacter ch, SheetTemplate template)
    {
        string fid = EffectiveFieldId(c);
        var (raw, max) = GetFieldValues(fid, ch, template);
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

    // ── Effect application ────────────────────────────────────────────────────

    /// Activates a skill's effects against the character's dynamic stat dicts.
    /// Bar effects are always applied immediately.
    /// Stat/number effects: permanent when Duration == 0; skipped when Duration > 0
    ///   (they take effect via PassiveStatAdjust while DurationRemaining > 0).
    public static void ApplyEffects(RpSkill skill, RpCharacter ch, SheetTemplate template)
    {
        bool timed  = skill.Duration > 0;
        var  hpBar  = template.FindHpBar();
        var  apBar  = template.FindApBar();

        foreach (var fx in skill.Effects)
        {
            string fid   = EffectiveFieldId(fx);
            var    field  = template.FindField(fid);
            bool   isBar  = field?.Type == FieldType.Bar;
            bool   isHpAp = (hpBar != null && fid == hpBar.Id)
                         || (apBar != null && fid == apBar.Id);

            if (timed && !isHpAp) continue;

            var (_, maxVal) = GetFieldValues(fid, ch, template);
            float amount = fx.IsPercentage && maxVal > 0 ? maxVal * fx.Value / 100f : fx.Value;
            int delta = fx.Op switch
            {
                EffectOp.Add      =>  (int)Math.Round(amount),
                EffectOp.Subtract => -(int)Math.Round(amount),
                _                 =>  0,
            };

            if (field == null) continue;

            switch (field.Type)
            {
                case FieldType.Bar:
                {
                    string curKey = fid + ":cur";
                    string maxKey = fid + ":max";
                    ch.StatValues.TryGetValue(maxKey, out int barMax);
                    int bonus = 0;
                    if (field.BonusSourceFieldId != null &&
                        ch.StatValues.TryGetValue(field.BonusSourceFieldId, out int bonusSrc))
                        bonus = StatMod(bonusSrc);
                    int effMax = barMax + bonus;
                    ch.StatValues.TryGetValue(curKey, out int curVal);
                    ch.StatValues[curKey] = fx.Op == EffectOp.Set
                        ? Math.Clamp((int)Math.Round(amount), 0, effMax)
                        : Math.Clamp(curVal + delta, 0, effMax);
                    break;
                }
                case FieldType.Dot:
                {
                    string curKey = fid + ":cur";
                    int    dotMax = field.Max > 0 ? field.Max : 5;
                    ch.StatValues.TryGetValue(curKey, out int curVal);
                    ch.StatValues[curKey] = fx.Op == EffectOp.Set
                        ? Math.Clamp((int)Math.Round(amount), 0, dotMax)
                        : Math.Clamp(curVal + delta, 0, dotMax);
                    break;
                }
                case FieldType.Number:
                {
                    ch.StatValues.TryGetValue(fid, out int numVal);
                    ch.StatValues[fid] = fx.Op == EffectOp.Set
                        ? (int)Math.Round(amount)
                        : numVal + delta;
                    break;
                }
                case FieldType.Checkbox:
                    if (fx.Op == EffectOp.Set)
                        ch.CheckValues[fid] = Math.Round(amount) >= 1;
                    break;
            }
        }

        if (skill.Duration > 0) skill.DurationRemaining = skill.Duration;
        if (skill.Cooldown > 0) skill.CooldownRemaining = skill.Cooldown;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    static (float raw, float max) GetFieldValues(string fieldId, RpCharacter ch, SheetTemplate template)
    {
        var field = template?.FindField(fieldId);

        if (field == null)
        {
            ch.StatValues.TryGetValue(fieldId, out int v);
            return (v, 20f);
        }

        return field.Type switch
        {
            FieldType.Bar => (
                ch.StatValues.TryGetValue(fieldId + ":cur", out int cur) ? cur : 0,
                ch.StatValues.TryGetValue(fieldId + ":max", out int max) && max > 0 ? max : 1
            ),
            FieldType.Dot => (
                ch.StatValues.TryGetValue(fieldId + ":cur", out int dcur) ? dcur : 0,
                field.Max > 0 ? field.Max : 5f
            ),
            FieldType.Number => (
                ch.StatValues.TryGetValue(fieldId, out int v) ? v : 0,
                field.Max > 0 ? field.Max : 20f
            ),
            FieldType.Checkbox => (
                ch.CheckValues.TryGetValue(fieldId, out bool b) && b ? 1f : 0f,
                1f
            ),
            _ => (0, 1),
        };
    }
}
