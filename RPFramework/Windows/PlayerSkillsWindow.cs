using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>Read-only skills list for a remote player, read straight from the store.</summary>
public class PlayerSkillsWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _playerId;
    private readonly string? _code;
    private readonly Action<string> _onClosed;
    private int _selectedIdx = -1;

    private static readonly string[] CondOpNames   = { "<", "≤", "=", "≥", ">" };
    private static readonly string[] EffectOpNames = { "+", "−", "=", "×", "÷" };

    public PlayerSkillsWindow(Plugin plugin, string playerId, Action<string> onClosed)
        : base($"Skills##rpsk_{playerId}",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin   = plugin;
        _playerId = playerId;
        _code     = plugin.ActiveCampaign;
        _onClosed = onClosed;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 400),
            MaximumSize = new Vector2(900, 800),
        };
        Size          = new Vector2(600, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnClose() => _onClosed(_playerId);
    public void Dispose() { }

    private List<RpSkill> Skills
    {
        get
        {
            var ch = _code != null ? _plugin.Store.Character(_code, _playerId) : null;
            return ch?.State.Skills ?? new List<RpSkill>();
        }
    }

    public override void Draw()
        => DrawReadOnly(_plugin.Store.TemplateOrDefault(_code), Skills, ref _selectedIdx, ImGuiHelpers.GlobalScale);

    /// <summary>Read-only skills browser (left list + detail) for any skill set. Reused by the Companion
    /// tab; the caller owns the selection index.</summary>
    public static void DrawReadOnly(SheetTemplate template, List<RpSkill> skills, ref int selectedIdx, float scale)
    {
        float leftW = 170 * scale;
        using (var left = ImRaii.Child("##rpviewsklist", new Vector2(leftW, -1), false))
        {
            if (left)
            {
                if (selectedIdx >= skills.Count) selectedIdx = skills.Count - 1;

                for (int i = 0; i < skills.Count; i++)
                {
                    var    sk    = skills[i];
                    string badge = sk.Type == SkillType.Active ? "[A]" : "[P]";
                    bool   sel   = selectedIdx == i;

                    if (sel) ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.26f, 0.59f, 0.98f, 0.4f));
                    if (ImGui.Selectable($"{badge} {sk.Name}##rpviewsk_{i}", sel)) selectedIdx = i;
                    if (sel) ImGui.PopStyleColor();
                }

                if (skills.Count == 0) ImGui.TextDisabled("No skills.");
            }
        }

        ImGui.SameLine();

        using var right = ImRaii.Child("##rpviewskeditor", new Vector2(-1, -1), false);
        if (!right) return;

        if (selectedIdx < 0 || selectedIdx >= skills.Count)
        {
            if (skills.Count > 0) ImGui.TextDisabled("Select a skill to view.");
            return;
        }

        DrawSkillView(template, skills[selectedIdx]);
    }

    private static void DrawSkillView(SheetTemplate template, RpSkill skill)
    {
        string GetFieldName(string fid) => template.FindField(fid)?.Name ?? fid;

        ImGui.TextDisabled(skill.Type == SkillType.Active ? "[Active]" : "[Passive]");
        ImGui.SameLine();
        ImGui.TextUnformatted(skill.Name);

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            ImGui.Spacing();
            ImGui.TextDisabled(skill.Description);
        }

        ImGui.Spacing();
        if (skill.Cooldown > 0) { ImGui.SameLine(); ImGui.TextDisabled($"  Cooldown: {skill.Cooldown}t"); }
        if (skill.Duration > 0) { ImGui.SameLine(); ImGui.TextDisabled($"  Duration: {skill.Duration}t"); }

        ImGui.Spacing();
        ImGui.Separator();

        if (skill.Type == SkillType.Passive && skill.Conditions.Count > 0)
        {
            ImGui.TextUnformatted("Conditions");
            foreach (var c in skill.Conditions)
            {
                string pct = c.IsPercentage ? "%" : "";
                ImGui.TextDisabled($"  {GetFieldName(c.FieldId)} {CondOpNames[(int)c.Op]} {c.Value:0}{pct}");
            }
            ImGui.Spacing();
            ImGui.Separator();
        }

        if (skill.Effects.Count > 0)
        {
            ImGui.TextUnformatted("Effects");
            foreach (var fx in skill.Effects)
            {
                string pct = fx.IsPercentage ? "%" : "";
                ImGui.TextDisabled($"  {GetFieldName(fx.FieldId)} {EffectOpNames[(int)fx.Op]} {fx.Value:0.#}{pct}");
            }
        }
    }
}
