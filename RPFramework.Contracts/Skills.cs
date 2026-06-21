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
    public List<SkillCondition> Conditions        { get; set; } = new();   // base block conditions (gate base Effects)
    public List<SkillEffect>    Effects           { get; set; } = new();   // base block effects
    public List<EffectBlock>    ConditionalBlocks { get; set; } = new();   // extra independent if-blocks, summed when met
    public bool                 TriggerOnTurnEnd  { get; set; } = false;
}
