using System;
using System.Collections.Generic;

namespace RPFramework.Models;

public static class WellKnownIds
{
    public const string Hp  = "builtin:hp";
    public const string Ap  = "builtin:ap";
    public const string Str = "builtin:str";
    public const string Dex = "builtin:dex";
    public const string Spd = "builtin:spd";
    public const string Con = "builtin:con";
    public const string Mem = "builtin:mem";
    public const string Mtl = "builtin:mtl";
    public const string Int = "builtin:int";
    public const string Cha = "builtin:cha";

    public static string SpecId(string specName)
        => "builtin:spec:" + specName.ToLowerInvariant().Replace(' ', '_');
}

public enum FieldType { Number, Checkbox, Bar, Dot }

[Serializable]
public class SheetField
{
    public string    Id   { get; set; } = Guid.NewGuid().ToString();
    public string    Name { get; set; } = "Field";
    public FieldType Type { get; set; } = FieldType.Number;

    // Number field settings
    public int  Min          { get; set; } = 0;
    public int  Max          { get; set; } = 20;
    public bool ShowModifier { get; set; } = false;

    // Bar: optional Number field whose StatMod() is added to this bar's effective max
    public string? BonusSourceFieldId { get; set; } = null;

    // Role flags
    public bool IsHpBar          { get; set; } = false;
    public bool IsApBar          { get; set; } = false;
    public bool IsInitiativeStat { get; set; } = false;

    // Player-facing help text shown on hover
    public string Tooltip { get; set; } = "";
}

[Serializable]
public class SheetGroup
{
    public string           Id     { get; set; } = Guid.NewGuid().ToString();
    public string           Name   { get; set; } = "Group";
    public List<SheetField> Fields { get; set; } = new();
}

[Serializable]
public class SheetTemplate
{
    public string           Id     { get; set; } = Guid.NewGuid().ToString();
    public List<SheetGroup> Groups { get; set; } = new();

    public SheetField? FindField(string id)
    {
        foreach (var g in Groups)
            foreach (var f in g.Fields)
                if (f.Id == id) return f;
        return null;
    }

    public SheetField? FindHpBar()
    {
        foreach (var g in Groups)
            foreach (var f in g.Fields)
                if (f.IsHpBar) return f;
        return null;
    }

    public SheetField? FindApBar()
    {
        foreach (var g in Groups)
            foreach (var f in g.Fields)
                if (f.IsApBar) return f;
        return null;
    }

    public SheetField? FindInitiativeStat()
    {
        foreach (var g in Groups)
            foreach (var f in g.Fields)
                if (f.IsInitiativeStat) return f;
        return null;
    }

