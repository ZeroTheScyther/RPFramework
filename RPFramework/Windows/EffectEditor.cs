using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Shared editor for a single <see cref="SkillEffect"/> row, used by the skill editor and the
/// item-effects editor. A Category dropdown (Stats / Specializations / Passives) groups the targets
/// and swaps the rest of the row: Stats get the full op set (+ - = x /) plus a percentage toggle;
/// Specializations collapse to a Set-only grant toggle; Passives (skills only) grant one of the
/// character's own passives for N turns. Text fields are never effect targets.
/// </summary>
public static class EffectEditor
{
    private static readonly string[] OpNames = { "+", "-", "=", "x", "/" };

    public enum Category { Stat, Specialization, Passive }

    // ── Target field lists ────────────────────────────────────────────────────

    /// <summary>Numeric stat targets: Number + Dot fields and each Bar split into current ("HP") and max
    /// ("MaxHP"). <paramref name="includeBarCurrent"/> is false for equipment (always-on effects only raise
    /// the cap; a permanent change to a live current value is meaningless).</summary>
    public static List<SheetField> StatFields(SheetTemplate template, bool includeBarCurrent = true)
    {
        var list = new List<SheetField>();
        foreach (var f in template.Groups.SelectMany(g => g.Fields))
        {
            switch (f.Type)
            {
                case FieldType.Bar:
                    if (includeBarCurrent) list.Add(f);
                    list.Add(MaxTarget(f));
                    break;
                case FieldType.Number:
                case FieldType.Dot:
                    list.Add(f);
                    break;
            }
        }
        return list;
    }

    /// <summary>Specialization targets: the template's Checkbox (proficiency) fields.</summary>
    public static List<SheetField> SpecFields(SheetTemplate template)
        => template.Groups.SelectMany(g => g.Fields).Where(f => f.Type == FieldType.Checkbox).ToList();

    /// <summary>Every target an effect may address (Stats + Specializations) — for callers that still want
    /// one flat list (e.g. summaries / field-name lookups).</summary>
    public static List<SheetField> TargetFields(SheetTemplate template, bool includeBarCurrent = true)
    {
        var list = StatFields(template, includeBarCurrent);
        list.AddRange(SpecFields(template));
        return list;
    }

    private static SheetField MaxTarget(SheetField bar) => new()
    {
        Id   = bar.Id + StatMath.MaxSuffix,
        Name = "Max" + bar.Name,
        Type = FieldType.Bar,
    };

    /// <summary>A fresh effect targeting the first available stat field (used by "+ Add effect").</summary>
    public static SkillEffect NewEffect(SheetTemplate template)
        => new() { FieldId = StatFields(template).FirstOrDefault()?.Id ?? "" };

    // ── Column layout shared by every effect/condition table ───────────────────

