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
    private static SkillEffect    CloneFx(SkillEffect e)      => new() { FieldId = e.FieldId, Op = e.Op, Value = e.Value, IsPercentage = e.IsPercentage, GrantPassiveId = e.GrantPassiveId };
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
            IsLocked = s.IsLocked, Active = s.Active, IsDmSkill = s.IsDmSkill, TriggerOnTurnEnd = false,
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

                void Row(int i)
                {
                    var    sk    = _draft[i];
                    var    live  = character.State.Skills.FirstOrDefault(s => s.Id == sk.Id);
                    string badge = sk.Type == SkillType.Active ? "[A]" : "[P]";
                    string tag   = live is { Active: true }            ? " *"
                                 : live is { DurationRemaining: > 0 } ? $" [{live.DurationRemaining}t]"
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

                // Character skills first, then a divided "DM Skills" vault section for DM-authored ones.
                for (int i = 0; i < _draft.Count; i++) if (!_draft[i].IsDmSkill) Row(i);

                if (_draft.Any(s => s.IsDmSkill))
                {
                    ImGuiHelpers.ScaledDummy(4f);
                    CharacterSheetWindow.DrawSectionHeader("DM Skills", scale);
                    for (int i = 0; i < _draft.Count; i++) if (_draft[i].IsDmSkill) Row(i);
                }

                // Passives inherited from equipped items (read-only, active while the item is worn).
                var granted = character.State.Equipment.Values
                    .Where(it => it.GrantedPassives is { Count: > 0 })
                    .SelectMany(it => it.GrantedPassives!.Select(p => (Item: it.Name, Skill: p))).ToList();
                if (granted.Count > 0)
                {
                    ImGuiHelpers.ScaledDummy(4f);
                    CharacterSheetWindow.DrawSectionHeader("From Equipment", scale);
                    foreach (var (itemName, p) in granted)
                    {
                        ImGui.TextDisabled($"[P] {p.Name}");
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted($"From {itemName}");
                            string fx = ItemEffects.Summary(p.Effects, template);
                            if (!string.IsNullOrEmpty(fx)) ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), fx);
                            ImGui.EndTooltip();
                        }
                    }
                }

                if (deleteAt >= 0)
                {
                    _draft.RemoveAt(deleteAt);
                    if (_selectedIdx >= _draft.Count) _selectedIdx = _draft.Count - 1;
                    // Publish the deletion immediately: there is no Save button outside the editor, and a
                    // removed skill must actually leave the server list for its (live) effects to disappear.
                    _ = plugin.Network.CharacterSetSkills(code, _entityId, _draft.Select(Clone).ToList());
                    _dirty   = false;
                    _editing = false;
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
        CharacterSheetWindow.DrawSectionHeader("Details", scale);

        ImGui.TextColored(CharacterSheetWindow.LabelMuted, "Name");
        ImGui.SetNextItemWidth(-1);
        string name = skill.Name;
        if (ImGui.InputText("##rpsk_name", ref name, 64)) { skill.Name = name; _dirty = true; }

        ImGui.Spacing();
        ImGui.TextColored(CharacterSheetWindow.LabelMuted, "Description");
        ImGui.SetNextItemWidth(-1);
        string desc = skill.Description;
        if (ImGui.InputTextMultiline("##rpsk_desc", ref desc, 512, new Vector2(-1, 54 * scale))) { skill.Description = desc; _dirty = true; }

        ImGui.Spacing();
        ImGui.TextColored(CharacterSheetWindow.LabelMuted, "Type");
        int typeVal = (int)skill.Type;
        if (ImGui.RadioButton("Active##rpsk_ta",  ref typeVal, 0)) { skill.Type = SkillType.Active;  _dirty = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Passive##rpsk_tp", ref typeVal, 1)) { skill.Type = SkillType.Passive; _dirty = true; }

        // DM vault flag: a DM authors passives here to embed into items they hand out via trade.
        if (plugin.IsDm(code))
        {
            ImGui.SameLine();
            ImGuiHelpers.ScaledDummy(12f, 0f); ImGui.SameLine();
            bool isDmSkill = skill.IsDmSkill;
            if (ImGui.Checkbox("DM##rpsk_dm", ref isDmSkill)) { skill.IsDmSkill = isDmSkill; _dirty = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mark as a DM vault skill: filed separately and usable as an item-granted passive.");
        }

        ImGui.Spacing();
        ImGui.TextColored(CharacterSheetWindow.LabelMuted, "Cooldown (turns)");
        ImGui.SetNextItemWidth(80 * scale);
        int cd = skill.Cooldown;
        if (ImGui.InputInt("##rpsk_cd", ref cd, 1, 1)) { skill.Cooldown = Math.Max(0, cd); _dirty = true; }

        ImGui.Spacing();
        ImGui.TextColored(CharacterSheetWindow.LabelMuted, "Duration (turns)");
        ImGui.SetNextItemWidth(80 * scale);
        int dur = skill.Duration;
        if (ImGui.InputInt("##rpsk_dur", ref dur, 1, 1)) { skill.Duration = Math.Max(0, dur); _dirty = true; }
        ImGui.SameLine();
        ImGui.TextDisabled("0 = instant / permanent");

        ImGui.Spacing();

        // Active skills can grant the character's own passives; passives can't (they never fire ApplyEffects).
        var grantables = skill.Type == SkillType.Active
            ? _draft.Where(s => s.Type == SkillType.Passive && s.Id != skill.Id).ToList()
            : null;

        CharacterSheetWindow.DrawSectionHeader("Effects", scale);
        DrawEffects(skill.Effects, template, grantables, scale);
        if (ImGui.SmallButton("+ Add Effect##rpsk_addefx"))
        { skill.Effects.Add(EffectEditor.NewEffect(template)); _dirty = true; }

        // Independent conditional blocks: extra (if -> then) groups on top of the base effects above.
        ImGui.Spacing();
        CharacterSheetWindow.DrawSectionHeader("Conditional blocks", scale);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled("Each block's effects apply independently while its own conditions hold. " +
                           "Set a block's Trigger to \"On Turn End\" to fire it once per turn instead.");
        ImGui.PopTextWrapPos();
        if (BlockListEditor.Draw(skill.ConditionalBlocks, template, includeBarCurrent: true, grantables, scale, "rpsk_blocks"))
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
        CharacterSheetWindow.DrawSectionHeader(skill.Name, scale);
        ImGui.TextColored(CharacterSheetWindow.LabelMuted, skill.Type == SkillType.Active ? "Active" : "Passive");
        if (skill.IsDmSkill) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.65f, 0.75f, 1f, 1f), "· DM"); }

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(skill.Description);
            ImGui.PopTextWrapPos();
        }

        if (skill.Cooldown > 0 || skill.Duration > 0)
        {
            ImGui.Spacing();
            if (skill.Cooldown > 0) ImGui.TextDisabled($"Cooldown: {skill.Cooldown}t");
            if (skill.Duration > 0) { if (skill.Cooldown > 0) ImGui.SameLine(); ImGui.TextDisabled($"  Duration: {skill.Duration}t"); }
        }

        ImGuiHelpers.ScaledDummy(4f);

        string baseFx = ItemEffects.Summary(skill.Effects, template);
        if (!string.IsNullOrEmpty(baseFx))
        {
            CharacterSheetWindow.DrawSectionHeader("Effects", scale);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), baseFx);
            ImGui.PopTextWrapPos();
        }

        if (skill.ConditionalBlocks.Count > 0)
        {
            var st = TargetCharacter()?.State;
            ImGuiHelpers.ScaledDummy(4f);
            CharacterSheetWindow.DrawSectionHeader("Conditional", scale);
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

    /// <summary>The Use/Toggle button + live status, shared by the view and editor panes. Active skills
    /// fire once (UseSkill applies effects + cooldown); passives toggle their live effects on and off.</summary>
    private void DrawUseRow(RpSkill skill, string code)
    {
        var live = TargetCharacter()?.State.Skills.FirstOrDefault(s => s.Id == skill.Id);

        if (skill.Type == SkillType.Passive)
        {
            bool on = live?.Active ?? false;
            using (ImRaii.Disabled(_dirty || live == null))
            using (ImRaii.PushColor(ImGuiCol.Button, on ? new Vector4(0.55f, 0.35f, 0.15f, 1f) : new Vector4(0.18f, 0.45f, 0.60f, 1f)))
                if (ImGui.Button(on ? "Disable##rpsk_use" : "Enable##rpsk_use")) _ = plugin.Network.UseSkill(code, _entityId, skill.Id);
            if (_dirty)  { ImGui.SameLine(); ImGui.TextDisabled("(save first)"); }
            else if (on) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.2f, 0.85f, 0.3f, 1f), "● Active"); }
            return;
        }

        bool blocked = _dirty || live == null || live.CooldownRemaining > 0 || live.DurationRemaining > 0;
        using (ImRaii.Disabled(blocked))
            if (ImGui.Button("Use Skill##rpsk_use")) _ = plugin.Network.UseSkill(code, _entityId, skill.Id);
        if (_dirty) { ImGui.SameLine(); ImGui.TextDisabled("(save first)"); }
        else if (live is { DurationRemaining: > 0 }) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.2f, 0.85f, 0.3f, 1f), $"● {live.DurationRemaining}t remaining"); }
        else if (live is { CooldownRemaining: > 0 }) { ImGui.SameLine(); ImGui.TextDisabled($"({live.CooldownRemaining}t cooldown)"); }
    }

    private void DrawEffects(List<SkillEffect> fxs, SheetTemplate template, IReadOnlyList<RpSkill>? grantables, float scale)
    {
        if (fxs.Count == 0) return;
        if (!ImGui.BeginTable("##rpsk_efx", 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV)) return;
        EffectEditor.SetupCols(scale);
        for (int i = fxs.Count - 1; i >= 0; i--)
        {
            ImGui.TableNextRow(); ImGui.PushID($"##efx{i}");
            if (EffectEditor.DrawRow(fxs[i], template, includeBarCurrent: true, grantables)) _dirty = true;
            ImGui.TableSetColumnIndex(5);
            if (ImGui.SmallButton($"X##edel{i}")) { fxs.RemoveAt(i); _dirty = true; }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }
}
