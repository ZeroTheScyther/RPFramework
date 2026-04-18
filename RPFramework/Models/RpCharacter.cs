using System;
using System.Collections.Generic;

namespace RPFramework.Models;

[Serializable]
public class RpCharacter
{
    // ── Dynamic sheet data ───────────────────────────────────────────────────
    // Number fields: StatValues[FieldId] = value
    // Bar fields:    StatValues[FieldId + ":cur"] = current, StatValues[FieldId + ":max"] = max
    public Dictionary<string, int>  StatValues  { get; set; } = new();
    public Dictionary<string, bool> CheckValues { get; set; } = new();

    // True once old hardcoded properties have been migrated into StatValues/CheckValues.
    public bool SheetMigrated { get; set; } = false;

    public List<RpSkill> Skills { get; set; } = new();

    // ── Legacy properties — kept for JSON deserialization and migration only ─
    public int Str { get; set; } = 10;
    public int Dex { get; set; } = 10;
    public int Spd { get; set; } = 10;
    public int Con { get; set; } = 10;
    public int Mem { get; set; } = 10;
    public int Mtl { get; set; } = 10;
    public int Int { get; set; } = 10;
    public int Cha { get; set; } = 10;

    public int HpCurrent { get; set; } = 0;
    public int HpMax     { get; set; } = 0;
    public int ApCurrent { get; set; } = 0;
    public int ApMax     { get; set; } = 0;

    public Dictionary<string, bool> Proficiencies { get; set; } = new();
}
