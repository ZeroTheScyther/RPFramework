namespace RPFramework.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// Skills. Clean-break model: conditions/effects reference sheet fields by string
// FieldId only (no legacy SkillStat enum). Session-local combat state (cooldown /
// duration remaining, locked) is part of the server-owned authoritative character
// state and is synced like everything else.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SkillCondition
{
    public string      FieldId      { get; set; } = "";
    public ConditionOp Op           { get; set; } = ConditionOp.LessEqual;
    public float       Value        { get; set; } = 50f;
    public bool        IsPercentage { get; set; } = true;
}

public sealed class SkillEffect
{
    public string   FieldId      { get; set; } = "";
    public EffectOp  Op          { get; set; } = EffectOp.Add;
    public float    Value        { get; set; } = 1f;
    public bool     IsPercentage { get; set; } = false;
    /// <summary>When set, this effect GRANTS a passive instead of applying a stat op: on activation the
    /// caster's own passive with this Id is switched on for <see cref="Value"/> turns. <see cref="FieldId"/>
    /// is left empty so stat/check gatherers ignore it.</summary>
    public Guid?    GrantPassiveId { get; set; } = null;
}

/// <summary>
/// One independent conditional group of effects. Empty <see cref="Conditions"/> = always active.
/// Lets a single skill/item carry multiple if-blocks (e.g. "+10 STR always; if HP&lt;50% then +20 STR")
/// that are evaluated and summed independently, instead of one condition set gating ALL effects.
/// </summary>
public sealed class EffectBlock
{
    public List<SkillCondition> Conditions  { get; set; } = new();
    public List<SkillEffect>    Effects     { get; set; } = new();   // applied while conditions hold
    public List<SkillEffect>    ElseEffects { get; set; } = new();   // applied while conditions do NOT hold (if/else)
}

public sealed class RpSkill
{
    public Guid                 Id                { get; set; } = Guid.NewGuid();
    public string               Name              { get; set; } = "New Skill";
    public string               Description       { get; set; } = "";
    public SkillType            Type              { get; set; } = SkillType.Active;
    public int                  Cooldown          { get; set; } = 0;
    public int                  CooldownRemaining { get; set; } = 0;
    public int                  Duration          { get; set; } = 0;
    public int                  DurationRemaining { get; set; } = 0;
    public bool                 IsLocked          { get; set; } = false;
    /// <summary>Passive toggle: a passive contributes its effects (live, via the mod layer) only while
    /// this is on. Players flip it via UseSkill. Ignored for Active skills. Server-owned runtime state.</summary>
    public bool                 Active            { get; set; } = false;
    /// <summary>DM-authored vault skill: shown in a separate "DM Skills" section and used as a source of
    /// passive definitions to embed into items (see <see cref="RpItemDto"/> GrantedPassives). DM-only.</summary>
    public bool                 IsDmSkill         { get; set; } = false;
    public List<SkillCondition> Conditions        { get; set; } = new();   // base block conditions (gate base Effects)
    public List<SkillEffect>    Effects           { get; set; } = new();   // base block effects
    public List<EffectBlock>    ConditionalBlocks { get; set; } = new();   // extra independent if-blocks, summed when met
    public bool                 TriggerOnTurnEnd  { get; set; } = false;
}
