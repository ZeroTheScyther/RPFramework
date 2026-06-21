using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Read-only character sheet for a remote player. Reads straight from the store — the
/// server hydrates every campaign member's character, so no fetch is needed.
/// </summary>
public class PlayerSheetWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _playerId;
    private readonly string? _code;
    private readonly Action<string> _onClosed;

    public PlayerSheetWindow(Plugin plugin, string playerId, Action<string> onClosed)
        : base($"Character Sheet##rpcs_{playerId}",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin   = plugin;
        _playerId = playerId;
        _code     = plugin.ActiveCampaign;
        _onClosed = onClosed;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 480),
            MaximumSize = new Vector2(600, 900),
        };
        Size          = new Vector2(380, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnClose() => _onClosed(_playerId);
    public void Dispose() { }

    public override void Draw()
    {
        var ch = _code != null ? _plugin.Store.Character(_code, _playerId) : null;
        if (ch == null)
        {
            ImGui.TextDisabled("This player is not in your active campaign.");
            return;
        }

        var   st       = ch.State;
        var   template = _plugin.Store.TemplateOrDefault(_code);
        float scale    = ImGuiHelpers.GlobalScale;

        ImGui.TextUnformatted(ch.DisplayName);
        ImGui.Separator();

        using var scroll = ImRaii.Child("##psheetscroll", new Vector2(-1, -1), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!scroll) return;

        foreach (var group in template.Groups)
        {
            DrawGroup(group, st, template, scale);
            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private void DrawGroup(SheetGroup group, CharacterState p, SheetTemplate template, float scale)
    {
        ImGui.TextUnformatted(group.Name);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        var bars    = group.Fields.Where(f => f.Type == FieldType.Bar).ToList();
        var dots    = group.Fields.Where(f => f.Type == FieldType.Dot).ToList();
        var numbers = group.Fields.Where(f => f.Type == FieldType.Number).ToList();
        var checks  = group.Fields.Where(f => f.Type == FieldType.Checkbox).ToList();
        var texts   = group.Fields.Where(f => f.Type == FieldType.Text).ToList();

        foreach (var f in bars)
        {
            DrawBarField(f, p, template, scale);
            ImGui.Spacing();
        }

        var apField = bars.FirstOrDefault(bf => bf.IsApBar);
        if (apField != null)
        {
            int pen = StatMath.ApPenalty(p, template);
            if (pen < 0)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), $"Exhausted: {pen} to all stat rolls");
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
                ImGui.TableSetupColumn("##sm1", ImGuiTableColumnFlags.WidthFixed, 58 * scale);
                ImGui.TableSetupColumn("##sl2", ImGuiTableColumnFlags.WidthFixed, 36 * scale);
                ImGui.TableSetupColumn("##sv2", ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("##sm2", ImGuiTableColumnFlags.WidthFixed, 58 * scale);

                for (int i = 0; i < numbers.Count; i += 2)
                    DrawNumberRow(numbers[i], i + 1 < numbers.Count ? numbers[i + 1] : null, p, template, scale);

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
                p.CheckValues.TryGetValue(f.Id, out bool baseVal);
                var  sources    = StatMath.CheckSources(p, f.Id, template);
                bool eff        = sources.Count > 0 ? sources[^1].Grant : baseVal;
                bool overridden = sources.Count > 0 && eff != baseVal;
                bool v = eff;
                using var _d = ImRaii.Disabled();
                using (ImRaii.PushColor(ImGuiCol.Text, CharacterSheetWindow.StatModified, overridden))
                    ImGui.Checkbox($"{f.Name}##pck_{f.Id}", ref v);
                if (sources.Count > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    CharacterSheetWindow.DrawCheckBreakdown(baseVal, sources);
                else MaybeTooltip(f);
            }
        }

        if (texts.Count > 0)
        {
            if (bars.Count > 0 || dots.Count > 0 || numbers.Count > 0 || checks.Count > 0) ImGui.Spacing();
            foreach (var f in texts)
            {
                p.TextValues.TryGetValue(f.Id, out string? val);
                ImGui.TextUnformatted(f.Name); MaybeTooltip(f);
                if (string.IsNullOrWhiteSpace(val)) { ImGui.TextDisabled("(empty)"); continue; }
                ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
                ImGui.TextUnformatted(val);
                ImGui.PopTextWrapPos();
            }
        }
    }

    private static void DrawDotField(SheetField f, CharacterState p, float scale)
    {
        p.StatValues.TryGetValue(f.Id + ":cur", out int cur);
        int dotMax = Math.Max(1, f.Max);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();

        var  drawList = ImGui.GetWindowDrawList();
        float r       = 6f * scale;
        float gap     = 3f * scale;

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

    private static void DrawBarField(SheetField f, CharacterState p, SheetTemplate template, float scale)
    {
        p.StatValues.TryGetValue(f.Id + ":cur", out int cur);

        int   effectiveMax = StatMath.EffectiveBarMax(p, f, template); // stored max + stat bonus + equipped gear
        float fraction     = effectiveMax > 0 ? Math.Clamp((float)cur / effectiveMax, 0f, 1f) : 0f;
        var   color        = f.IsHpBar  ? new Vector4(0.20f, 0.70f, 0.20f, 1f)
                           : f.IsApBar ? new Vector4(0.20f, 0.50f, 0.90f, 1f)
                                       : new Vector4(0.60f, 0.40f, 0.80f, 1f);

        ImGui.TextUnformatted(f.Name);
        MaybeTooltip(f);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, color))
            ImGui.ProgressBar(fraction, new Vector2(-1, 14 * scale), "");

        p.StatValues.TryGetValue(f.Id + ":max", out int storedMax);
        bool lifted = effectiveMax != storedMax;
        ImGui.TextDisabled($"{cur} / "); ImGui.SameLine(0f, 0f);
        if (lifted) ImGui.TextColored(CharacterSheetWindow.StatModified, $"{effectiveMax}");
        else        ImGui.TextDisabled($"{effectiveMax}");
        if (lifted && ImGui.IsItemHovered()) CharacterSheetWindow.DrawBarMaxBreakdown(p, f, template);
    }

    private static void DrawNumberRow(SheetField f1, SheetField? f2, CharacterState p, SheetTemplate template, float scale)
    {
        ImGui.TableNextRow();

        p.StatValues.TryGetValue(f1.Id, out int v1);
        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(f1.Name); MaybeTooltip(f1);
        ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{v1}");
        if (ImGui.IsItemHovered()) CharacterSheetWindow.DrawStatBreakdown(p, f1, v1, template);
        if (f1.ShowModifier) { ImGui.TableSetColumnIndex(2); CharacterSheetWindow.DrawModifier(p, f1, v1, template); }

        if (f2 == null) return;

        p.StatValues.TryGetValue(f2.Id, out int v2);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(f2.Name); MaybeTooltip(f2);
        ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted($"{v2}");
        if (ImGui.IsItemHovered()) CharacterSheetWindow.DrawStatBreakdown(p, f2, v2, template);
        if (f2.ShowModifier) { ImGui.TableSetColumnIndex(5); CharacterSheetWindow.DrawModifier(p, f2, v2, template); }
    }
}
