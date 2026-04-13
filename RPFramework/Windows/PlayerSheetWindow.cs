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
/// Read-only character sheet for a remote player, populated from the relay server.
/// </summary>
public class PlayerSheetWindow : Window, IDisposable
{
    private CharacterProfileDto _profile;
    private readonly Action<string> _onClosed;

    private static readonly string[] Specializations =
    [
        "Acrobatics", "Animal Handling", "Thaumaturgy", "Arcanima", "Conjury",
        "History", "Insight", "Aetherology", "Intimidation", "Investigation",
        "Medicine", "Herbalism", "Perception", "Performance", "Persuasion",
        "Religion", "Sleight of Hand", "Bartering", "Stealth", "Deception",
        "Streetwise", "Hobnobbing", "Survival",
    ];

    public PlayerSheetWindow(CharacterProfileDto profile, Action<string> onClosed)
        : base($"{profile.DisplayName} Character Sheet##rpcs_{profile.PlayerId}",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _profile  = profile;
        _onClosed = onClosed;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 480),
            MaximumSize = new Vector2(600, 900),
        };
        Size          = new Vector2(380, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void UpdateProfile(CharacterProfileDto profile) => _profile = profile;
    public override void OnClose() => _onClosed(_profile.PlayerId);
    public void Dispose() { }

    public override void Draw()
    {
        var   p     = _profile;
        float scale = ImGuiHelpers.GlobalScale;

        // ── HP / AP bars ──────────────────────────────────────────────────────
        int hpBonus = SkillHelpers.StatMod(p.Con);
        DrawPool("HP", p.HpCurrent, p.HpMax,
                 new Vector4(0.20f, 0.70f, 0.20f, 1f), scale, hpBonus);
        ImGui.Spacing();
        DrawPool("AP", p.ApCurrent, p.ApMax,
                 new Vector4(0.20f, 0.50f, 0.90f, 1f), scale);

        // AP exhaustion indicator
        if (p.ApMax > 0)
        {
            float pct = (float)p.ApCurrent / p.ApMax;
            int pen = pct switch
            {
                <= 0.10f => -5,
                <= 0.20f => -4,
                <= 0.30f => -2,
                <= 0.40f => -1,
                _        =>  0,
            };
            if (pen < 0)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f),
                                  $"Exhausted: {pen} to all stat rolls");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Stat grid ─────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Stats");

        if (ImGui.BeginTable("##rpviewstattbl", 6, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("##sl1", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
            ImGui.TableSetupColumn("##sv1", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
            ImGui.TableSetupColumn("##sm1", ImGuiTableColumnFlags.WidthFixed, 34 * scale);
            ImGui.TableSetupColumn("##sl2", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
            ImGui.TableSetupColumn("##sv2", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
            ImGui.TableSetupColumn("##sm2", ImGuiTableColumnFlags.WidthFixed, 34 * scale);

            DrawStatRow("STR", p.Str, "DEX", p.Dex, scale);
            DrawStatRow("SPD", p.Spd, "CON", p.Con, scale);
            DrawStatRow("MEM", p.Mem, "MTL", p.Mtl, scale);
            DrawStatRow("INT", p.Int, "CHA", p.Cha, scale);

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Specializations ───────────────────────────────────────────────────
        ImGui.TextUnformatted("Specializations");

        using var child = ImRaii.Child("##rpviewspecs", new Vector2(-1, -1));
        if (!child) return;

        foreach (var spec in Specializations)
        {
            p.Proficiencies.TryGetValue(spec, out bool proficient);
            bool v = proficient;
            using var _ = ImRaii.Disabled();
            ImGui.Checkbox($"{spec}##rpviewspec_{spec}", ref v);
        }
    }

    private static void DrawPool(string label, int current, int max, Vector4 color, float scale, int bonus = 0)
    {
        int   effectiveMax = max + bonus;
        float fraction     = effectiveMax > 0
            ? Math.Clamp((float)current / effectiveMax, 0f, 1f)
            : 0f;

        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, color))
            ImGui.ProgressBar(fraction, new Vector2(-1, 14 * scale), "");

        ImGui.TextDisabled($"{current} / {effectiveMax}");
        if (bonus != 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(bonus > 0 ? $"(+{bonus} CON)" : $"({bonus} CON)");
        }
    }

    private static void DrawStatRow(string lbl1, int v1, string lbl2, int v2, float scale)
    {
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(lbl1);
        ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{v1}");
        ImGui.TableSetColumnIndex(2);
        int m1 = SkillHelpers.StatMod(v1);
        ImGui.TextDisabled(m1 >= 0 ? $"+{m1}" : $"{m1}");

        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(lbl2);
        ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted($"{v2}");
        ImGui.TableSetColumnIndex(5);
        int m2 = SkillHelpers.StatMod(v2);
        ImGui.TextDisabled(m2 >= 0 ? $"+{m2}" : $"{m2}");
    }
}