    /// <summary>Sets up the 6 columns (Category / Field / Op / Value / % / delete) used by all
    /// effect and condition tables so they line up. The caller fills the delete column.</summary>
    public static void SetupCols(float scale)
    {
        ImGui.TableSetupColumn("Type",  ImGuiTableColumnFlags.WidthFixed, 74 * scale);
        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 96 * scale);
        ImGui.TableSetupColumn("Op",    ImGuiTableColumnFlags.WidthFixed, 52 * scale);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 60 * scale);
        ImGui.TableSetupColumn("%",     ImGuiTableColumnFlags.WidthFixed, 24 * scale);
        ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 22 * scale);
        ImGui.TableHeadersRow();
    }

    // ── Row ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the Category / Field / Op / Value / % cells (columns 0-4) for one effect row. The caller owns
    /// the table, the per-row <c>PushID</c>, <c>TableNextRow</c>, and the trailing delete column. Pass a
    /// non-null <paramref name="grantablePassives"/> to offer the Passives (grant) category. Returns true
    /// if the effect changed this frame.
    /// </summary>
    public static bool DrawRow(SkillEffect fx, SheetTemplate template, bool includeBarCurrent = true,
                               IReadOnlyList<RpSkill>? grantablePassives = null)
    {
        bool changed = false;
        var  stats   = StatFields(template, includeBarCurrent);
        var  specs   = SpecFields(template);
        bool allowPassive = grantablePassives is { Count: > 0 };

        var cat = CategoryOf(fx, specs);

        // Column 0 — Category.
        ImGui.TableSetColumnIndex(0); ImGui.SetNextItemWidth(-1);
        var catNames = allowPassive ? new[] { "Stat", "Spec.", "Passive" } : new[] { "Stat", "Spec." };
        int ci = (int)cat;
        if (ci >= catNames.Length) ci = 0;
        if (ImGui.Combo("##ecat", ref ci, catNames, catNames.Length))
        {
            var newCat = (Category)ci;
            if (newCat != cat) { SetCategory(fx, newCat, stats, specs, grantablePassives); changed = true; cat = newCat; }
        }

        switch (cat)
        {
            case Category.Passive: return DrawPassiveGrant(fx, grantablePassives!) || changed;
            case Category.Specialization: return DrawSpecGrant(fx, specs) || changed;
            default: return DrawStatOp(fx, stats) || changed;
        }
    }

    private static Category CategoryOf(SkillEffect fx, List<SheetField> specs)
    {
        if (fx.GrantPassiveId != null) return Category.Passive;
        if (specs.Any(f => f.Id == fx.FieldId)) return Category.Specialization;
        return Category.Stat;
    }

    private static void SetCategory(SkillEffect fx, Category cat, List<SheetField> stats, List<SheetField> specs,
                                    IReadOnlyList<RpSkill>? passives)
    {
        fx.GrantPassiveId = null;
        switch (cat)
        {
            case Category.Stat:
                fx.FieldId = stats.FirstOrDefault()?.Id ?? "";
                fx.Op = EffectOp.Add; fx.IsPercentage = false; fx.Value = 1f;
                break;
            case Category.Specialization:
                fx.FieldId = specs.FirstOrDefault()?.Id ?? "";
                fx.Op = EffectOp.Set; fx.IsPercentage = false; fx.Value = 1f;
                break;
            case Category.Passive:
                fx.FieldId = "";
                fx.GrantPassiveId = passives?.FirstOrDefault()?.Id ?? System.Guid.Empty;
                fx.Op = EffectOp.Add; fx.IsPercentage = false; fx.Value = 1f;
                break;
        }
    }

    private static bool DrawStatOp(SkillEffect fx, List<SheetField> stats)
    {
        bool changed = false;
        var names = stats.Select(f => f.Name).ToArray();
        ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(-1);
        int fi = System.Math.Max(0, stats.FindIndex(f => f.Id == fx.FieldId));
        if (names.Length > 0 && ImGui.Combo("##efld", ref fi, names, names.Length)) { fx.FieldId = stats[fi].Id; changed = true; }

        ImGui.TableSetColumnIndex(2); ImGui.SetNextItemWidth(-1);
        int opI = (int)fx.Op;
        if (ImGui.Combo("##eop", ref opI, OpNames, OpNames.Length)) { fx.Op = (EffectOp)opI; changed = true; }
        ImGui.TableSetColumnIndex(3); ImGui.SetNextItemWidth(-1);
        float v = fx.Value;
        if (ImGui.InputFloat("##eval", ref v, 0f, 0f, "%.1f")) { fx.Value = v; changed = true; }
        ImGui.TableSetColumnIndex(4);
        bool p = fx.IsPercentage;
        if (ImGui.Checkbox("##epct", ref p)) { fx.IsPercentage = p; changed = true; }
        return changed;
    }

    private static bool DrawSpecGrant(SkillEffect fx, List<SheetField> specs)
    {
        bool changed = false;
        if (fx.Op != EffectOp.Set) { fx.Op = EffectOp.Set; changed = true; }
        if (fx.IsPercentage)       { fx.IsPercentage = false; changed = true; }

        var names = specs.Select(f => f.Name).ToArray();
        ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(-1);
        int fi = System.Math.Max(0, specs.FindIndex(f => f.Id == fx.FieldId));
        if (names.Length > 0 && ImGui.Combo("##espec", ref fi, names, names.Length)) { fx.FieldId = specs[fi].Id; changed = true; }

        ImGui.TableSetColumnIndex(2); ImGui.TextDisabled("grant");
        ImGui.TableSetColumnIndex(3);
        bool grant = fx.Value >= 1f;
        if (ImGui.Checkbox("##egrant", ref grant)) { fx.Value = grant ? 1f : 0f; changed = true; }
        ImGui.TableSetColumnIndex(4);
        return changed;
    }

    private static bool DrawPassiveGrant(SkillEffect fx, IReadOnlyList<RpSkill> passives)
    {
        bool changed = false;
        var names = passives.Select(p => p.Name).ToArray();
        int idx = System.Math.Max(0, IndexOfId(passives, fx.GrantPassiveId));
        ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(-1);
        if (names.Length > 0 && ImGui.Combo("##egp", ref idx, names, names.Length)) { fx.GrantPassiveId = passives[idx].Id; changed = true; }

        ImGui.TableSetColumnIndex(2); ImGui.TextDisabled("for");
        ImGui.TableSetColumnIndex(3); ImGui.SetNextItemWidth(-1);
        int turns = (int)fx.Value;
        if (ImGui.InputInt("##egpturns", ref turns, 0, 0)) { fx.Value = System.Math.Max(1, turns); changed = true; }
        ImGui.TableSetColumnIndex(4); ImGui.TextDisabled("t");
        return changed;
    }

    private static int IndexOfId(IReadOnlyList<RpSkill> passives, System.Guid? id)
    {
        for (int i = 0; i < passives.Count; i++) if (passives[i].Id == id) return i;
        return 0;
    }
}
