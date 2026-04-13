using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models;

namespace RPFramework.Windows;

public class CharacterSheetWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private static readonly string[] Specializations =
    [
        "Acrobatics", "Animal Handling", "Thaumaturgy", "Arcanima", "Conjury",
        "History", "Insight", "Aetherology", "Intimidation", "Investigation",
        "Medicine", "Herbalism", "Perception", "Performance", "Persuasion",
        "Religion", "Sleight of Hand", "Bartering", "Stealth", "Deception",
        "Streetwise", "Hobnobbing", "Survival",
    ];

    public CharacterSheetWindow(Plugin plugin)
        : base("RP Character Sheet##RPFramework.CharSheet",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 480),
            MaximumSize = new Vector2(600, 900),
        };
        Size          = new Vector2(380, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        string? pid = plugin.LocalPlayerId;
        if (pid == null)
        {
            ImGui.TextDisabled("Log in to view your character sheet.");
            return;
        }

        var ch = plugin.GetOrCreateCharacter(pid);
        float scale = ImGuiHelpers.GlobalScale;
        bool dirty  = false;

        // Copy properties to locals (properties can't be passed as ref)
        int hpCur = ch.HpCurrent, hpMax = ch.HpMax;
        int apCur = ch.ApCurrent, apMax = ch.ApMax;
        int str   = ch.Str,  dex = ch.Dex;
        int spd   = ch.Spd,  con = ch.Con;
        int mem   = ch.Mem,  mtl = ch.Mtl;
        int intS  = ch.Int,  cha = ch.Cha;

        // ── HP / AP bars ──────────────────────────────────────────────────────
        DrawPool("HP", ref hpCur, ref hpMax,
                 new Vector4(0.20f, 0.70f, 0.20f, 1f), scale, ref dirty,
                 bonus: SkillHelpers.StatMod(ch.Con));
        ImGui.Spacing();
        DrawPool("AP", ref apCur, ref apMax,
                 new Vector4(0.20f, 0.50f, 0.90f, 1f), scale, ref dirty);

        // AP exhaustion indicator
        int apPen = SkillHelpers.ApPenalty(ch);
        if (apPen < 0)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f),
                              $"Exhausted: {apPen} to all stat rolls");

        ImGui.Spacing();
        ImGui.Separator();

        // ── Stat grid ─────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Stats");

        if (ImGui.BeginTable("##rpstattbl", 6, ImGuiTableFlags.None))
        {
            // 6 columns: label, input, mod, label, input, mod
            ImGui.TableSetupColumn("##sl1", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
            ImGui.TableSetupColumn("##sv1", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
            ImGui.TableSetupColumn("##sm1", ImGuiTableColumnFlags.WidthFixed, 34 * scale);
            ImGui.TableSetupColumn("##sl2", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
            ImGui.TableSetupColumn("##sv2", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
            ImGui.TableSetupColumn("##sm2", ImGuiTableColumnFlags.WidthFixed, 34 * scale);

            DrawStatRow("STR", ref str, "DEX", ref dex, scale, ref dirty);
            DrawStatRow("SPD", ref spd, "CON", ref con, scale, ref dirty);
            DrawStatRow("MEM", ref mem, "MTL", ref mtl, scale, ref dirty);
            DrawStatRow("INT", ref intS, "CHA", ref cha, scale, ref dirty);

            ImGui.EndTable();
        }

        // Write locals back to properties
        ch.HpCurrent = hpCur; ch.HpMax = hpMax;
        ch.ApCurrent = apCur; ch.ApMax = apMax;
        ch.Str = str;  ch.Dex = dex;
        ch.Spd = spd;  ch.Con = con;
        ch.Mem = mem;  ch.Mtl = mtl;
        ch.Int = intS; ch.Cha = cha;

        ImGui.Spacing();
        ImGui.Separator();

        // ── Specializations ───────────────────────────────────────────────────
        ImGui.TextUnformatted("Specializations");

        using var child = ImRaii.Child("##rpspecs", new Vector2(-1, -1));
        if (!child) goto save;

        foreach (var skill in Specializations)
        {
            ch.Proficiencies.TryGetValue(skill, out bool proficient);
            bool v = proficient;
            if (ImGui.Checkbox($"{skill}##rpspec_{skill}", ref v))
            {
                ch.Proficiencies[skill] = v;
                dirty = true;
            }
        }

        save:
        if (dirty)
        {
            plugin.Configuration.Save();
            plugin.PushLocalProfile();
        }
    }

    private static void DrawPool(
        string label, ref int current, ref int max,
        Vector4 color, float scale, ref bool dirty, int bonus = 0)
    {
        int effectiveMax = max + bonus;
        float fraction   = effectiveMax > 0
            ? Math.Clamp((float)current / effectiveMax, 0f, 1f)
            : 0f;

        ImGui.TextUnformatted(label);
        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, color))
            ImGui.ProgressBar(fraction, new Vector2(-1, 14 * scale), "");

        float fieldW = 60 * scale;
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputInt($"##rp{label}cur", ref current, 0, 0))
        {
            current = Math.Clamp(current, 0, effectiveMax);
            dirty = true;
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("/");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputInt($"##rp{label}max", ref max, 0, 0))
        {
            max     = Math.Clamp(max,     0,       9999);
            current = Math.Clamp(current, 0,       max + bonus);
            dirty = true;
        }
        if (bonus != 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(bonus > 0 ? $"(+{bonus} CON)" : $"({bonus} CON)");
        }
    }

    private static void DrawStatRow(
        string lbl1, ref int v1,
        string lbl2, ref int v2,
        float scale, ref bool dirty)
    {
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(lbl1);

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(48 * scale);
        if (ImGui.InputInt($"##rp{lbl1}", ref v1, 0, 0))
        {
            v1    = Math.Clamp(v1, 8, 20);
            dirty = true;
        }

        ImGui.TableSetColumnIndex(2);
        int m1 = SkillHelpers.StatMod(v1);
        ImGui.TextDisabled(m1 >= 0 ? $"+{m1}" : $"{m1}");

        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted(lbl2);

        ImGui.TableSetColumnIndex(4);
        ImGui.SetNextItemWidth(48 * scale);
        if (ImGui.InputInt($"##rp{lbl2}", ref v2, 0, 0))
        {
            v2    = Math.Clamp(v2, 8, 20);
            dirty = true;
        }

        ImGui.TableSetColumnIndex(5);
        int m2 = SkillHelpers.StatMod(v2);
        ImGui.TextDisabled(m2 >= 0 ? $"+{m2}" : $"{m2}");
    }
}
