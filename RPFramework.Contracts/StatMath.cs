namespace RPFramework.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// Shared stat/skill math. Lives in Contracts so the SERVER (authority) and the
// CLIENT (optimistic echo + display) compute identical results. Operates on the
// authoritative CharacterState + the party's SheetTemplate.
// ─────────────────────────────────────────────────────────────────────────────

public static class StatMath
{
    /// D&D-style stat modifier: floor((stat − 10) / 2).
    public static int StatMod(int stat) => (int)Math.Floor((stat - 10) / 2.0);

    // ── AP exhaustion penalty ─────────────────────────────────────────────────

    /// AP exhaustion penalty applied to ALL stat rolls.
    /// Thresholds: ≤40% → -1, ≤30% → -2, ≤20% → -4, ≤10% → -5.
    public static int ApPenalty(CharacterState ch, SheetTemplate template)
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

    /// Raw stat value plus any active passive adjustments, before the StatMod formula.
    public static int EffectiveStat(CharacterState ch, string fieldId, SheetTemplate template)
    {
        ch.StatValues.TryGetValue(fieldId, out int raw);
        return raw + PassiveStatAdjust(ch, fieldId, template);
    }

    /// The initiative bonus for a character = StatMod(effective initiative stat).
    public static int InitiativeBonus(CharacterState ch, SheetTemplate template)
    {
        var stat = template.FindInitiativeStat();
        string fid = stat?.Id ?? WellKnownIds.Spd;
        return StatMod(EffectiveStat(ch, fid, template));
    }

    // ── Passive stat adjust ───────────────────────────────────────────────────

    /// Sum of raw stat deltas from active passives + equipped gear for the given field ID.
    public static int PassiveStatAdjust(CharacterState ch, string fieldId, SheetTemplate template)
    {
        ch.StatValues.TryGetValue(fieldId, out int cur);
        int total = 0;

        foreach (var skill in ch.Skills)
        {
            bool condActive = skill.Type == SkillType.Passive
                              && skill.Conditions.Count > 0
                              && ConditionsMet(skill, ch, template);
            bool durActive  = skill.DurationRemaining > 0;
            if (!condActive && !durActive) continue;
            total += FieldDelta(skill.Effects, fieldId, cur);
        }

        // Equipped gear contributes always-on passive adjustments.
        foreach (var item in ch.Equipment.Values)
            if (item.Effects != null)
                total += FieldDelta(item.Effects, fieldId, cur);

        return total;
    }

    /// Additive accumulator (applied before StatMod) of a set of effects targeting one field.
    /// Multiply/Divide contribute the delta they'd produce against the current raw value; Set is
    /// not an additive adjustment and is skipped.
    static int FieldDelta(IEnumerable<SkillEffect> effects, string fieldId, int cur)
    {
        int total = 0;
        foreach (var fx in effects)
        {
            if (fx.FieldId != fieldId) continue;
            total += fx.Op switch
            {
                EffectOp.Add      =>  (int)Math.Round(fx.Value),
                EffectOp.Subtract => -(int)Math.Round(fx.Value),
                EffectOp.Multiply =>  (int)Math.Round(cur * fx.Value) - cur,
                EffectOp.Divide   =>  fx.Value != 0f ? (int)Math.Round(cur / fx.Value) - cur : 0,
                _                 =>  0,
            };
        }
        return total;
    }

    /// <summary>
    /// A Bar's effective maximum: the stored max + the StatMod bonus from its BonusSourceFieldId +
    /// any equipped-gear effects that target the bar field (gear "+10 HP max" raises the cap). Use
    /// this anywhere a bar is rendered or clamped so equipment is reflected consistently.
    /// </summary>
    public static int EffectiveBarMax(CharacterState ch, SheetField bar, SheetTemplate template)
    {
        ch.StatValues.TryGetValue(bar.Id + ":max", out int storedMax);
        int bonus = 0;
        if (bar.BonusSourceFieldId != null && ch.StatValues.TryGetValue(bar.BonusSourceFieldId, out int src))
            bonus = StatMod(src);
        int gear = 0;
        foreach (var item in ch.Equipment.Values)
            if (item.Effects != null)
                gear += FieldDelta(item.Effects, bar.Id, storedMax);
        return storedMax + bonus + gear;
    }

