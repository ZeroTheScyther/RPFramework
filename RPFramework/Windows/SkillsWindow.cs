using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

/// <summary>
/// Skills editor. Edits a local draft of the active character's skills and publishes the
/// whole list via CharacterSetSkills. Activating a skill is a UseSkill intent (the server
/// applies effects + cooldowns authoritatively); cooldown/duration shown live from the store.
/// </summary>
public class SkillsWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private readonly List<RpSkill> _draft = new();
    private int  _selectedIdx = -1;
    private bool _dirty;

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

    public override void OnOpen() => SyncDraft();

    private void SyncDraft()
    {
        _draft.Clear();
        var ch = plugin.ActiveCharacter;
        if (ch != null) _draft.AddRange(ch.State.Skills.Select(Clone));
        _dirty = false;
        if (_selectedIdx >= _draft.Count) _selectedIdx = _draft.Count - 1;
    }

    private static RpSkill Clone(RpSkill s) => new()
    {
        Id = s.Id, Name = s.Name, Description = s.Description, Type = s.Type,
        Cooldown = s.Cooldown, Duration = s.Duration,
        CooldownRemaining = s.CooldownRemaining, DurationRemaining = s.DurationRemaining,
        IsLocked = s.IsLocked, TriggerOnTurnEnd = s.TriggerOnTurnEnd,
        Conditions = s.Conditions.Select(c => new SkillCondition { FieldId = c.FieldId, Op = c.Op, Value = c.Value, IsPercentage = c.IsPercentage }).ToList(),
        Effects    = s.Effects.Select(e => new SkillEffect { FieldId = e.FieldId, Op = e.Op, Value = e.Value, IsPercentage = e.IsPercentage }).ToList(),
    };

    /// <summary>Re-pull the draft from the authoritative store (call when entering the Skills tab).</summary>
    internal void SyncFromStore() => SyncDraft();

    public override void Draw()
    {
        string? code = plugin.ActiveCampaign;
        if (code == null || plugin.ActiveCharacter == null)
        {
            ImGui.TextDisabled("Connect and select a campaign to manage skills.");
            return;
        }
        DrawBody(code);
    }

    /// <summary>The skills editor body (left list + right editor), hosted standalone or in the RPCHARACTER Skills tab.</summary>
    internal void DrawBody(string code)
    {
        var character = plugin.ActiveCharacter;
        if (character == null) { ImGui.TextDisabled("No character in this campaign yet."); return; }
        float scale = ImGuiHelpers.GlobalScale;
        var   template = plugin.Store.TemplateOrDefault(code);

        // If the authoritative skill count changed (e.g. another device) and we're clean, re-sync.
        if (!_dirty && character.State.Skills.Count != _draft.Count) SyncDraft();

        // ── Left pane ──────────────────────────────────────────────────────────
        float leftW = 180 * scale;
        using (var leftChild = ImRaii.Child("##skilllist", new Vector2(leftW, -1), false))
        {
            if (leftChild)
            {
                ImGui.Spacing();
                int deleteAt = -1;
                for (int i = 0; i < _draft.Count; i++)
                {
                    var    sk    = _draft[i];
                    var    live  = character.State.Skills.FirstOrDefault(s => s.Id == sk.Id);
                    string badge = sk.Type == SkillType.Active ? "[A]" : "[P]";
                    string tag   = live is { DurationRemaining: > 0 } ? $" [{live.DurationRemaining}t]"
                                 : live is { CooldownRemaining: > 0 } ? $" (cd:{live.CooldownRemaining}t)"
                                 : "";
                    bool selected = _selectedIdx == i;
                    if (selected) ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.26f, 0.59f, 0.98f, 0.4f));
                    if (ImGui.Selectable($"{badge} {sk.Name}{tag}##rpsk_{i}", selected)) _selectedIdx = i;
                    if (selected) ImGui.PopStyleColor();
                    if (ImGui.BeginPopupContextItem($"##rpsk_ctx{i}"))
                    {
                        _selectedIdx = i;
                        if (ImGui.MenuItem("Delete##rpsk_ctx_del")) deleteAt = i;
                        ImGui.EndPopup();
                    }
                }
                if (deleteAt >= 0)
                {
                    _draft.RemoveAt(deleteAt);
                    if (_selectedIdx >= _draft.Count) _selectedIdx = _draft.Count - 1;
                    _dirty = true;
                }

                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                if (ImGui.Button("New Skill##rpsk_new", new Vector2(-1, 0)))
                {
                    _draft.Add(new RpSkill { Type = SkillType.Active });
                    _selectedIdx = _draft.Count - 1;
                    _dirty = true;
                }

                using (ImRaii.Disabled(!_dirty))
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.18f, 0.55f, 0.18f, 1f)))
                    if (ImGui.Button("Save Changes##rpsk_save", new Vector2(-1, 0)))
                    {
                        _ = plugin.Network.CharacterSetSkills(code, _draft.Select(Clone).ToList());
                        _dirty = false;
                    }
                if (_dirty) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Unsaved changes");
            }
        }

        ImGui.SameLine();

        using var rightChild = ImRaii.Child("##skilleditor", new Vector2(-1, -1), false);
        if (!rightChild) return;

        if (_selectedIdx < 0 || _selectedIdx >= _draft.Count)
        {
            ImGui.TextDisabled("Select a skill or create a new one.");
            return;
        }

        DrawEditor(_draft[_selectedIdx], template, code, scale);
    }

    private void DrawEditor(RpSkill skill, SheetTemplate template, string code, float scale)
    {
        var allFields  = template.Groups.SelectMany(g => g.Fields).ToList();
        var fieldNames = allFields.Select(f => f.Name).ToArray();

        ImGui.TextUnformatted("Name");
        ImGui.SetNextItemWidth(-1);
        string name = skill.Name;
        if (ImGui.InputText("##rpsk_name", ref name, 64)) { skill.Name = name; _dirty = true; }

        ImGui.Spacing();
        ImGui.TextUnformatted("Description");
        ImGui.SetNextItemWidth(-1);
        string desc = skill.Description;
        if (ImGui.InputTextMultiline("##rpsk_desc", ref desc, 512, new Vector2(-1, 54 * scale))) { skill.Description = desc; _dirty = true; }

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
            if (ImGui.Checkbox("Trigger on Turn End##rpsk_tote", ref tot)) { skill.TriggerOnTurnEnd = tot; _dirty = true; }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Cooldown (turns)");
        ImGui.SetNextItemWidth(80 * scale);
        int cd = skill.Cooldown;
        if (ImGui.InputInt("##rpsk_cd", ref cd, 1, 1)) { skill.Cooldown = Math.Max(0, cd); _dirty = true; }

        ImGui.Spacing();
        ImGui.TextUnformatted("Duration (turns)");
        ImGui.SetNextItemWidth(80 * scale);
        int dur = skill.Duration;
        if (ImGui.InputInt("##rpsk_dur", ref dur, 1, 1)) { skill.Duration = Math.Max(0, dur); _dirty = true; }
        ImGui.SameLine();
        ImGui.TextDisabled("0 = instant / permanent");

        ImGui.Spacing(); ImGui.Separator();

        if (skill.Type == SkillType.Passive)
        {
            ImGui.TextUnformatted("Conditions  (fires when ALL are true)");
            DrawParts(skill.Conditions, allFields, fieldNames, scale);
            if (ImGui.SmallButton("+ Add Condition##rpsk_addcond"))
            { skill.Conditions.Add(new SkillCondition { FieldId = allFields.FirstOrDefault()?.Id ?? "" }); _dirty = true; }
            ImGui.Spacing(); ImGui.Separator();
        }

        ImGui.TextUnformatted("Effects");
        var fxFields = EffectEditor.TargetFields(template);
        var fxNames  = fxFields.Select(f => f.Name).ToArray();
        DrawEffects(skill.Effects, fxFields, fxNames, scale);
        if (ImGui.SmallButton("+ Add Effect##rpsk_addefx"))
        { skill.Effects.Add(new SkillEffect { FieldId = fxFields.FirstOrDefault()?.Id ?? "" }); _dirty = true; }

        // ── Use / activate (authoritative; disabled while unsaved or on cooldown) ──
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        var live = plugin.ActiveCharacter?.State.Skills.FirstOrDefault(s => s.Id == skill.Id);
        bool blocked = _dirty || live == null || live.CooldownRemaining > 0 || live.DurationRemaining > 0;
        string useLabel = skill.Type == SkillType.Active ? "Use Skill##rpsk_use" : "Trigger##rpsk_use";
        using (ImRaii.Disabled(blocked))
            if (ImGui.Button(useLabel)) _ = plugin.Network.UseSkill(code, skill.Id);
        if (_dirty) { ImGui.SameLine(); ImGui.TextDisabled("(save first)"); }
        else if (live is { DurationRemaining: > 0 }) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.2f, 0.85f, 0.3f, 1f), $"● {live.DurationRemaining}t remaining"); }
        else if (live is { CooldownRemaining: > 0 }) { ImGui.SameLine(); ImGui.TextDisabled($"({live.CooldownRemaining}t cooldown)"); }
    }

    private void DrawParts(List<SkillCondition> conds, List<SheetField> allFields, string[] fieldNames, float scale)
    {
        if (!ImGui.BeginTable("##rpsk_conds", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV)) return;
        SetupPartCols(scale);
        for (int i = conds.Count - 1; i >= 0; i--)
        {
            ImGui.TableNextRow(); ImGui.PushID($"##cond{i}");
            if (ConditionEditor.DrawRow(conds[i], allFields, fieldNames)) _dirty = true;
            ImGui.TableSetColumnIndex(4);
            if (ImGui.SmallButton($"X##cdel{i}")) { conds.RemoveAt(i); _dirty = true; }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawEffects(List<SkillEffect> fxs, List<SheetField> fields, string[] fieldNames, float scale)
    {
        if (!ImGui.BeginTable("##rpsk_efx", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV)) return;
        SetupPartCols(scale);
        for (int i = fxs.Count - 1; i >= 0; i--)
        {
            ImGui.TableNextRow(); ImGui.PushID($"##efx{i}");
            if (EffectEditor.DrawRow(fxs[i], fields, fieldNames)) _dirty = true;
            ImGui.TableSetColumnIndex(4);
            if (ImGui.SmallButton($"X##edel{i}")) { fxs.RemoveAt(i); _dirty = true; }
            ImGui.PopID();
        }
        ImGui.EndTable();
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
