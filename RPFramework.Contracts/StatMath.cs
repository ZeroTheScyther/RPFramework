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

    /// Suffix that marks an effect targeting a bar's MAXIMUM rather than its current value. A bare bar
    /// id (e.g. "builtin:hp") addresses current HP; "builtin:hp:max" addresses max HP. Equipment folds
    /// only ":max" effects into the cap; skills/consumables apply bare effects to current.
    public const string MaxSuffix = ":max";

    /// Splits a bar-effect target id into its underlying field id and whether it addresses the max.
    public static (string BaseId, bool TargetMax) SplitBarTarget(string fieldId)
        => fieldId.EndsWith(MaxSuffix) ? (fieldId[..^MaxSuffix.Length], true) : (fieldId, false);

    /// Synthetic condition field id marking a block as an "On Turn End" trigger rather than a continuous
    /// state predicate. A block carrying this condition never contributes to live passive adjustment; it
    /// fires once per turn-end via <see cref="ApplyTurnEndEffects"/>. The marker is ignored when a block's
    /// other conditions are evaluated, so "On Turn End" can be combined with real conditions.
    public const string OnTurnEndId = "builtin:onturnend";

    /// True when a block is gated by the <see cref="OnTurnEndId"/> turn-end marker.
    static bool IsTurnEndBlock(EffectBlock b)
    {
        foreach (var c in b.Conditions)
            if (c.FieldId == OnTurnEndId) return true;
        return false;
    }

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

    // ── Active-effect gatherers (single source of truth for "what applies now") ──

    /// <summary>
    /// Every effect a skill currently contributes to live passive adjustment, across its base block and
    /// each conditional block. The base block keeps the legacy gate (active skills contribute only during
    /// an active duration; a conditional passive contributes while its conditions are met). Conditional
    /// blocks contribute while the skill is engaged (active duration OR any passive) and their own
    /// conditions hold (empty conditions = always while engaged). With no conditional blocks this is
    /// identical to the pre-blocks behavior.
    /// </summary>
    public static IEnumerable<SkillEffect> ActiveSkillEffects(RpSkill skill, CharacterState ch, SheetTemplate template)
    {
        bool dur     = skill.DurationRemaining > 0;
        bool passive = skill.Type == SkillType.Passive;

        if (dur || (passive && skill.Conditions.Count > 0 && ConditionsMet(skill.Conditions, ch, template)))
            foreach (var fx in skill.Effects) yield return fx;

        if (dur || passive)
            foreach (var b in skill.ConditionalBlocks)
            {
                if (IsTurnEndBlock(b)) continue;   // turn-end blocks fire only on turn end, never continuously
                foreach (var fx in (ConditionsMet(b.Conditions, ch, template) ? b.Effects : b.ElseEffects))
                    yield return fx;
            }
    }

    /// <summary>
    /// Every effect an equipped item currently contributes: its base effects (while <see cref="ItemActive"/>)
    /// plus each conditional block whose conditions are met. With no blocks this is identical to the
    /// pre-blocks behavior. The single place equipment effect-gathering lives.
    /// </summary>
    public static IEnumerable<SkillEffect> ActiveItemEffects(RpItemDto item, CharacterState ch, SheetTemplate template)
    {
        if (item.Effects != null && ItemActive(item, ch, template))
            foreach (var fx in item.Effects) yield return fx;

        if (item.Blocks != null)
            foreach (var b in item.Blocks)
                foreach (var fx in (ConditionsMet(b.Conditions, ch, template) ? b.Effects : b.ElseEffects))
                    yield return fx;
    }

    // ── Passive stat adjust ───────────────────────────────────────────────────

    /// Sum of raw stat deltas from active passives + equipped gear for the given field ID.
    public static int PassiveStatAdjust(CharacterState ch, string fieldId, SheetTemplate template)
    {
        ch.StatValues.TryGetValue(fieldId, out int cur);
        int total = 0;

        foreach (var skill in ch.Skills)
            total += FieldDelta(ActiveSkillEffects(skill, ch, template), fieldId, cur);

        foreach (var item in ch.Equipment.Values)
            total += FieldDelta(ActiveItemEffects(item, ch, template), fieldId, cur);

        return total;
    }

    /// Per-source breakdown of the passive/gear adjustments to a field, in the same order they are
    /// summed by <see cref="PassiveStatAdjust"/> (active skills first, then equipped gear). Each entry
    /// is the source's display name and its net delta; zero-delta sources are omitted. Used to build
    /// the "where did this come from" tooltip on a modified stat.
    public static List<(string Name, int Delta)> StatSources(CharacterState ch, string fieldId, SheetTemplate template)
    {
        ch.StatValues.TryGetValue(fieldId, out int cur);
        var list = new List<(string, int)>();

        foreach (var skill in ch.Skills)
        {
            int d = FieldDelta(ActiveSkillEffects(skill, ch, template), fieldId, cur);
            if (d != 0) list.Add((skill.Name, d));
        }

        foreach (var item in ch.Equipment.Values)
        {
            int d = FieldDelta(ActiveItemEffects(item, ch, template), fieldId, cur);
            if (d != 0) list.Add((item.Name, d));
        }

        return list;
    }

    /// Active Set-effects on a Checkbox (proficiency/specialization) field from gear and active
    /// passives, in apply order (skills first, then gear). Each entry is the source's name and whether
    /// it grants (true) or removes (false) the proficiency. Empty when nothing overrides the field.
    public static List<(string Name, bool Grant)> CheckSources(CharacterState ch, string fieldId, SheetTemplate template)
    {
        var list = new List<(string, bool)>();

        foreach (var skill in ch.Skills)
            foreach (var fx in ActiveSkillEffects(skill, ch, template))
                if (fx.FieldId == fieldId && fx.Op == EffectOp.Set)
                    list.Add((skill.Name, fx.Value >= 1f));

        foreach (var item in ch.Equipment.Values)
            foreach (var fx in ActiveItemEffects(item, ch, template))
                if (fx.FieldId == fieldId && fx.Op == EffectOp.Set)
                    list.Add((item.Name, fx.Value >= 1f));

        return list;
    }

    /// Effective on/off state of a Checkbox field: the stored base value unless an active gear/passive
    /// source overrides it, in which case the last-applied Set wins (gear over skills).
    public static bool EffectiveCheck(CharacterState ch, string fieldId, SheetTemplate template)
    {
        var sources = CheckSources(ch, fieldId, template);
        if (sources.Count > 0) return sources[^1].Grant;
        ch.CheckValues.TryGetValue(fieldId, out bool baseVal);
        return baseVal;
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
            gear += FieldDelta(ActiveItemEffects(item, ch, template), bar.Id + MaxSuffix, storedMax);
        return storedMax + bonus + gear;
    }

    /// Per-source breakdown of everything that lifts a bar's max above its stored value: the stat
    /// bonus from its BonusSourceFieldId (named after that stat) first, then each equipped-gear delta.
    /// Mirrors the additions in <see cref="EffectiveBarMax"/>; zero-delta sources are omitted.
    public static List<(string Name, int Delta)> BarMaxSources(CharacterState ch, SheetField bar, SheetTemplate template)
    {
        var list = new List<(string, int)>();
        ch.StatValues.TryGetValue(bar.Id + ":max", out int storedMax);

        if (bar.BonusSourceFieldId != null)
        {
            var bf = template.FindField(bar.BonusSourceFieldId);
            if (bf != null && ch.StatValues.TryGetValue(bar.BonusSourceFieldId, out int src))
            {
                int b = StatMod(src);
                if (b != 0) list.Add((bf.Name, b));
            }
        }

        foreach (var item in ch.Equipment.Values)
        {
            int d = FieldDelta(ActiveItemEffects(item, ch, template), bar.Id + MaxSuffix, storedMax);
            if (d != 0) list.Add((item.Name, d));
        }

        return list;
    }

    // ── Condition evaluation ──────────────────────────────────────────────────

    public static bool ConditionsMet(RpSkill skill, CharacterState ch, SheetTemplate template)
        => ConditionsMet(skill.Conditions, ch, template);

    /// True when every condition in the set holds (an empty set is vacuously true). The
    /// <see cref="OnTurnEndId"/> turn-end marker is not a state predicate, so it is skipped here.
    public static bool ConditionsMet(IEnumerable<SkillCondition> conditions, CharacterState ch, SheetTemplate template)
    {
        foreach (var c in conditions)
        {
            if (c.FieldId == OnTurnEndId) continue;   // event marker, not a state predicate
            if (!EvalCondition(c, ch, template)) return false;
        }
        return true;
    }

    /// Whether an equipped item's effects currently apply: an item with no conditions is always-on;
    /// a conditional item contributes only while ALL of its conditions hold (same gate as a passive).
    public static bool ItemActive(RpItemDto item, CharacterState ch, SheetTemplate template)
        => item.Conditions == null || item.Conditions.Count == 0 || ConditionsMet(item.Conditions, ch, template);

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

        // Activation applies the base block plus every conditional block whose conditions hold right now.
        // Turn-end blocks are excluded here — they fire from ApplyTurnEndEffects, not on activation.
        var toApply = new List<SkillEffect>(skill.Effects);
        foreach (var b in skill.ConditionalBlocks)
        {
            if (IsTurnEndBlock(b)) continue;
            toApply.AddRange(ConditionsMet(b.Conditions, ch, template) ? b.Effects : b.ElseEffects);
        }

        ApplyEffectList(toApply, timed, ch, template);

        if (skill.Duration > 0) skill.DurationRemaining = skill.Duration;
        if (skill.Cooldown > 0) skill.CooldownRemaining = skill.Cooldown;
    }

    /// <summary>
    /// Fires a skill's turn-end effects once (called from the server's end-of-turn tick). Honors both
    /// the legacy whole-skill <see cref="RpSkill.TriggerOnTurnEnd"/> flag (applies the base effects while
    /// the base conditions hold) and any conditional block carrying the <see cref="OnTurnEndId"/> marker
    /// (applies that block's met/else branch). Turn-end effects are never timed — they tick current
    /// values permanently. Sets the cooldown if anything applied and the skill has one. Returns whether
    /// any effect was applied.
    /// </summary>
    public static bool ApplyTurnEndEffects(RpSkill skill, CharacterState ch, SheetTemplate template)
    {
        var toApply = new List<SkillEffect>();

        if (skill.TriggerOnTurnEnd && ConditionsMet(skill.Conditions, ch, template))
            toApply.AddRange(skill.Effects);

        foreach (var b in skill.ConditionalBlocks)
        {
            if (!IsTurnEndBlock(b)) continue;
            toApply.AddRange(ConditionsMet(b.Conditions, ch, template) ? b.Effects : b.ElseEffects);
        }

        if (toApply.Count == 0) return false;

        ApplyEffectList(toApply, timed: false, ch, template);
        if (skill.Cooldown > 0) skill.CooldownRemaining = skill.Cooldown;
        return true;
    }

    /// Applies a flat list of effects to the character's stat dicts. When <paramref name="timed"/> is
    /// true (a duration skill activating), only current HP/AP are touched — every other change, including
    /// any bar-max change, is deferred to live passive adjustment. When false, all effects apply directly.
    static void ApplyEffectList(IEnumerable<SkillEffect> effects, bool timed, CharacterState ch, SheetTemplate template)
    {
        var hpBar = template.FindHpBar();
        var apBar = template.FindApBar();

        foreach (var fx in effects)
        {
            var (baseId, targetMax) = SplitBarTarget(fx.FieldId);
            var    field  = template.FindField(baseId);
            bool   isHpAp = (hpBar != null && baseId == hpBar.Id)
                         || (apBar != null && baseId == apBar.Id);

            // Timed skills only tick current HP/AP; everything else (incl. any max change) is permanent.
            if (timed && !(isHpAp && !targetMax)) continue;

            var (_, maxVal) = GetFieldValues(baseId, ch, template);

            if (field == null) continue;

            switch (field.Type)
            {
                case FieldType.Bar:
                {
                    if (targetMax)
                    {
                        string maxKey = baseId + ":max";
                        ch.StatValues.TryGetValue(maxKey, out int curMax);
                        ch.StatValues[maxKey] = Math.Clamp(ApplyOp(fx, curMax, maxVal), 0, 9999);
                    }
                    else
                    {
                        string curKey = baseId + ":cur";
                        int    effMax = EffectiveBarMax(ch, field, template); // stored max + stat bonus + equipped gear
                        ch.StatValues.TryGetValue(curKey, out int curVal);
                        ch.StatValues[curKey] = Math.Clamp(ApplyOp(fx, curVal, maxVal), 0, effMax);
                    }
                    break;
                }
                case FieldType.Dot:
                {
                    string curKey = baseId + ":cur";
                    int    dotMax = field.Max > 0 ? field.Max : 5;
                    ch.StatValues.TryGetValue(curKey, out int curVal);
                    ch.StatValues[curKey] = Math.Clamp(ApplyOp(fx, curVal, maxVal), 0, dotMax);
                    break;
                }
                case FieldType.Number:
                {
                    ch.StatValues.TryGetValue(baseId, out int numVal);
                    ch.StatValues[baseId] = ApplyOp(fx, numVal, maxVal);
                    break;
                }
                case FieldType.Checkbox:
                    if (fx.Op == EffectOp.Set)
                        ch.CheckValues[baseId] = fx.Value >= 1f;
                    break;
            }
        }
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
