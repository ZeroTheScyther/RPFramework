using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>Read-only skills browser, shared by the remote-player sheet (Skills tab) and the Companion tab.</summary>
public static class PlayerSkillsWindow
{
    /// <summary>Read-only skills browser (left list + detail) for any skill set. Reused by the Companion
    /// tab; the caller owns the selection index.</summary>
    public static void DrawReadOnly(SheetTemplate template, List<RpSkill> skills, ref int selectedIdx, float scale,
                                    IReadOnlyList<(string Item, RpSkill Skill)>? granted = null)
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
                    string tag   = sk.Active ? " *" : "";
                    bool   sel   = selectedIdx == i;

                    if (sel) ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.26f, 0.59f, 0.98f, 0.4f));
                    if (ImGui.Selectable($"{badge} {sk.Name}{tag}##rpviewsk_{i}", sel)) selectedIdx = i;
                    if (sel) ImGui.PopStyleColor();
                }

                if (skills.Count == 0 && (granted == null || granted.Count == 0)) ImGui.TextDisabled("No skills.");

                // Passives inherited from equipped items (read-only).
                if (granted is { Count: > 0 })
                {
                    ImGuiHelpers.ScaledDummy(4f);
                    CharacterSheetWindow.DrawSectionHeader("From Equipment", scale);
                    foreach (var (itemName, p) in granted)
                    {
                        ImGui.TextDisabled($"[P] {p.Name}");
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted($"From {itemName}");
                            string fx = ItemEffects.Summary(p.Effects, template);
                            if (!string.IsNullOrEmpty(fx)) ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), fx);
                            ImGui.EndTooltip();
                        }
                    }
                }
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

    private static readonly Vector4 Rule = new(0.45f, 0.85f, 0.45f, 1f);

    private static void DrawSkillView(SheetTemplate template, RpSkill skill)
    {
        ImGui.TextDisabled(skill.Type == SkillType.Active ? "[Active]" : "[Passive]");
        ImGui.SameLine();
        ImGui.TextUnformatted(skill.Name);
        if (skill.Active) { ImGui.SameLine(); ImGui.TextColored(Rule, "● Active"); }

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(skill.Description);
            ImGui.PopTextWrapPos();
        }

        if (skill.Cooldown > 0 || skill.Duration > 0)
        {
            ImGui.Spacing();
            if (skill.Cooldown > 0) ImGui.TextDisabled($"Cooldown: {skill.Cooldown}t");
            if (skill.Duration > 0) { if (skill.Cooldown > 0) ImGui.SameLine(); ImGui.TextDisabled($"  Duration: {skill.Duration}t"); }
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Base effects (the editor folds base conditions into blocks, so these are unconditional).
        string baseFx = ItemEffects.Summary(skill.Effects, template);
        if (!string.IsNullOrEmpty(baseFx))
        {
            ImGui.TextUnformatted("Effects");
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(Rule, baseFx);
            ImGui.PopTextWrapPos();
        }

        if (skill.ConditionalBlocks.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Conditional");
            ImGui.PushTextWrapPos(0f);
            foreach (var b in skill.ConditionalBlocks)
            {
                string fx = ItemEffects.Summary(b.Effects, template);
                if (string.IsNullOrEmpty(fx)) continue;
                string cond = ItemEffects.ConditionSummary(b.Conditions, template);
                ImGui.TextColored(Rule, string.IsNullOrEmpty(cond) ? $"Always: {fx}" : $"If {cond}: {fx}");
                string elseFx = ItemEffects.Summary(b.ElseEffects, template);
                if (!string.IsNullOrEmpty(elseFx)) ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1f), $"  Else: {elseFx}");
            }
            ImGui.PopTextWrapPos();
        }
    }
}
