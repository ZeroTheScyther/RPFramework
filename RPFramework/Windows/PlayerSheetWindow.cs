using System;
using System.Linq;
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
    private readonly Plugin _plugin;
    private CharacterProfileDto _profile;
    private readonly Action<string> _onClosed;

    public PlayerSheetWindow(Plugin plugin, CharacterProfileDto profile, Action<string> onClosed)
        : base($"{profile.DisplayName} Character Sheet##rpcs_{profile.PlayerId}",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin   = plugin;
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
        var   p        = _profile;
        var   template = _plugin.Configuration.ActiveTemplate;
        float scale    = ImGuiHelpers.GlobalScale;

        using var scroll = ImRaii.Child("##psheetscroll", new Vector2(-1, -1), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!scroll) return;

        foreach (var group in template.Groups)
        {
            DrawGroup(group, p, template, scale);
            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private void DrawGroup(SheetGroup group, CharacterProfileDto p, SheetTemplate template, float scale)
    {
        ImGui.TextUnformatted(group.Name);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        var bars    = group.Fields.Where(f => f.Type == FieldType.Bar).ToList();
        var dots    = group.Fields.Where(f => f.Type == FieldType.Dot).ToList();
        var numbers = group.Fields.Where(f => f.Type == FieldType.Number).ToList();
        var checks  = group.Fields.Where(f => f.Type == FieldType.Checkbox).ToList();

        foreach (var f in bars)
        {
            DrawBarField(f, p, template, scale);
            ImGui.Spacing();
        }

        if (bars.Any(bf => bf.IsApBar))
        {
            var apField = bars.FirstOrDefault(bf => bf.IsApBar);
            if (apField != null)
            {
                p.StatValues.TryGetValue(apField.Id + ":cur", out int apCur);
                p.StatValues.TryGetValue(apField.Id + ":max", out int apMax);
                if (apMax > 0)
                {
                    float pct = (float)apCur / apMax;
                    int pen = pct switch { <= 0.10f => -5, <= 0.20f => -4, <= 0.30f => -2, <= 0.40f => -1, _ => 0 };
                    if (pen < 0)
                        ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), $"Exhausted: {pen} to all stat rolls");
                }
            }
        }

        foreach (var f in dots)
        {
            DrawDotField(f, p, scale);
            ImGui.Spacing();
        }

        if (numbers.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0) ImGui.Spacing();
            if (ImGui.BeginTable($"##pnums_{group.Id}", 6, ImGuiTableFlags.None))
            {
                ImGui.TableSetupColumn("##sl1", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
                ImGui.TableSetupColumn("##sv1", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("##sm1", ImGuiTableColumnFlags.WidthFixed, 34 * scale);
                ImGui.TableSetupColumn("##sl2", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
                ImGui.TableSetupColumn("##sv2", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("##sm2", ImGuiTableColumnFlags.WidthFixed, 34 * scale);

                for (int i = 0; i < numbers.Count; i += 2)
                    DrawNumberRow(numbers[i], i + 1 < numbers.Count ? numbers[i + 1] : null, p, scale);

                ImGui.EndTable();
            }
        }

        if (checks.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0 || numbers.Count > 0) ImGui.Spacing();
            using var child = ImRaii.Child($"##pchecks_{group.Id}",
                new Vector2(-1, checks.Count <= 12 ? 0 : 150 * scale), false);
            foreach (var f in checks)
            {
                p.CheckValues.TryGetValue(f.Id, out bool val);
                bool v = val;
                using var _ = ImRaii.Disabled();
                ImGui.Checkbox($"{f.Name}##pck_{f.Id}", ref v);
                MaybeTooltip(f);
            }
        }
    }

    private static void DrawDotField(SheetField f, CharacterProfileDto p, float scale)
    {
        p.StatValues.TryGetValue(f.Id + ":cur", out int cur);
        int dotMax = Math.Max(1, f.Max);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();

        var  drawList = ImGui.GetWindowDrawList();
        float r       = 6f * scale;
        float gap     = 3f * scale;

        // Vertically center the dots with the text line
        float dotH    = 2f * r;
        float baseY   = ImGui.GetCursorPosY();
        float yOffset = MathF.Max(0f, (ImGui.GetTextLineHeight() - dotH) * 0.5f);

        for (int i = 0; i < dotMax; i++)
        {
            if (i > 0) ImGui.SameLine(0f, gap);
            ImGui.SetCursorPosY(baseY + yOffset);
            var startPos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(2f * r, 2f * r));

            bool filled   = i < cur;
            var  center   = new Vector2(startPos.X + r, startPos.Y + r);
            var  fillColor = filled
                ? new Vector4(0.20f, 0.72f, 0.20f, 1f)
                : new Vector4(0.28f, 0.28f, 0.28f, 0.90f);

            drawList.AddCircleFilled(center, r - 1f, ImGui.ColorConvertFloat4ToU32(fillColor));
            drawList.AddCircle(center, r - 1f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.55f, 0.55f, 0.65f)),
                0, 1.2f * scale);
        }
    }

    private static void MaybeTooltip(SheetField f)
    {
        if (string.IsNullOrWhiteSpace(f.Tooltip) || !ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(320f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(f.Tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static void DrawBarField(SheetField f, CharacterProfileDto p, SheetTemplate template, float scale)
    {
        p.StatValues.TryGetValue(f.Id + ":cur", out int cur);
        p.StatValues.TryGetValue(f.Id + ":max", out int max);

        int    bonus      = 0;
        string bonusLabel = "";
        if (f.BonusSourceFieldId != null)
        {
            var bonusField = template.FindField(f.BonusSourceFieldId);
            if (bonusField != null && p.StatValues.TryGetValue(f.BonusSourceFieldId, out int bonusSrc))
            {
                bonus      = SkillHelpers.StatMod(bonusSrc);
                bonusLabel = bonusField.Name;
            }
        }

        int   effectiveMax = max + bonus;
        float fraction     = effectiveMax > 0 ? Math.Clamp((float)cur / effectiveMax, 0f, 1f) : 0f;
        var   color        = f.IsHpBar  ? new Vector4(0.20f, 0.70f, 0.20f, 1f)
                           : f.IsApBar ? new Vector4(0.20f, 0.50f, 0.90f, 1f)
                                       : new Vector4(0.60f, 0.40f, 0.80f, 1f);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, color))
            ImGui.ProgressBar(fraction, new Vector2(-1, 14 * scale), "");

        ImGui.TextDisabled($"{cur} / {effectiveMax}");
        if (bonus != 0 && bonusLabel.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(bonus > 0 ? $"(+{bonus} {bonusLabel})" : $"({bonus} {bonusLabel})");
        }
    }

    private static void DrawNumberRow(SheetField f1, SheetField? f2, CharacterProfileDto p, float scale)
    {
        ImGui.TableNextRow();

        p.StatValues.TryGetValue(f1.Id, out int v1);
        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(f1.Name); MaybeTooltip(f1);
        ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{v1}");
        if (f1.ShowModifier)
        {
            int m = SkillHelpers.StatMod(v1);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextDisabled(m >= 0 ? $"+{m}" : $"{m}");
        }

        if (f2 == null) return;

        p.StatValues.TryGetValue(f2.Id, out int v2);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(f2.Name); MaybeTooltip(f2);
        ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted($"{v2}");
        if (f2.ShowModifier)
        {
            int m = SkillHelpers.StatMod(v2);
            ImGui.TableSetColumnIndex(5);
            ImGui.TextDisabled(m >= 0 ? $"+{m}" : $"{m}");
        }
    }
}
