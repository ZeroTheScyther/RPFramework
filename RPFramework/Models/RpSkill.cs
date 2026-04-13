using System;
using System.Collections.Generic;

namespace RPFramework.Models;

public enum SkillType   { Active, Passive }
public enum SkillStat   { Hp, Ap, Str, Dex, Spd, Con, Mem, Mtl, Int, Cha }
public enum ConditionOp { Less, LessEqual, Equal, GreaterEqual, Greater }
public enum EffectOp    { Add, Subtract, Set }

[Serializable]
public class SkillCondition
{
    public SkillStat   Stat         { get; set; } = SkillStat.Ap;
    public ConditionOp Op           { get; set; } = ConditionOp.LessEqual;
    public float       Value        { get; set; } = 50f;
    public bool        IsPercentage { get; set; } = true;   // true = % of pool max (Hp/Ap), or % of 20 for stats
}

[Serializable]
public class SkillEffect
{
    public SkillStat Target       { get; set; } = SkillStat.Hp;
    public EffectOp  Op           { get; set; } = EffectOp.Add;
    public float     Value        { get; set; } = 1f;
    public bool      IsPercentage { get; set; } = false;    // true = % of pool max (Hp/Ap only)
}

[Serializable]
public class RpSkill
{
    public Guid                 Id                { get; set; } = Guid.NewGuid();
    public string               Name              { get; set; } = "New Skill";
    public string               Description       { get; set; } = "";
    public SkillType            Type              { get; set; } = SkillType.Active;
    public int                  Cooldown          { get; set; } = 0;    // 0 = no cooldown
    public int                  CooldownRemaining { get; set; } = 0;
    /// How many turns stat effects last after activation. 0 = instant/permanent (HP/AP effects always instant).
    public int                  Duration          { get; set; } = 0;
    public int                  DurationRemaining { get; set; } = 0;
    /// When true, the editor pane shows a read-only view. Right-click → Edit to unlock.
    public bool                 IsLocked          { get; set; } = false;
    /// Passives with conditions auto-evaluate at roll time; empty = manually triggered.
    public List<SkillCondition> Conditions        { get; set; } = new();
    public List<SkillEffect>    Effects           { get; set; } = new();
    /// When true, this passive's effects are applied each time the player presses End Turn in the Initiative window.
    public bool                 TriggerOnTurnEnd  { get; set; } = false;
}
