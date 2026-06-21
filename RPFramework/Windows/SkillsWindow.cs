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
    private bool _editing;   // selected skill shows the read-only view until this is flipped on

    // The entity whose skills are being edited (PC = LocalPlayerId, or a companion/NPC id). Set in DrawBody.
    private string _code     = "";
    private string _entityId = "";
    private string _syncedId = "";   // entity the current draft was pulled for (re-sync when it changes)

    /// <summary>The character the editor is currently bound to (target entity, falling back to the local PC).</summary>
    private CharacterDto? TargetCharacter()
        => _code.Length > 0 ? plugin.Store.Character(_code, _entityId) : plugin.ActiveCharacter;

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
        var ch = TargetCharacter();
        if (ch != null) _draft.AddRange(ch.State.Skills.Select(Clone));
        _dirty    = false;
        _editing  = false;
        _syncedId = _entityId;
        if (_selectedIdx >= _draft.Count) _selectedIdx = _draft.Count - 1;
    }

    private static SkillCondition CloneCond(SkillCondition c) => new() { FieldId = c.FieldId, Op = c.Op, Value = c.Value, IsPercentage = c.IsPercentage };
    private static SkillEffect    CloneFx(SkillEffect e)      => new() { FieldId = e.FieldId, Op = e.Op, Value = e.Value, IsPercentage = e.IsPercentage };
    private static EffectBlock    CloneBlock(EffectBlock b)   => new()
    {
        Conditions  = b.Conditions.Select(CloneCond).ToList(),
        Effects     = b.Effects.Select(CloneFx).ToList(),
        ElseEffects = b.ElseEffects.Select(CloneFx).ToList(),
    };

    private static RpSkill Clone(RpSkill s)
    {
        var blocks    = s.ConditionalBlocks.Select(CloneBlock).ToList();
        var baseConds = s.Conditions.Select(CloneCond).ToList();
        var baseFx    = s.Effects.Select(CloneFx).ToList();

        if (s.TriggerOnTurnEnd && (baseFx.Count > 0 || baseConds.Count > 0))
        {
            // Legacy whole-skill "Trigger on Turn End" flag is now an explicit "On Turn End" block:
            // its base conditions+effects become a leading block carrying the turn-end marker. The flag
            // is dropped below. Lossless + idempotent.
            baseConds.Insert(0, new SkillCondition { FieldId = StatMath.OnTurnEndId });
            blocks.Insert(0, new EffectBlock { Conditions = baseConds, Effects = baseFx });
            baseConds = new();
            baseFx    = new();
        }
        else if (baseConds.Count > 0)
        {
            // Legacy base conditions are equivalent to a leading conditional block (a passive's base
            // conditions+effects fire exactly like a met block). The standalone base-conditions editor has
            // been retired in favour of the unified block list, so fold them in here. Lossless + idempotent.
            blocks.Insert(0, new EffectBlock { Conditions = baseConds, Effects = baseFx });
            baseConds = new();
            baseFx    = new();
        }

        return new RpSkill
        {
            Id = s.Id, Name = s.Name, Description = s.Description, Type = s.Type,
            Cooldown = s.Cooldown, Duration = s.Duration,
            CooldownRemaining = s.CooldownRemaining, DurationRemaining = s.DurationRemaining,
            IsLocked = s.IsLocked, TriggerOnTurnEnd = false,
            Conditions        = baseConds,
            Effects           = baseFx,
            ConditionalBlocks = blocks,
        };
    }

    /// <summary>Re-pull the draft from the authoritative store when entering the Skills tab, but only if
    /// there is nothing in flight - an unsaved edit or an open editor is preserved across tab switches so
    /// the user doesn't lose work by glancing at another tab.</summary>
    internal void SyncFromStore() { if (!_dirty && !_editing) SyncDraft(); }

    public override void Draw()
    {
        string? code = plugin.ActiveCampaign;
        if (code == null || plugin.LocalPlayerId == null || plugin.ActiveCharacter == null)
        {
            ImGui.TextDisabled("Connect and select a campaign to manage skills.");
            return;
        }
        DrawBody(code, plugin.LocalPlayerId);
    }

    /// <summary>The skills editor body (left list + right editor) for a target entity, hosted standalone or
    /// in the RPCHARACTER Skills tab (PC = LocalPlayerId, or a companion/NPC id).</summary>
    internal void DrawBody(string code, string entityId)
    {
        _code = code; _entityId = entityId;
        var character = TargetCharacter();
        if (character == null) { ImGui.TextDisabled("No character in this campaign yet."); return; }
        float scale = ImGuiHelpers.GlobalScale;
        var   template = plugin.Store.TemplateOrDefault(code);

        // Switching to a different entity always re-pulls (the draft belongs to the old entity; unsaved
        // edits there are discarded by design). For the same entity, only re-pull when clean and the
        // authoritative skill count changed elsewhere — preserving an in-progress edit across tab switches.
        if (_syncedId != entityId || (!_dirty && character.State.Skills.Count != _draft.Count)) SyncDraft();

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
                    if (ImGui.Selectable($"{badge} {sk.Name}{tag}##rpsk_{i}", selected))
                    { if (_selectedIdx != i) _editing = false; _selectedIdx = i; }
                    if (selected) ImGui.PopStyleColor();
                    if (ImGui.BeginPopupContextItem($"##rpsk_ctx{i}"))
                    {
                        if (_selectedIdx != i) _editing = false;
                        _selectedIdx = i;
                        if (ImGui.MenuItem("Edit##rpsk_ctx_edit")) _editing = true;
                        ImGui.Separator();
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
                    _editing = true;   // new skills open straight into the editor
                    _dirty = true;
                }
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

        if (_editing) DrawEditor(_draft[_selectedIdx], template, code, scale);
        else          DrawSkillView(_draft[_selectedIdx], template, code, scale);
    }

    private void DrawEditor(RpSkill skill, SheetTemplate template, string code, float scale)
    {
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

        ImGui.TextUnformatted("Effects");
        var fxFields = EffectEditor.TargetFields(template);
        var fxNames  = fxFields.Select(f => f.Name).ToArray();
        DrawEffects(skill.Effects, fxFields, fxNames, scale);
        if (ImGui.SmallButton("+ Add Effect##rpsk_addefx"))
        { skill.Effects.Add(new SkillEffect { FieldId = fxFields.FirstOrDefault()?.Id ?? "" }); _dirty = true; }

        // Independent conditional blocks: extra (if -> then) groups on top of the base effects above.
        ImGui.Spacing(); ImGui.Separator();
        ImGui.TextUnformatted("Conditional blocks");
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled("Each block's effects apply independently while its own conditions hold. " +
                           "Use the \"On Turn End\" trigger to fire a block once per turn instead.");
        ImGui.PopTextWrapPos();
        var condFields = ConditionEditor.TargetFields(template, includeTurnEnd: true);
        var condNames  = condFields.Select(f => f.Name).ToArray();
        if (BlockListEditor.Draw(skill.ConditionalBlocks, condFields, condNames, fxFields, fxNames, scale, "rpsk_blocks"))
            _dirty = true;

        // ── Save this edit (publishes the whole skill list) and return to the read-only view ──
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.18f, 0.55f, 0.18f, 1f)))
            if (ImGui.Button("Save##rpsk_save", new Vector2(-1, 0)))
            {
                _ = plugin.Network.CharacterSetSkills(code, _entityId, _draft.Select(Clone).ToList());
                _dirty   = false;
                _editing = false;
            }
    }

    /// <summary>Read-only presentation of a skill: details, effects, live block status, and the Use/Trigger
    /// button. An "Edit" button flips into <see cref="DrawEditor"/>. This is the default pane.</summary>
    private void DrawSkillView(RpSkill skill, SheetTemplate template, string code, float scale)
    {
        ImGui.TextDisabled(skill.Type == SkillType.Active ? "[Active]" : "[Passive]");
        ImGui.SameLine();
        ImGui.TextUnformatted(skill.Name);
        ImGui.Separator();

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(skill.Description);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
        }

        if (skill.Cooldown > 0 || skill.Duration > 0)
        {
            if (skill.Cooldown > 0) ImGui.TextDisabled($"Cooldown: {skill.Cooldown}t");
            if (skill.Duration > 0) { if (skill.Cooldown > 0) ImGui.SameLine(); ImGui.TextDisabled($"  Duration: {skill.Duration}t"); }
        }

        ImGui.Spacing(); ImGui.Separator();

        string baseFx = ItemEffects.Summary(skill.Effects, template);
        if (!string.IsNullOrEmpty(baseFx))
        {
            ImGui.TextUnformatted("Effects");
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), baseFx);
            ImGui.PopTextWrapPos();
        }

        if (skill.ConditionalBlocks.Count > 0)
        {
            var st = TargetCharacter()?.State;
            ImGui.Spacing();
            ImGui.TextUnformatted("Conditional");
            ImGui.PushTextWrapPos(0f);
            foreach (var b in skill.ConditionalBlocks)
            {
                string fx = ItemEffects.Summary(b.Effects, template);
                if (string.IsNullOrEmpty(fx)) continue;
                string cond = ItemEffects.ConditionSummary(b.Conditions, template);
                bool   met  = st != null && StatMath.ConditionsMet(b.Conditions, st, template);
                var    col  = met ? new Vector4(0.45f, 0.85f, 0.45f, 1f) : new Vector4(0.65f, 0.65f, 0.65f, 1f);
                ImGui.TextColored(col, string.IsNullOrEmpty(cond) ? $"Always: {fx}" : $"If {cond}: {fx}");
                string elseFx = ItemEffects.Summary(b.ElseEffects, template);
                if (!string.IsNullOrEmpty(elseFx))
                    ImGui.TextColored(met ? new Vector4(0.65f, 0.65f, 0.65f, 1f) : new Vector4(0.45f, 0.85f, 0.45f, 1f), $"  Else: {elseFx}");
            }
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        DrawUseRow(skill, code);

        ImGui.Spacing();
        ImGui.TextDisabled("Right-click a skill in the list to edit or delete.");
    }

    /// <summary>The Use/Trigger button + live cooldown/duration status, shared by the view and editor panes.</summary>
    private void DrawUseRow(RpSkill skill, string code)
    {
        var live = TargetCharacter()?.State.Skills.FirstOrDefault(s => s.Id == skill.Id);
        bool blocked = _dirty || live == null || live.CooldownRemaining > 0 || live.DurationRemaining > 0;
        string useLabel = skill.Type == SkillType.Active ? "Use Skill##rpsk_use" : "Trigger##rpsk_use";
        using (ImRaii.Disabled(blocked))
            if (ImGui.Button(useLabel)) _ = plugin.Network.UseSkill(code, _entityId, skill.Id);
        if (_dirty) { ImGui.SameLine(); ImGui.TextDisabled("(save first)"); }
        else if (live is { DurationRemaining: > 0 }) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.2f, 0.85f, 0.3f, 1f), $"● {live.DurationRemaining}t remaining"); }
        else if (live is { CooldownRemaining: > 0 }) { ImGui.SameLine(); ImGui.TextDisabled($"({live.CooldownRemaining}t cooldown)"); }
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
