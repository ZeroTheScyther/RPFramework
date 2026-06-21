using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Server-first dice roller: the client only picks the die/stat/spec/mode and sends a
/// RollDice intent. The server rolls authoritatively and broadcasts the result, which the
/// plugin prints to chat. No client-side RNG or stat math (only a read-only modifier preview).
/// </summary>
public class DiceRollerWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private int _selectedDie     = 20;
    private int _statModifierIdx = 0;
    private int _specIdx         = 0;
    private int _advantageMode   = 0;   // 0=Normal, 1=Advantage, 2=Disadvantage
    private int _rollAsIdx       = 0;   // who to roll as: yourself, a companion, or (DM) an NPC

    private static readonly int[] DieSizes = { 4, 6, 8, 10, 12, 20, 100 };

    public DiceRollerWindow(Plugin plugin)
        : base("RP Dice Roller##RPFramework.Dice",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 280),
            MaximumSize = new Vector2(500, 600),
        };
        Size          = new Vector2(320, 340);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        float scale    = ImGuiHelpers.GlobalScale;
        var   template = plugin.ActiveTemplate;
        string? code   = plugin.ActiveCampaign;

        // ── Roll as: yourself, one of your companions, or (DM only) an NPC from the vault ──
        var rollAs = BuildRollAsList(code);
        if (_rollAsIdx >= rollAs.Count) _rollAsIdx = 0;
        string rollAsId = rollAs.Count > 0 ? rollAs[_rollAsIdx].Id : (plugin.LocalPlayerId ?? "");
        var    state    = code != null ? plugin.Store.Character(code, rollAsId)?.State : null;

        if (rollAs.Count > 1)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(CharacterSheetWindow.LabelMuted, "Roll as");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);   // dropdown fills the rest of the row, to the right of the label
            ImGui.Combo("##rprollas", ref _rollAsIdx, rollAs.Select(r => r.Label).ToArray(), rollAs.Count);
            ImGui.Spacing();
            ImGui.Separator();            // clearly divide the "roll as" row from the dice buttons
            ImGui.Spacing();
        }

        var numberFields = template.Groups.SelectMany(g => g.Fields).Where(f => f.Type == FieldType.Number).ToList();
        var checkFields  = template.Groups.SelectMany(g => g.Fields).Where(f => f.Type == FieldType.Checkbox).ToList();

        var statOptions = new string[numberFields.Count + 1];
        statOptions[0] = "None";
        for (int i = 0; i < numberFields.Count; i++) statOptions[i + 1] = numberFields[i].Name;

        var specOptions = new string[checkFields.Count + 1];
        specOptions[0] = "None";
        for (int i = 0; i < checkFields.Count; i++) specOptions[i + 1] = checkFields[i].Name;

        if (_statModifierIdx >= statOptions.Length) _statModifierIdx = 0;
        if (_specIdx         >= specOptions.Length) _specIdx         = 0;

        // ── Die buttons ───────────────────────────────────────────────────────
        var highlightColor = new Vector4(0.26f, 0.59f, 0.98f, 1f);
        foreach (int die in DieSizes)
        {
            bool selected = _selectedDie == die;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, highlightColor);
            if (ImGui.Button($"d{die}##rpdie_{die}")) _selectedDie = die;
            if (selected) ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        ImGui.SetNextItemWidth(56 * scale);
        if (ImGui.InputInt("##rpdiecustom", ref _selectedDie, 0, 0))
            _selectedDie = Math.Clamp(_selectedDie, 2, 1000);

        ImGui.Spacing();

        // ── Stat modifier ─────────────────────────────────────────────────────
        CharacterSheetWindow.DrawSectionHeader("Stat Modifier", scale);
        ImGui.SetNextItemWidth(120 * scale);
        ImGui.Combo("##rpstatmod", ref _statModifierIdx, statOptions, statOptions.Length);
        if (_statModifierIdx > 0 && state != null)
        {
            var f      = numberFields[_statModifierIdx - 1];
            int modVal = StatMath.StatMod(StatMath.EffectiveStat(state, f.Id, template)) + StatMath.ApPenalty(state, template);
            ImGui.SameLine();
            ImGui.TextDisabled($"({(modVal >= 0 ? "+" : "")}{modVal})");
        }

        // ── Specialization ────────────────────────────────────────────────────
        ImGui.Spacing();
        CharacterSheetWindow.DrawSectionHeader("Specialization", scale);
        ImGui.SetNextItemWidth(160 * scale);
        ImGui.Combo("##rpspecmod", ref _specIdx, specOptions, specOptions.Length);
        if (_specIdx > 0 && state != null)
        {
            bool prof = StatMath.EffectiveCheck(state, checkFields[_specIdx - 1].Id, template);
            ImGui.SameLine();
            ImGui.TextDisabled(prof ? "(proficient)" : "(no proficiency)");
        }

        // ── Advantage mode ────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.RadioButton("Normal##rpadv",       ref _advantageMode, 0); ImGui.SameLine();
        ImGui.RadioButton("Advantage##rpadv",    ref _advantageMode, 1); ImGui.SameLine();
        ImGui.RadioButton("Disadvantage##rpadv", ref _advantageMode, 2);

        // ── Roll ──────────────────────────────────────────────────────────────
        ImGui.Spacing();
        bool canRoll = plugin.ActiveCampaign != null && plugin.Network.IsConnected;
        if (!canRoll) ImGui.BeginDisabled();
        if (ImGui.Button($"Roll d{_selectedDie}##rproll", new Vector2(-1, 0)))
        {
            string? stat = _statModifierIdx > 0 ? numberFields[_statModifierIdx - 1].Id : null;
            string? spec = _specIdx > 0 ? checkFields[_specIdx - 1].Id : null;
            Send(rollAsId, _selectedDie, Mode(), stat, spec);
        }
        if (!canRoll) ImGui.EndDisabled();
        if (!plugin.Network.IsConnected)
            ImGui.TextDisabled("Connect to a server to roll.");
    }

    private RollMode Mode() => _advantageMode switch
    {
        1 => RollMode.Advantage,
        2 => RollMode.Disadvantage,
        _ => RollMode.Normal,
    };

    /// <summary>The roll-as choices for the active campaign: yourself first, then your companions, then
    /// (DM only) the campaign's NPCs. Each entry pairs the entity id with a display label.</summary>
    private List<(string Id, string Label)> BuildRollAsList(string? code)
    {
        var list = new List<(string, string)>();
        string pid = plugin.LocalPlayerId ?? "";
        list.Add((pid, "Yourself"));
        if (code == null) return list;
        foreach (var c in plugin.Store.CompanionsOf(code, pid).OrderBy(c => c.DisplayName))
            list.Add((c.EntityId, c.DisplayName));
        if (plugin.IsDm(code))
            foreach (var n in plugin.Store.NpcsIn(code).OrderBy(n => n.DisplayName))
                list.Add((n.EntityId, $"{n.DisplayName} (NPC)"));
        return list;
    }

    private void Send(string entityId, int die, RollMode mode, string? statFieldId, string? specFieldId)
    {
        string? code = plugin.ActiveCampaign;
        if (code == null) return;
        _ = plugin.Network.RollDice(code, entityId, die, mode, statFieldId, specFieldId);
    }

    /// <summary>Quick roll from chat (/rpdice dN) — no stat/spec modifiers, normal mode, as yourself.</summary>
    public void RollFromCommand(string args)
    {
        args = args.Trim().ToLowerInvariant().TrimStart('d');
        if (int.TryParse(args, out int n) && n >= 2)
            _selectedDie = Math.Clamp(n, 2, 1000);
        Send(plugin.LocalPlayerId ?? "", _selectedDie, RollMode.Normal, null, null);
    }
}
