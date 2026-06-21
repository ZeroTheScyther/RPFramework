using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Shared editor for a flat list of conditional <see cref="EffectBlock"/>s, used by both the skill
/// editor and the item-effects editor. Each block renders an "If ALL of" conditions table plus a
/// "Then" effects table and a remove button; an add-block button sits at the bottom. The caller
/// supplies the condition/effect target field lists so each context controls which targets are
/// offered (equipment, for instance, omits bar-current effects). Returns true if anything changed,
/// was added, or was removed this frame so the caller can flag itself dirty.
/// </summary>
public static class BlockListEditor
{
    public static bool Draw(
        List<EffectBlock> blocks,
        List<SheetField> condFields, string[] condNames,
        List<SheetField> fxFields,   string[] fxNames,
        float scale, string idScope)
    {
        bool changed = false;
        ImGui.PushID(idScope);

        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            ImGui.PushID($"blk{b}");

            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f), $"Block {b + 1}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove block")) { blocks.RemoveAt(b); ImGui.PopID(); changed = true; break; }

            ImGui.TextDisabled(block.Conditions.Count == 0
                ? "If: (no conditions = always active)"
                : "If ALL of:");
            changed |= DrawConditionTable(block.Conditions, condFields, condNames, scale);
            if (ImGui.SmallButton("+ condition"))
            { block.Conditions.Add(new SkillCondition { FieldId = condFields.FirstOrDefault()?.Id ?? "" }); changed = true; }

            ImGui.TextDisabled("Then:");
            changed |= DrawEffectTable("##blkthen", block.Effects, fxFields, fxNames, scale);
            if (ImGui.SmallButton("+ effect"))
            { block.Effects.Add(new SkillEffect { FieldId = fxFields.FirstOrDefault()?.Id ?? "" }); changed = true; }

            // Else branch only makes sense with conditions (an unconditional block's else never fires).
            if (block.Conditions.Count > 0)
            {
                ImGui.TextDisabled("Else:");
                changed |= DrawEffectTable("##blkelse", block.ElseEffects, fxFields, fxNames, scale);
                if (ImGui.SmallButton("+ else effect"))
                { block.ElseEffects.Add(new SkillEffect { FieldId = fxFields.FirstOrDefault()?.Id ?? "" }); changed = true; }
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.SmallButton("+ Add conditional block"))
        { blocks.Add(new EffectBlock()); changed = true; }

        ImGui.PopID();
        return changed;
    }

    private static bool DrawConditionTable(List<SkillCondition> conds, List<SheetField> fields, string[] names, float scale)
    {
        if (conds.Count == 0) return false;
        bool changed = false;
        if (ImGui.BeginTable("##blkcond", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            SetupPartCols(scale);
            for (int i = conds.Count - 1; i >= 0; i--)
            {
                ImGui.TableNextRow(); ImGui.PushID($"bc{i}");
                if (ConditionEditor.DrawRow(conds[i], fields, names)) changed = true;
                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton("X")) { conds.RemoveAt(i); changed = true; }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        return changed;
    }

    private static bool DrawEffectTable(string tableId, List<SkillEffect> fxs, List<SheetField> fields, string[] names, float scale)
    {
        if (fxs.Count == 0) return false;
        bool changed = false;
        if (ImGui.BeginTable(tableId, 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            SetupPartCols(scale);
            for (int i = fxs.Count - 1; i >= 0; i--)
            {
                ImGui.TableNextRow(); ImGui.PushID($"be{i}");
                if (EffectEditor.DrawRow(fxs[i], fields, names)) changed = true;
                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton("X")) { fxs.RemoveAt(i); changed = true; }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        return changed;
    }

    private static void SetupPartCols(float scale)
    {
        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 80 * scale);
        ImGui.TableSetupColumn("Op",    ImGuiTableColumnFlags.WidthFixed, 52 * scale);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 60 * scale);
        ImGui.TableSetupColumn("%",     ImGuiTableColumnFlags.WidthFixed, 24 * scale);
        ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 22 * scale);
        ImGui.TableHeadersRow();
    }
}
