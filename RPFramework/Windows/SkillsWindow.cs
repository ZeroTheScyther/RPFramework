using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models;

namespace RPFramework.Windows;

public class SkillsWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private int  _selectedIdx = -1;
    private bool _dirty       = false;

    private static readonly string[] CondOpNames   = { "<", "≤", "=", "≥", ">" };
    private static readonly string[] EffectOpNames = { "+", "−", "=" };

    public SkillsWindow(Plugin plugin)
        : base("RP Skills & Passives##RPFramework.Skills",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 400),
            MaximumSize = new Vector2(900, 800),
        };
        Size          = new Vector2(600, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        string? pid = plugin.LocalPlayerId;
        if (pid == null)
        {
            ImGui.TextDisabled("Log in to manage skills.");
            return;
        }

        var ch    = plugin.GetOrCreateCharacter(pid);
        float scale = ImGuiHelpers.GlobalScale;

        if (_selectedIdx >= ch.Skills.Count) _selectedIdx = ch.Skills.Count - 1;

        // ── Left pane ──────────────────────────────────────────────────────────
        float leftW = 170 * scale;

        using (var leftChild = ImRaii.Child("##skilllist", new Vector2(leftW, -1), false))
        {
            if (leftChild)
            {
                ImGui.Spacing();

                int deleteAt = -1;

                for (int i = 0; i < ch.Skills.Count; i++)
                {
                    var sk    = ch.Skills[i];
                    string badge = sk.Type == SkillType.Active ? "[A]" : "[P]";
                    string tag   = sk.DurationRemaining > 0
                                 ? $" [{sk.DurationRemaining} turn{(sk.DurationRemaining == 1 ? "" : "s")}]"
                                 : sk.CooldownRemaining > 0
                                 ? $" (cd:{sk.CooldownRemaining} turn{(sk.CooldownRemaining == 1 ? "" : "s")})"
                                 : "";
                    string label = $"{badge} {sk.Name}{tag}##rpsk_{i}";

                    bool selected = _selectedIdx == i;
                    if (selected)
                        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.26f, 0.59f, 0.98f, 0.4f));

                    if (ImGui.Selectable(label, selected))
                        _selectedIdx = i;

                    if (selected)
                        ImGui.PopStyleColor();

                    if (ImGui.BeginPopupContextItem($"##rpsk_ctx{i}"))
                    {
                        _selectedIdx = i;
                        if (ImGui.MenuItem("Edit##rpsk_ctx_edit"))
                        { sk.IsLocked = false; plugin.Configuration.Save(); }
                        ImGui.Separator();
                        if (ImGui.MenuItem("Delete##rpsk_ctx_del"))
                            deleteAt = i;
                        ImGui.EndPopup();
                    }
                }

                if (deleteAt >= 0)
                {
                    ch.Skills.RemoveAt(deleteAt);
                    if (_selectedIdx >= ch.Skills.Count)
                        _selectedIdx = ch.Skills.Count - 1;
                    plugin.Configuration.Save();
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.Button("New Skill##rpsk_new", new Vector2(-1, 0)))
                {
                    ch.Skills.Add(new RpSkill { Type = SkillType.Active, IsLocked = false });
                    _selectedIdx = ch.Skills.Count - 1;
                    plugin.Configuration.Save();
                }
            }
        }

        ImGui.SameLine();

        using var rightChild = ImRaii.Child("##skilleditor", new Vector2(-1, -1), false);
        if (!rightChild) return;

        if (_selectedIdx < 0 || _selectedIdx >= ch.Skills.Count)
        {
            ImGui.TextDisabled("Select a skill or create a new one.");
            return;
        }

        var skill    = ch.Skills[_selectedIdx];
        var template = plugin.Configuration.ActiveTemplate;
        _dirty = false;

        if (skill.IsLocked)
            DrawLockedView(skill, ch, template, scale);
        else
            DrawEditorView(skill, ch, template, scale);

        if (_dirty)
            plugin.Configuration.Save();
    }

    // ── Editor (unlocked) ─────────────────────────────────────────────────────

    private void DrawEditorView(RpSkill skill, RpCharacter ch, SheetTemplate template, float scale)
    {
        // Build field lists for dropdowns
        var allFields  = template.Groups.SelectMany(g => g.Fields).ToList();
        var fieldNames = allFields.Select(f => f.Name).ToArray();

        // Name
        ImGui.TextUnformatted("Name");
        ImGui.SetNextItemWidth(-1);
        string name = skill.Name;
        if (ImGui.InputText("##rpsk_name", ref name, 64))
        { skill.Name = name; _dirty = true; }

        // Description
        ImGui.Spacing();
        ImGui.TextUnformatted("Description");
        ImGui.SetNextItemWidth(-1);
        string desc = skill.Description;
        if (ImGui.InputTextMultiline("##rpsk_desc", ref desc, 512, new Vector2(-1, 54 * scale)))
        { skill.Description = desc; _dirty = true; }

        // Type
        ImGui.Spacing();
        ImGui.TextUnformatted("Type");
        int typeVal = (int)skill.Type;
        if (ImGui.RadioButton("Active##rpsk_ta",  ref typeVal, 0)) { skill.Type = SkillType.Active;  _dirty = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Passive##rpsk_tp", ref typeVal, 1)) { skill.Type = SkillType.Passive; _dirty = true; }

        if (skill.Type == SkillType.Passive)
        {
            ImGui.Spacing();
            bool tot = skill.TriggerOnTurnEnd;
            if (ImGui.Checkbox("Trigger on Turn End##rpsk_tote", ref tot))
            { skill.TriggerOnTurnEnd = tot; _dirty = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Effects are applied each time you press End Turn in the Initiative window.");
        }

        // Cooldown
        ImGui.Spacing();
        ImGui.TextUnformatted("Cooldown (turns)");
        ImGui.SetNextItemWidth(80 * scale);
        int cd = skill.Cooldown;
        if (ImGui.InputInt("##rpsk_cd", ref cd, 1, 1))
        { skill.Cooldown = Math.Max(0, cd); _dirty = true; }

        // Duration
        ImGui.Spacing();
        ImGui.TextUnformatted("Duration (turns)");
        ImGui.SetNextItemWidth(80 * scale);
        int dur = skill.Duration;
        if (ImGui.InputInt("##rpsk_dur", ref dur, 1, 1))
        { skill.Duration = Math.Max(0, dur); _dirty = true; }
        ImGui.SameLine();
        ImGui.TextDisabled("0 = instant / permanent");

        ImGui.Spacing();
        ImGui.Separator();

        // Conditions (passive only)
        if (skill.Type == SkillType.Passive)
        {
            ImGui.TextUnformatted("Conditions  (auto-triggers when ALL are true)");

            if (ImGui.BeginTable("##rpsk_conds", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 80 * scale);
                ImGui.TableSetupColumn("Op",    ImGuiTableColumnFlags.WidthFixed, 52 * scale);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 60 * scale);
                ImGui.TableSetupColumn("%",     ImGuiTableColumnFlags.WidthFixed, 24 * scale);
                ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 22 * scale);
                ImGui.TableHeadersRow();

                for (int i = skill.Conditions.Count - 1; i >= 0; i--)
                {
                    var c = skill.Conditions[i];
                    ImGui.TableNextRow();
                    ImGui.PushID($"##cond{i}");

                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetNextItemWidth(76 * scale);
                    int statI = allFields.FindIndex(f => f.Id == SkillHelpers.EffectiveFieldId(c));
                    if (statI < 0) statI = 0;
                    if (ImGui.Combo("##cs", ref statI, fieldNames, fieldNames.Length))
                    { c.FieldId = allFields[statI].Id; _dirty = true; }

                    ImGui.TableSetColumnIndex(1);
                    ImGui.SetNextItemWidth(48 * scale);
                    int opI = (int)c.Op;
                    if (ImGui.Combo("##co", ref opI, CondOpNames, CondOpNames.Length))
                    { c.Op = (ConditionOp)opI; _dirty = true; }

                    ImGui.TableSetColumnIndex(2);
                    ImGui.SetNextItemWidth(56 * scale);
                    float val = c.Value;
                    if (ImGui.InputFloat("##cv", ref val, 0f, 0f, "%.0f"))
                    { c.Value = val; _dirty = true; }

                    ImGui.TableSetColumnIndex(3);
                    bool isPct = c.IsPercentage;
                    if (ImGui.Checkbox("##cpct", ref isPct))
                    { c.IsPercentage = isPct; _dirty = true; }

                    ImGui.TableSetColumnIndex(4);
                    if (ImGui.SmallButton($"X##cdel{i}"))
                    { skill.Conditions.RemoveAt(i); _dirty = true; }

                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            if (ImGui.SmallButton("+ Add Condition##rpsk_addcond"))
            { skill.Conditions.Add(new SkillCondition { FieldId = allFields.FirstOrDefault()?.Id ?? "" }); _dirty = true; }

            ImGui.Spacing();
            ImGui.Separator();
        }

        // Effects
        ImGui.TextUnformatted("Effects");

        if (ImGui.BeginTable("##rpsk_efx", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 80 * scale);
            ImGui.TableSetupColumn("Op",     ImGuiTableColumnFlags.WidthFixed, 52 * scale);
            ImGui.TableSetupColumn("Value",  ImGuiTableColumnFlags.WidthFixed, 60 * scale);
            ImGui.TableSetupColumn("%",      ImGuiTableColumnFlags.WidthFixed, 24 * scale);
            ImGui.TableSetupColumn("",       ImGuiTableColumnFlags.WidthFixed, 22 * scale);
            ImGui.TableHeadersRow();

            for (int i = skill.Effects.Count - 1; i >= 0; i--)
            {
                var fx = skill.Effects[i];
                ImGui.TableNextRow();
                ImGui.PushID($"##efx{i}");

                ImGui.TableSetColumnIndex(0);
                ImGui.SetNextItemWidth(76 * scale);
                int tgtI = allFields.FindIndex(f => f.Id == SkillHelpers.EffectiveFieldId(fx));
                if (tgtI < 0) tgtI = 0;
                if (ImGui.Combo("##et", ref tgtI, fieldNames, fieldNames.Length))
                { fx.FieldId = allFields[tgtI].Id; _dirty = true; }

                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(48 * scale);
                int opI = (int)fx.Op;
                if (ImGui.Combo("##eo", ref opI, EffectOpNames, EffectOpNames.Length))
                { fx.Op = (EffectOp)opI; _dirty = true; }

                ImGui.TableSetColumnIndex(2);
                ImGui.SetNextItemWidth(56 * scale);
                float val = fx.Value;
                if (ImGui.InputFloat("##ev", ref val, 0f, 0f, "%.1f"))
                { fx.Value = val; _dirty = true; }

                ImGui.TableSetColumnIndex(3);
                bool isPct = fx.IsPercentage;
                if (ImGui.Checkbox("##epct", ref isPct))
                { fx.IsPercentage = isPct; _dirty = true; }

                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton($"X##edel{i}"))
                { skill.Effects.RemoveAt(i); _dirty = true; }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (ImGui.SmallButton("+ Add Effect##rpsk_addefx"))
        { skill.Effects.Add(new SkillEffect { FieldId = allFields.FirstOrDefault()?.Id ?? "" }); _dirty = true; }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        float saveW = 80 * scale;
        float avail = ImGui.GetContentRegionAvail().X;
        if (avail > saveW)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - saveW);

        using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.18f, 0.55f, 0.18f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.70f, 0.25f, 1f)))
        {
            if (ImGui.Button("Save##rpsk_save", new Vector2(saveW, 0)))
            {
                skill.IsLocked = true;
                plugin.Configuration.Save();
                plugin.PushLocalProfile();
                _dirty = false;
            }
        }
    }

    // ── Locked (read-only) view ───────────────────────────────────────────────

    private void DrawLockedView(RpSkill skill, RpCharacter ch, SheetTemplate template, float scale)
    {
        string GetFieldName(string fid) => template.FindField(fid)?.Name ?? fid;

        string typeBadge = skill.Type == SkillType.Active ? "[Active]" : "[Passive]";
        ImGui.TextDisabled(typeBadge);
        ImGui.SameLine();
        ImGui.TextUnformatted(skill.Name);

        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            ImGui.Spacing();
            ImGui.TextDisabled(skill.Description);
            ImGui.Spacing();
        }
        ImGui.Separator();

        ImGui.Spacing();
        bool hasMeta = skill.Cooldown > 0 || skill.Duration > 0
                       || (skill.Type == SkillType.Passive && skill.TriggerOnTurnEnd);
        if (hasMeta)
        {
            bool first = true;
            if (skill.Cooldown > 0)
            {
                ImGui.TextDisabled($"Cooldown: {skill.Cooldown} turn{(skill.Cooldown == 1 ? "" : "s")}");
                first = false;
            }
            if (skill.Duration > 0)
            {
                if (!first) ImGui.SameLine();
                ImGui.TextDisabled($"  Duration: {skill.Duration} turn{(skill.Duration == 1 ? "" : "s")}");
                first = false;
            }
            if (skill.Type == SkillType.Passive && skill.TriggerOnTurnEnd)
            {
                if (!first) ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.85f, 0.70f, 0.15f, 1f), first ? "[Turn End]" : "  [Turn End]");
            }
            ImGui.Spacing();
        }
        ImGui.Separator();

        if (skill.Type == SkillType.Passive && skill.Conditions.Count > 0)
        {
            ImGui.TextUnformatted("Conditions");
            foreach (var c in skill.Conditions)
            {
                string pct  = c.IsPercentage ? "%" : "";
                ImGui.TextDisabled($"  {GetFieldName(SkillHelpers.EffectiveFieldId(c))} {CondOpNames[(int)c.Op]} {c.Value:0}{pct}");
            }
            ImGui.Spacing();
            ImGui.Separator();
        }

        if (skill.Effects.Count > 0)
        {
            ImGui.TextUnformatted("Effects");
            foreach (var fx in skill.Effects)
            {
                string pct  = fx.IsPercentage ? "%" : "";
                ImGui.TextDisabled($"  {GetFieldName(SkillHelpers.EffectiveFieldId(fx))} {EffectOpNames[(int)fx.Op]} {fx.Value:0.#}{pct}");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool isConditionPassive = skill.Type == SkillType.Passive && skill.Conditions.Count > 0;

        if (isConditionPassive)
        {
            bool active = SkillHelpers.ConditionsMet(skill, ch, template);
            var  col    = active ? new Vector4(0.2f, 0.85f, 0.3f, 1f)
                                 : new Vector4(0.55f, 0.55f, 0.55f, 1f);
            ImGui.TextColored(col, active ? "● Active (applied in rolls)" : "● Inactive");
        }
        else
        {
            bool onCooldown = skill.CooldownRemaining  > 0;
            bool onDuration = skill.DurationRemaining > 0;
            bool blocked    = onCooldown || onDuration;

            string btnLabel = skill.Type == SkillType.Active ? "Use Skill##rpsk_use" : "Trigger##rpsk_use";

            if (blocked)
            {
                using var _ = ImRaii.Disabled();
                ImGui.Button(btnLabel);
            }
            else if (ImGui.Button(btnLabel))
            {
                SkillHelpers.ApplyEffects(skill, ch, template);
                plugin.Configuration.Save();
                plugin.PushLocalProfile();
            }

            if (onDuration)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.2f, 0.85f, 0.3f, 1f),
                    $"● Active ({skill.DurationRemaining} turn{(skill.DurationRemaining == 1 ? "" : "s")} remaining)");
            }
            else if (onCooldown)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"({skill.CooldownRemaining} turn{(skill.CooldownRemaining == 1 ? "" : "s")} cooldown)");
            }
        }
    }
}
