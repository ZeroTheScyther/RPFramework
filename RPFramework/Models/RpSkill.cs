using System;
using System.Collections.Generic;

namespace RPFramework.Models;

public enum SkillType   { Active, Passive }
// SkillStat is kept for JSON migration — new code uses string FieldId.
public enum SkillStat   { Hp, Ap, Str, Dex, Spd, Con, Mem, Mtl, Int, Cha }
public enum ConditionOp { Less, LessEqual, Equal, GreaterEqual, Greater }
public enum EffectOp    { Add, Subtract, Set }

[Serializable]
public class SkillCondition
{
    // New: SheetField.Id reference. Takes precedence over legacy Stat when non-empty.
    public string      FieldId      { get; set; } = "";
    // Legacy: kept so pre-refactor saves deserialize correctly.
    public SkillStat   Stat         { get; set; } = SkillStat.Ap;
    public ConditionOp Op           { get; set; } = ConditionOp.LessEqual;
    public float       Value        { get; set; } = 50f;
    public bool        IsPercentage { get; set; } = true;
}

[Serializable]
public class SkillEffect
{
    // New: SheetField.Id reference. Takes precedence over legacy Target when non-empty.
    public string    FieldId      { get; set; } = "";
    // Legacy: kept so pre-refactor saves deserialize correctly.
    public SkillStat Target       { get; set; } = SkillStat.Hp;
    public EffectOp  Op           { get; set; } = EffectOp.Add;
    public float     Value        { get; set; } = 1f;
    public bool      IsPercentage { get; set; } = false;
}

[Serializable]
public class RpSkill
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