    // ── Condition evaluation ──────────────────────────────────────────────────

    public static bool ConditionsMet(RpSkill skill, CharacterState ch, SheetTemplate template)
    {
        foreach (var c in skill.Conditions)
            if (!EvalCondition(c, ch, template)) return false;
        return true;
    }

    static bool EvalCondition(SkillCondition c, CharacterState ch, SheetTemplate template)
    {
        var (raw, max) = GetFieldValues(c.FieldId, ch, template);
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

    /// Activates a skill's effects against the character's stat dicts.
    /// Bar/HP/AP effects apply immediately. Number effects: permanent when
    /// Duration == 0; otherwise applied transiently via PassiveStatAdjust while
    /// DurationRemaining > 0.
    public static void ApplyEffects(RpSkill skill, CharacterState ch, SheetTemplate template)
    {
        bool timed = skill.Duration > 0;
        var  hpBar = template.FindHpBar();
        var  apBar = template.FindApBar();

        foreach (var fx in skill.Effects)
        {
            string fid    = fx.FieldId;
            var    field  = template.FindField(fid);
            bool   isHpAp = (hpBar != null && fid == hpBar.Id)
                         || (apBar != null && fid == apBar.Id);

            if (timed && !isHpAp) continue;

            var (_, maxVal) = GetFieldValues(fid, ch, template);

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
                    ch.StatValues[curKey] = Math.Clamp(ApplyOp(fx, curVal, maxVal), 0, effMax);
                    break;
                }
                case FieldType.Dot:
                {
                    string curKey = fid + ":cur";
                    int    dotMax = field.Max > 0 ? field.Max : 5;
                    ch.StatValues.TryGetValue(curKey, out int curVal);
                    ch.StatValues[curKey] = Math.Clamp(ApplyOp(fx, curVal, maxVal), 0, dotMax);
                    break;
                }
                case FieldType.Number:
                {
                    ch.StatValues.TryGetValue(fid, out int numVal);
                    ch.StatValues[fid] = ApplyOp(fx, numVal, maxVal);
                    break;
                }
                case FieldType.Checkbox:
                    if (fx.Op == EffectOp.Set)
                        ch.CheckValues[fid] = fx.Value >= 1f;
                    break;
            }
        }

        if (skill.Duration > 0) skill.DurationRemaining = skill.Duration;
        if (skill.Cooldown > 0) skill.CooldownRemaining = skill.Cooldown;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// Computes the new integer value of an effect applied to <paramref name="current"/>.
    /// Add/Subtract/Set may take a percentage relative to <paramref name="maxForPct"/>;
    /// Multiply/Divide use the literal factor (a multiplier is not a share, so percentage
    /// is ignored). Divide by zero is a no-op.
    static int ApplyOp(SkillEffect fx, int current, float maxForPct)
    {
        float amount = fx.IsPercentage && maxForPct > 0 ? maxForPct * fx.Value / 100f : fx.Value;
        return fx.Op switch
        {
            EffectOp.Add      => current + (int)Math.Round(amount),
            EffectOp.Subtract => current - (int)Math.Round(amount),
            EffectOp.Set      => (int)Math.Round(amount),
            EffectOp.Multiply => (int)Math.Round(current * fx.Value),
            EffectOp.Divide   => fx.Value != 0f ? (int)Math.Round(current / fx.Value) : current,
            _                 => current,
        };
    }

    static (float raw, float max) GetFieldValues(string fieldId, CharacterState ch, SheetTemplate template)
    {
        var field = template.FindField(fieldId);

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
