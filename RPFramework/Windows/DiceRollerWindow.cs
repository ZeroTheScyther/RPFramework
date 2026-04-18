using System;
using System.Collections.Generic;
using System.Linq;
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

    private static readonly int[] DieSizes = { 4, 6, 8, 10, 12, 20, 100 };

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

        var template     = plugin.Configuration.ActiveTemplate;
        var numberFields = template.Groups.SelectMany(g => g.Fields)
                                          .Where(f => f.Type == FieldType.Number)
                                          .ToList();
        var checkFields  = template.Groups.SelectMany(g => g.Fields)
                                          .Where(f => f.Type == FieldType.Checkbox)
                                          .ToList();

        var statOptions = new string[numberFields.Count + 1];
        statOptions[0] = "None";
        for (int i = 0; i < numberFields.Count; i++) statOptions[i + 1] = numberFields[i].Name;

        var specOptions = new string[checkFields.Count + 1];
        specOptions[0] = "None";
        for (int i = 0; i < checkFields.Count; i++) specOptions[i + 1] = checkFields[i].Name;

        // Clamp indices after template changes
        if (_statModifierIdx >= statOptions.Length) _statModifierIdx = 0;
        if (_specIdx         >= specOptions.Length) _specIdx         = 0;

        // ── Die buttons ───────────────────────────────────────────────────────
        var highlightColor = new Vector4(0.26f, 0.59f, 0.98f, 1f);

        foreach (int die in DieSizes)
        {
            bool selected = _selectedDie == die;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, highlightColor);

            if (ImGui.Button($"d{die}##rpdie_{die}"))
                _selectedDie = die;

            if (selected) ImGui.PopStyleColor();
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(56 * scale);
        if (ImGui.InputInt("##rpdiecustom", ref _selectedDie, 0, 0))
            _selectedDie = Math.Clamp(_selectedDie, 1, 9999);

        ImGui.Spacing();
        ImGui.Separator();

        // ── Stat modifier ─────────────────────────────────────────────────────
        ImGui.TextUnformatted("Stat Modifier");
        ImGui.SetNextItemWidth(120 * scale);
        ImGui.Combo("##rpstatmod", ref _statModifierIdx, statOptions, statOptions.Length);

        if (_statModifierIdx > 0 && ch != null)
        {
            var selField = numberFields[_statModifierIdx - 1];
            int modVal   = GetStatValue(ch, selField, template);
            ImGui.SameLine();
            ImGui.TextDisabled($"({(modVal >= 0 ? "+" : "")}{modVal})");

            int apPen = SkillHelpers.ApPenalty(ch, template);
            if (apPen < 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), $"[AP {apPen}]");
            }
        }

        // ── Specialization ────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextUnformatted("Specialization");

        ImGui.SetNextItemWidth(160 * scale);
        ImGui.Combo("##rpspecmod", ref _specIdx, specOptions, specOptions.Length);

        if (_specIdx > 0 && ch != null)
        {
            var specField = checkFields[_specIdx - 1];
            ch.CheckValues.TryGetValue(specField.Id, out bool hasProficiency);
            ImGui.SameLine();
            ImGui.TextDisabled(hasProficiency ? "(proficient)" : "(no proficiency)");
        }

        // ── Advantage mode ────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.RadioButton("Normal##rpadv",        ref _advantageMode, 0); ImGui.SameLine();
        ImGui.RadioButton("Advantage##rpadv",     ref _advantageMode, 1); ImGui.SameLine();
        ImGui.RadioButton("Disadvantage##rpadv",  ref _advantageMode, 2);

        // ── Roll button ───────────────────────────────────────────────────────
        ImGui.Spacing();
        if (ImGui.Button($"Roll d{_selectedDie}##rproll", new Vector2(-1, 0)))
            ExecuteRoll(ch, numberFields, checkFields, template);

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
        var template     = plugin.Configuration.ActiveTemplate;
        var numberFields = template.Groups.SelectMany(g => g.Fields)
                                          .Where(f => f.Type == FieldType.Number).ToList();
        var checkFields  = template.Groups.SelectMany(g => g.Fields)
                                          .Where(f => f.Type == FieldType.Checkbox).ToList();
        ExecuteRoll(ch, numberFields, checkFields, template);
    }

    private void ExecuteRoll(RpCharacter? ch,
                             List<SheetField> numberFields, List<SheetField> checkFields,
                             SheetTemplate template)
    {
        int modVal = 0;
        string statName = "";
        if (_statModifierIdx > 0 && ch != null && _statModifierIdx <= numberFields.Count)
        {
            var selField = numberFields[_statModifierIdx - 1];
            modVal   = GetStatValue(ch, selField, template);
            statName = selField.Name;
        }

        bool   hasProficiency = false;
        string specName       = "";
        if (_specIdx > 0 && ch != null && _specIdx <= checkFields.Count)
        {
            var specField = checkFields[_specIdx - 1];
            ch.CheckValues.TryGetValue(specField.Id, out hasProficiency);
            specName = specField.Name;
        }

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

        if (ch != null)
        {
            int apPen = SkillHelpers.ApPenalty(ch, template);
            if (apPen < 0) modeTag += $" [AP {apPen}]";
        }

        int r1 = RollDie(_selectedDie);
        int rawRoll;
        string rollPart;

        if (rollTwice)
        {
            int r2   = RollDie(_selectedDie);
            rawRoll  = keepHigher ? Math.Max(r1, r2) : Math.Min(r1, r2);
            rollPart = $": {r1},{r2}→{rawRoll}";
        }
        else
        {
            rawRoll  = r1;
            rollPart = $": {r1}";
        }

        int total = rawRoll + modVal;

        string modPart  = statName.Length > 0
                            ? $" + {statName}({(modVal >= 0 ? "+" : "")}{modVal})"
                            : "";
        string specPart = specName.Length > 0 ? $" [{specName}]" : "";

        string line = $"d{_selectedDie}{specPart}{modPart}{rollPart} = {total}{modeTag}";

        Plugin.ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
        {
            Message = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
                .AddUiForeground("[RPDice] ", 32)
                .AddText(line)
                .Build(),
            Type = Dalamud.Game.Text.XivChatType.Echo,
        });

        _history.Insert(0, line);
        if (_history.Count > 5)
            _history.RemoveAt(_history.Count - 1);

        // Broadcast to party members
        if (plugin.Network.IsConnected)
        {
            string? pid         = plugin.LocalPlayerId;
            string  displayName = plugin.LocalDisplayName;
            foreach (var party in plugin.Configuration.Parties)
            {
                var dto = new RPFramework.Models.Net.DiceRollBroadcastDto(
                    party.Code, pid ?? "", displayName, line);
                System.Threading.Tasks.Task.Run(() => plugin.Network.BroadcastDiceRollAsync(dto));
            }
        }
    }

    private static int RollDie(int sides) => Random.Shared.Next(1, sides + 1);

    private static int GetStatValue(RpCharacter ch, SheetField field, SheetTemplate template)
    {
        ch.StatValues.TryGetValue(field.Id, out int raw);
        int passiveAdj = SkillHelpers.PassiveStatAdjust(ch, field.Id, template);
        return SkillHelpers.StatMod(raw + passiveAdj) + SkillHelpers.ApPenalty(ch, template);
    }
}
