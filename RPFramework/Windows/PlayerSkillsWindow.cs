using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models;
using RPFramework.Models.Net;

namespace RPFramework.Windows;

/// <summary>
/// Read-only skills list for a remote player, populated from the relay server.
/// </summary>
public class PlayerSkillsWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private CharacterProfileDto _profile;
    private int _selectedIdx = -1;
    private readonly Action<string> _onClosed;

    private static readonly string[] CondOpNames   = { "<", "≤", "=", "≥", ">" };
    private static readonly string[] EffectOpNames = { "+", "−", "=" };

    public PlayerSkillsWindow(Plugin plugin, CharacterProfileDto profile, Action<string> onClosed)
        : base($"{profile.DisplayName} Skills##rpsk_{profile.PlayerId}",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin   = plugin;
        _profile  = profile;
        _onClosed = onClosed;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 400),
            MaximumSize = new Vector2(900, 800),
        };
        Size          = new Vector2(600, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void UpdateProfile(CharacterProfileDto profile)
    {
        _profile = profile;
        if (_selectedIdx >= _profile.Skills.Count) _selectedIdx = -1;
    }

    public override void OnClose() => _onClosed(_profile.PlayerId);
    public void Dispose() { }

    public override void Draw()
    {
        float scale = ImGuiHelpers.GlobalScale;
        float leftW = 170 * scale;

        using (var left = ImRaii.Child("##rpviewsklist", new Vector2(leftW, -1), false))
        {
            if (left)
            {
                if (_selectedIdx >= _profile.Skills.Count)
                    _selectedIdx = _profile.Skills.Count - 1;

                for (int i = 0; i < _profile.Skills.Count; i++)
                {
                    var    sk    = _profile.Skills[i];
                    string badge = sk.Type == SkillType.Active ? "[A]" : "[P]";
                    string label = $"{badge} {sk.Name}##rpviewsk_{i}";
                    bool   sel   = _selectedIdx == i;

                    if (sel) ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.26f, 0.59f, 0.98f, 0.4f));
                    if (ImGui.Selectable(label, sel)) _selectedIdx = i;
                    if (sel) ImGui.PopStyleColor();
                }

                if (_profile.Skills.Count == 0)
                    ImGui.TextDisabled("No skills.");
            }
        }

        ImGui.SameLine();

        using var right = ImRaii.Child("##rpviewskeditor", new Vector2(-1, -1), false);
        if (!right) return;

        if (_selectedIdx < 0 || _selectedIdx >= _profile.Skills.Count)
        {
            ImGui.TextDisabled("Select a skill to view.");
            return;
        }

        DrawSkillView(_profile.Skills[_selectedIdx]);
    }

    private void DrawSkillView(RpSkillDto skill)
    {
        var template = _plugin.Configuration.ActiveTemplate;
        string GetFieldName(string fid) => template.FindField(fid)?.Name ?? fid;

        ImGui.TextUnformatted(skill.Name);

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            ImGui.Spacing();
            ImGui.TextDisabled(skill.Description);
        }

        ImGui.Spacing();
        string typeBadge = skill.Type == SkillType.Active ? "[Active]" : "[Passive]";
        ImGui.TextDisabled(typeBadge);
        if (skill.Cooldown > 0) { ImGui.SameLine(); ImGui.TextDisabled($"  Cooldown: {skill.Cooldown}t"); }
        if (skill.Duration > 0) { ImGui.SameLine(); ImGui.TextDisabled($"  Duration: {skill.Duration}t"); }

        ImGui.Spacing();
        ImGui.Separator();

        if (skill.Type == SkillType.Passive && skill.Conditions.Count > 0)
        {
            ImGui.TextUnformatted("Conditions");
            foreach (var c in skill.Conditions)
            {
                string fid = SkillHelpers.EffectiveFieldId(c);
                string pct = c.IsPercentage ? "%" : "";
                ImGui.TextDisabled($"  {GetFieldName(fid)} {CondOpNames[(int)c.Op]} {c.Value:0}{pct}");
            }
            ImGui.Spacing();
            ImGui.Separator();
        }

        if (skill.Effects.Count > 0)
        {
            ImGui.TextUnformatted("Effects");
            foreach (var fx in skill.Effects)
            {
                string fid = SkillHelpers.EffectiveFieldId(fx);
                string pct = fx.IsPercentage ? "%" : "";
                ImGui.TextDisabled($"  {GetFieldName(fid)} {EffectOpNames[(int)fx.Op]} {fx.Value:0.#}{pct}");
            }
        }
    }
}
