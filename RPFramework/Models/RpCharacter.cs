using System;
using System.Collections.Generic;

namespace RPFramework.Models;

[Serializable]
public class RpCharacter
{
    public int Str { get; set; } = 0;
    public int Dex { get; set; } = 0;
    public int Spd { get; set; } = 0;
    public int Con { get; set; } = 0;
    public int Mem { get; set; } = 0;
    public int Mtl { get; set; } = 0;
    public int Int { get; set; } = 0;
    public int Cha { get; set; } = 0;

    public int HpCurrent { get; set; } = 0;
    public int HpMax     { get; set; } = 0;
    public int ApCurrent { get; set; } = 0;
    public int ApMax     { get; set; } = 0;

    // Key = skill name (from hardcoded list), value = proficient. Missing key = false.
    public Dictionary<string, bool> Proficiencies { get; set; } = new();
}