    public static SheetTemplate Default()
    {
        var specs = new (string Name, string Tooltip)[]
        {
            ("Acrobatics",      "Tumbling, balancing, and athletic feats requiring agility."),
            ("Animal Handling", "Calming, commanding, or reading the intentions of animals."),
            ("Thaumaturgy",     "Conjuring destructive aetheric energies and elemental magic."),
            ("Arcanima",        "Manipulating aether through geometry and sigils; arcane scholarship."),
            ("Conjury",         "Healing and nature magic; mending wounds and communing with the land."),
            ("History",         "Recalling events, lore, legends, and cultural knowledge."),
            ("Insight",         "Reading people — sensing emotions, detecting lies, and empathising."),
            ("Aetherology",     "Understanding aetheric theory, ley lines, and aetheric phenomena."),
            ("Intimidation",    "Influencing others through threats, displays of power, or sheer presence."),
            ("Investigation",   "Searching for clues, deducing facts, and solving puzzles."),
            ("Medicine",        "Treating injuries, diagnosing illness, and providing first aid."),
            ("Herbalism",       "Identifying, harvesting, and using medicinal or alchemical plants."),
            ("Perception",      "Noticing details in your environment using all your senses."),
            ("Performance",     "Entertaining through music, dance, acting, or storytelling."),
            ("Persuasion",      "Changing minds through honest appeal, charm, and reasoned argument."),
            ("Religion",        "Knowledge of deities, rites, theology, and sacred traditions."),
            ("Sleight of Hand", "Pickpocketing, palming objects, and fine manual trickery."),
            ("Bartering",       "Negotiating prices, appraising goods, and navigating merchant dealings."),
            ("Stealth",         "Moving and hiding without being detected."),
            ("Deception",       "Lying convincingly, disguising intent, and misdirecting attention."),
            ("Streetwise",      "Navigating cities, knowing the underworld, and finding things in settlements."),
            ("Hobnobbing",      "Moving in elite circles; knowing etiquette, names, and noble politics."),
            ("Survival",        "Tracking, foraging, navigating wilderness, and enduring harsh conditions."),
        };

        var specFields = new List<SheetField>();
        foreach (var (name, tooltip) in specs)
            specFields.Add(new SheetField
            {
                Id      = WellKnownIds.SpecId(name),
                Name    = name,
                Type    = FieldType.Checkbox,
                Tooltip = tooltip,
            });

        return new SheetTemplate
        {
            Groups =
            [
                new SheetGroup
                {
                    Id = "builtin:pools", Name = "Pools",
                    Fields =
                    [
                        new SheetField
                        {
                            Id = WellKnownIds.Hp, Name = "HP", Type = FieldType.Bar,
                            Min = 0, Max = 9999, IsHpBar = true,
                            BonusSourceFieldId = WellKnownIds.Con,
                            Tooltip = "Hit Points — your health pool. Reaches 0 and you are incapacitated.",
                        },
                        new SheetField
                        {
                            Id = WellKnownIds.Ap, Name = "AP", Type = FieldType.Bar,
                            Min = 0, Max = 9999, IsApBar = true,
                            Tooltip = "Action Points — your stamina. Low AP imposes penalties on all stat rolls.",
                        },
                    ],
                },
                new SheetGroup
                {
                    Id = "builtin:stats", Name = "Stats",
                    Fields =
                    [
                        new SheetField { Id = WellKnownIds.Str, Name = "STR", Type = FieldType.Number, Min = 8, Max = 20, ShowModifier = true,
                            Tooltip = "Strength — raw physical power. Affects melee attacks, lifting, and feats of brute force." },
                        new SheetField { Id = WellKnownIds.Dex, Name = "DEX", Type = FieldType.Number, Min = 8, Max = 20, ShowModifier = true,
                            Tooltip = "Dexterity — agility and reflexes. Affects ranged attacks, dodging, and precise movements." },
                        new SheetField { Id = WellKnownIds.Spd, Name = "SPD", Type = FieldType.Number, Min = 8, Max = 20, ShowModifier = true, IsInitiativeStat = true,
                            Tooltip = "Speed — reaction time. Its modifier is added as a bonus when rolling initiative." },
                        new SheetField { Id = WellKnownIds.Con, Name = "CON", Type = FieldType.Number, Min = 8, Max = 20, ShowModifier = true,
                            Tooltip = "Constitution — endurance and vitality. Its modifier adds to your maximum HP." },
                        new SheetField { Id = WellKnownIds.Mem, Name = "MEM", Type = FieldType.Number, Min = 8, Max = 20, ShowModifier = true,
                            Tooltip = "Memory — mental acuity and knowledge. Affects recall, lore, and aetheric affinity." },
                        new SheetField { Id = WellKnownIds.Mtl, Name = "MTL", Type = FieldType.Number, Min = 8, Max = 20, ShowModifier = true,
                            Tooltip = "Mettle — willpower and grit. Affects courage, mental resilience, and determination." },
                        new SheetField { Id = WellKnownIds.Int, Name = "INT", Type = FieldType.Number, Min = 8, Max = 20, ShowModifier = true,
                            Tooltip = "Intuition — perceptiveness and instinct. Affects reading situations, magic, and quick thinking." },
                        new SheetField { Id = WellKnownIds.Cha, Name = "CHA", Type = FieldType.Number, Min = 8, Max = 20, ShowModifier = true,
                            Tooltip = "Charisma — presence and social power. Affects persuasion, deception, and performance." },
                    ],
                },
                new SheetGroup
                {
                    Id = "builtin:specs", Name = "Specializations",
                    Fields = specFields,
                },
            ],
        };
    }
}
