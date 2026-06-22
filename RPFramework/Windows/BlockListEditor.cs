using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Shared editor for a flat list of conditional <see cref="EffectBlock"/>s, used by the skill editor and
/// the item-effects editor. Each block has a Trigger selector (On Active = applies while engaged / On Turn
/// End = fires once per turn-end, encoded by the <see cref="StatMath.OnTurnEndId"/> marker condition), an
/// "If ALL of" conditions table, a "Then"/"Else" effects table, and a remove button. Returns true if
/// anything changed so the caller can flag itself dirty.
/// </summary>
public static class BlockListEditor
{
    private static readonly string[] TriggerNames = { "On Active", "On Turn End" };

    public static bool Draw(
        List<EffectBlock> blocks, SheetTemplate template, bool includeBarCurrent,
        IReadOnlyList<RpSkill>? grantablePassives, float scale, string idScope,
        bool allowTurnEnd = true)
    {
        bool changed = false;
        ImGui.PushID(idScope);

        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            ImGui.PushID($"blk{b}");

            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Block {b + 1}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove block")) { blocks.RemoveAt(b); ImGui.PopID(); changed = true; break; }

            // Trigger: On Active vs On Turn End (the latter encoded by a marker condition on the block).
            if (allowTurnEnd)
            {
                bool isTurnEnd = block.Conditions.Any(c => c.FieldId == StatMath.OnTurnEndId);
                ImGui.SameLine();
                ImGui.TextDisabled("Trigger:"); ImGui.SameLine();
                ImGui.SetNextItemWidth(120 * scale);
                int ti = isTurnEnd ? 1 : 0;
                if (ImGui.Combo("##blktrig", ref ti, TriggerNames, TriggerNames.Length))
                {
                    bool wantTurnEnd = ti == 1;
                    if (wantTurnEnd && !isTurnEnd) { block.Conditions.Insert(0, new SkillCondition { FieldId = StatMath.OnTurnEndId }); changed = true; }
                    else if (!wantTurnEnd && isTurnEnd) { block.Conditions.RemoveAll(c => c.FieldId == StatMath.OnTurnEndId); changed = true; }
                }
            }

            // Real (non-marker) conditions are what the user edits.
            var conds = block.Conditions.Where(c => c.FieldId != StatMath.OnTurnEndId).ToList();
            ImGui.TextDisabled(conds.Count == 0 ? "If: (no conditions = always)" : "If ALL of:");
            changed |= DrawConditionTable(block.Conditions, conds, template, scale);
            if (ImGui.SmallButton("+ condition"))
            { block.Conditions.Add(ConditionEditor.NewCondition(template)); changed = true; }

            ImGui.TextDisabled("Then:");
            changed |= DrawEffectTable("##blkthen", block.Effects, template, includeBarCurrent, grantablePassives, scale);
            if (ImGui.SmallButton("+ effect"))
            { block.Effects.Add(EffectEditor.NewEffect(template)); changed = true; }

            // Else only makes sense with real conditions (an unconditional/turn-end block's else never fires).
            if (conds.Count > 0)
            {
                ImGui.TextDisabled("Else:");
                changed |= DrawEffectTable("##blkelse", block.ElseEffects, template, includeBarCurrent, grantablePassives, scale);
                if (ImGui.SmallButton("+ else effect"))
                { block.ElseEffects.Add(EffectEditor.NewEffect(template)); changed = true; }
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.SmallButton("+ Add conditional block"))
        { blocks.Add(new EffectBlock()); changed = true; }

        ImGui.PopID();
        return changed;
    }

    private static bool DrawConditionTable(List<SkillCondition> all, List<SkillCondition> visible, SheetTemplate template, float scale)
    {
        if (visible.Count == 0) return false;
        bool changed = false;
        if (ImGui.BeginTable("##blkcond", 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            EffectEditor.SetupCols(scale);
            for (int i = visible.Count - 1; i >= 0; i--)
            {
                var c = visible[i];
                ImGui.TableNextRow(); ImGui.PushID($"bc{i}");
                if (ConditionEditor.DrawRow(c, template)) changed = true;
                ImGui.TableSetColumnIndex(5);
                if (ImGui.SmallButton("X")) { all.Remove(c); changed = true; }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        return changed;
    }

    private static bool DrawEffectTable(string tableId, List<SkillEffect> fxs, SheetTemplate template,
                                        bool includeBarCurrent, IReadOnlyList<RpSkill>? grantablePassives, float scale)
    {
        if (fxs.Count == 0) return false;
        bool changed = false;
        if (ImGui.BeginTable(tableId, 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            EffectEditor.SetupCols(scale);
            for (int i = fxs.Count - 1; i >= 0; i--)
            {
                ImGui.TableNextRow(); ImGui.PushID($"be{i}");
                if (EffectEditor.DrawRow(fxs[i], template, includeBarCurrent, grantablePassives)) changed = true;
                ImGui.TableSetColumnIndex(5);
                if (ImGui.SmallButton("X")) { fxs.RemoveAt(i); changed = true; }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        return changed;
    }
}
