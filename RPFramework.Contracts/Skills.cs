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
    public List<SkillCondition> Conditions        { get; set; } = new();
    public List<SkillEffect>    Effects           { get; set; } = new();
    public bool                 TriggerOnTurnEnd  { get; set; } = false;
}
