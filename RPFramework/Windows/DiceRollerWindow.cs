using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models;

namespace RPFramework.Windows;

public class DiceRollerWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private int    _selectedDie     = 20;
    private int    _statModifierIdx = 0;
    private int    _specIdx         = 0;
    private int    _advantageMode   = 0;   // 0=Normal, 1=Advantage, 2=Disadvantage
    private readonly List<string> _history = new();

    private static readonly int[]    DieSizes  = { 4, 6, 8, 10, 12, 20, 100 };
    private static readonly string[] StatNames = { "None", "STR", "DEX", "SPD", "CON", "MEM", "MTL", "INT", "CHA" };
    private static readonly SkillStat[] StatMapping =
    {
        SkillStat.Str, // idx 1
        SkillStat.Dex, // idx 2
        SkillStat.Spd, // idx 3
        SkillStat.Con, // idx 4
        SkillStat.Mem, // idx 5
        SkillStat.Mtl, // idx 6
        SkillStat.Int, // idx 7
        SkillStat.Cha, // idx 8
    };
    private static readonly string[] Specializations =
    [
        "Acrobatics", "Animal Handling", "Thaumaturgy", "Arcanima", "Conjury",
        "History", "Insight", "Aetherology", "Intimidation", "Investigation",
        "Medicine", "Herbalism", "Perception", "Performance", "Persuasion",
        "Religion", "Sleight of Hand", "Bartering", "Stealth", "Deception",
        "Streetwise", "Hobnobbing", "Survival",
    ];
    private static readonly string[] SpecOptions;

    static DiceRollerWindow()
    {
        SpecOptions = new string[Specializations.Length + 1];
        SpecOptions[0] = "None";
        Array.Copy(Specializations, 0, SpecOptions, 1, Specializations.Length);
    }

    public DiceRollerWindow(Plugin plugin)
        : base("RP Dice Roller##RPFramework.Dice",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 340),
            MaximumSize = new Vector2(500, 700),
        };
        Size          = new Vector2(320, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        string? pid = plugin.LocalPlayerId;
        RpCharacter? ch = pid != null ? plugin.GetOrCreateCharacter(pid) : null;
        float scale = ImGuiHelpers.GlobalScale;

        // ── Die buttons ───────────────────────────────────────────────────────
        var highlightColor = new Vector4(0.26f, 0.59f, 0.98f, 1f);

        foreach (int die in DieSizes)
        {
            bool selected = _selectedDie == die;
            if (selected)
                ImGui.PushStyleColor(ImGuiCol.Button, highlightColor);

            if (ImGui.Button($"d{die}##rpdie_{die}"))
                _selectedDie = die;

            if (selected)
                ImGui.PopStyleColor();

            ImGui.SameLine();
        }

        // Custom die input
        ImGui.SetNextItemWidth(56 * scale);
        if (ImGui.InputInt("##rpdiecustom", ref _selectedDie, 0, 0))
            _selectedDie = Math.Clamp(_selectedDie, 1, 9999);

        ImGui.Spacing();
        ImGui.Separator();

        // ── Stat modifier ─────────────────────────────────────────────────────
        ImGui.TextUnformatted("Stat Modifier");
        ImGui.SetNextItemWidth(120 * scale);
        ImGui.Combo("##rpstatmod", ref _statModifierIdx, StatNames, StatNames.Length);

        if (_statModifierIdx > 0 && ch != null)
        {
            int modVal = GetStatValue(ch, _statModifierIdx);
            ImGui.SameLine();
            ImGui.TextDisabled($"({(modVal >= 0 ? "+" : "")}{modVal})");

            // AP exhaustion indicator
            int apPen = SkillHelpers.ApPenalty(ch);
            if (apPen < 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), $"[AP {apPen}]");
            }
        }

        // ── Specialization ────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextUnformatted("Specialization");

        bool specDisabled = ch == null;
        if (specDisabled)
        {
            using var _ = ImRaii.Disabled();
            ImGui.SetNextItemWidth(160 * scale);
            ImGui.Combo("##rpspecmod", ref _specIdx, SpecOptions, SpecOptions.Length);
        }
        else
        {
            ImGui.SetNextItemWidth(160 * scale);
            ImGui.Combo("##rpspecmod", ref _specIdx, SpecOptions, SpecOptions.Length);
        }

        if (_specIdx > 0 && ch != null)
        {
            string skillName = SpecOptions[_specIdx];
            ch.Proficiencies.TryGetValue(skillName, out bool hasProficiency);
            ImGui.SameLine();
            if (hasProficiency)
                ImGui.TextDisabled("(proficient)");
            else
                ImGui.TextDisabled("(no proficiency)");
        }

        // ── Advantage mode ────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.RadioButton("Normal##rpadv",   ref _advantageMode, 0); ImGui.SameLine();
        ImGui.RadioButton("Advantage##rpadv",    ref _advantageMode, 1); ImGui.SameLine();
        ImGui.RadioButton("Disadvantage##rpadv", ref _advantageMode, 2);

        // ── Roll button ───────────────────────────────────────────────────────
        ImGui.Spacing();
        if (ImGui.Button($"Roll d{_selectedDie}##rproll", new Vector2(-1, 0)))
            ExecuteRoll(ch);

        // ── History ───────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Recent rolls:");

        using var child = ImRaii.Child("##rpdicehistory", new Vector2(-1, -1));
        if (!child) return;

        foreach (var entry in _history)
            ImGui.TextUnformatted(entry);
    }

    public void RollFromCommand(string args)
    {
        args = args.Trim().ToLowerInvariant().TrimStart('d');
        if (int.TryParse(args, out int n) && n >= 1)
            _selectedDie = n;
        string? pid = plugin.LocalPlayerId;
        var ch = pid != null ? plugin.GetOrCreateCharacter(pid) : null;
        ExecuteRoll(ch);
    }

    private void ExecuteRoll(RpCharacter? ch)
    {
        int modVal = 0;
        if (_statModifierIdx > 0 && ch != null)
            modVal = GetStatValue(ch, _statModifierIdx);

        bool hasProficiency = false;
        string? specName = null;
        if (_specIdx > 0 && ch != null)
        {
            specName = SpecOptions[_specIdx];
            ch.Proficiencies.TryGetValue(specName, out hasProficiency);
        }

        // Resolve effective roll mode
        // Proficiency + Disadvantage cancels to single roll
        // Proficiency alone = advantage
        bool rollTwice;
        bool keepHigher;
        string modeTag = "";

        if (hasProficiency && _advantageMode == 2)
        {
            rollTwice  = false;
            keepHigher = true;
            modeTag    = " [prof+dis→cancelled]";
        }
        else if (hasProficiency || _advantageMode == 1)
        {
            rollTwice  = true;
            keepHigher = true;
            modeTag    = hasProficiency && _advantageMode == 1 ? " [adv+prof]"
                       : hasProficiency                        ? " [proficiency]"
                                                               : " [advantage]";
        }
        else if (_advantageMode == 2)
        {
            rollTwice  = true;
            keepHigher = false;
            modeTag    = " [disadvantage]";
        }
        else
        {
            rollTwice  = false;
            keepHigher = true;
        }

        // Append AP exhaustion tag if active
        if (ch != null)
        {
            int apPen = SkillHelpers.ApPenalty(ch);
            if (apPen < 0)
                modeTag += $" [AP {apPen}]";
        }

        int r1 = RollDie(_selectedDie);
        int rawRoll;
        string rollPart;

        if (rollTwice)
        {
            int r2 = RollDie(_selectedDie);
            rawRoll  = keepHigher ? Math.Max(r1, r2) : Math.Min(r1, r2);
            rollPart = $": {r1},{r2}→{rawRoll}";
        }
        else
        {
            rawRoll  = r1;
            rollPart = $": {r1}";
        }

        int total = rawRoll + modVal;

        string modPart  = _statModifierIdx > 0
                            ? $" + {StatNames[_statModifierIdx]}({(modVal >= 0 ? "+" : "")}{modVal})"
                            : "";
        string specPart = specName != null ? $" [{specName}]" : "";

        string line = $"[RPDice] d{_selectedDie}{specPart}{modPart}{rollPart} = {total}{modeTag}";

        Plugin.ChatGui.Print(line);
        _history.Insert(0, line);
        if (_history.Count > 5)
            _history.RemoveAt(_history.Count - 1);
    }

    private static int RollDie(int sides) =>
        Random.Shared.Next(1, sides + 1);

    /// Returns the effective modifier for the given stat index, incorporating
    /// any active passive condition adjustments and the AP exhaustion penalty.
    private static int GetStatValue(RpCharacter ch, int statIdx)
    {
        if (statIdx < 1 || statIdx > 8) return 0;

        SkillStat skillStat = StatMapping[statIdx - 1];

        int raw = statIdx switch
        {
            1 => ch.Str,
            2 => ch.Dex,
            3 => ch.Spd,
            4 => ch.Con,
            5 => ch.Mem,
            6 => ch.Mtl,
            7 => ch.Int,
            8 => ch.Cha,
            _ => 10,
        };

        int passiveAdj = SkillHelpers.PassiveStatAdjust(ch, skillStat);
        return SkillHelpers.StatMod(raw + passiveAdj) + SkillHelpers.ApPenalty(ch);
    }
}
