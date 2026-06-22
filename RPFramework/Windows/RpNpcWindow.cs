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
/// The RPNPC "vault": a building-bench for companions (and, for DMs, NPCs). A left-hand roster of
/// entities plus a full character sheet on the right (Profile / Stats / Skills) for the selected one.
/// Companions are owned by the viewer; the viewer marks one as their active companion per campaign,
/// which is what the RPCHARACTER Companions tab then surfaces. NPCs are DM-owned campaign actors.
///
/// The right-pane bodies are reused from <see cref="CharacterSheetWindow"/> and a private
/// <see cref="SkillsWindow"/> instance (private so its single draft never collides with the shared
/// Skills tab in <see cref="RpCharacterWindow"/>), each pointed at the selected entity id.
/// </summary>
public class RpNpcWindow : Window, IDisposable
{
    private readonly Plugin      _plugin;
    private readonly SkillsWindow _skills;   // private body provider for the right-pane Skills sub-tab

    private string _selectedId = "";
    private string _newCompanion = "";
    private string _newNpc       = "";
    private string _nameBuf      = "";   // rename buffer, reseeded when the selection changes
    private string _nameForId    = "";
    private string? _pendingDelete;
    private string _importCode   = "";   // paste-buffer for importing a companion from an export code
    private bool   _importAsNpc;          // DM-only: import target kind (Companion vs NPC)
    private bool   _importPendingOpen;    // deferred open for the import modal
    private DateTime _exportMsgUntil;     // brief "Copied!" confirmation timer
    private string   _exportMsgId = "";   // entity the confirmation belongs to

    public RpNpcWindow(Plugin plugin) : base("RPNPC Vault##RPFramework.Npc",
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin = plugin;
        _skills = new SkillsWindow(plugin);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 460),
            MaximumSize = new Vector2(1100, 1000),
        };
        Size          = new Vector2(760, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() => _skills.Dispose();

    public override void Draw()
    {
        string? code = _plugin.ActiveCampaign;
        if (_plugin.LocalPlayerId == null || code == null)
        {
            ImGui.TextDisabled("Connect to a server to open the vault.");
            return;
        }

        _plugin.CharacterSheetWindow.DrawCampaignSelector(code);
        ImGui.Separator();

        float scale = ImGuiHelpers.GlobalScale;
        float leftW = 200 * scale;

        using (var left = ImRaii.Child("##npcleft", new Vector2(leftW, -1), true))
            if (left) DrawRoster(code);

        ImGui.SameLine();

        using var right = ImRaii.Child("##npcright", new Vector2(-1, -1), false);
        if (right) DrawSelected(code, scale);

        DrawDeleteConfirm(code);
        DrawImportModal(code, scale);
    }

    // ── Left: roster ─────────────────────────────────────────────────────────

    private void DrawRoster(string code)
    {
        string pid   = _plugin.LocalPlayerId!;
        bool   isDm  = _plugin.IsDm(code);
        var    activeId = _plugin.ActiveCompanion(code)?.EntityId;

        // Reserve room for the pinned Import button (separator + a full-width button row).
        float footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 2;
        using (var list = ImRaii.Child("##npcrosterlist", new Vector2(0, -footer), false))
        {
            if (list)
            {
                ImGui.TextDisabled("Companions");
                ImGui.Separator();
                foreach (var c in _plugin.Store.CompanionsOf(code, pid).OrderBy(c => c.DisplayName))
                    DrawRosterRow(c, c.EntityId == activeId);

                ImGui.Spacing();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##newcomp", "New companion name", ref _newCompanion, 64);
                using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_newCompanion)))
                    if (ImGui.Button("Create Companion##npc_addcomp", new Vector2(-1, 0)))
                    {
                        _ = _plugin.Network.EntityCreate(code, EntityKind.Companion, _newCompanion.Trim());
                        _newCompanion = "";
                    }

                if (isDm)
                {
                    ImGuiHelpers.ScaledDummy(8f);
                    ImGui.TextDisabled("NPCs (DM)");
                    ImGui.Separator();
                    foreach (var n in _plugin.Store.NpcsIn(code).OrderBy(n => n.DisplayName))
                        DrawRosterRow(n, false);

                    ImGui.Spacing();
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##newnpc", "New NPC name", ref _newNpc, 64);
                    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_newNpc)))
                        if (ImGui.Button("Create NPC##npc_addnpc", new Vector2(-1, 0)))
                        {
                            _ = _plugin.Network.EntityCreate(code, EntityKind.Npc, _newNpc.Trim());
                            _newNpc = "";
                        }
                }
            }
        }

        // Pinned at the bottom of the left panel: open the import modal.
        ImGui.Separator();
        if (ImGui.Button("Import##npc_import_open", new Vector2(-1, 0)))
        {
            _importCode        = "";
            _importAsNpc       = false;
            _importPendingOpen = true;
        }
    }

    private void DrawRosterRow(CharacterDto e, bool active)
    {
        bool selected = e.EntityId == _selectedId;
        string label = active ? $"★ {e.DisplayName}" : e.DisplayName;
        if (selected) ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.26f, 0.59f, 0.98f, 0.4f));
        if (ImGui.Selectable($"{label}##npc_{e.EntityId}", selected)) _selectedId = e.EntityId;
        if (selected) ImGui.PopStyleColor();
        if (active && ImGui.IsItemHovered()) ImGui.SetTooltip("Active companion");
    }

    // ── Right: selected entity sheet ─────────────────────────────────────────

    private void DrawSelected(string code, float scale)
    {
        var entity = _selectedId.Length > 0 ? _plugin.Store.Character(code, _selectedId) : null;
        if (entity == null)
        {
            ImGui.TextDisabled("Select a companion or NPC, or create one.");
            return;
        }

        // Name (rename) — reseed the buffer whenever the selection changes.
        if (_nameForId != entity.EntityId) { _nameBuf = entity.DisplayName; _nameForId = entity.EntityId; }
        ImGui.SetNextItemWidth(220 * scale);
        ImGui.InputText("##npc_name", ref _nameBuf, 64);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_nameBuf) || _nameBuf == entity.DisplayName))
            if (ImGui.Button("Rename##npc_rename"))
                _ = _plugin.Network.EntityRename(code, entity.EntityId, _nameBuf.Trim());

        ImGui.SameLine();
        ImGui.TextDisabled(entity.Kind == EntityKind.Npc ? "[NPC]" : "[Companion]");

        // Active-companion swap (companions only).
        if (entity.Kind == EntityKind.Companion)
        {
            bool isActive = _plugin.ActiveCompanion(code)?.EntityId == entity.EntityId;
            if (isActive)
            {
                ImGui.SameLine();
                if (ImGui.Button("Stow##npc_stow")) _plugin.SetActiveCompanion(code, null);
                ImGui.SameLine(); ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), "● Active");
            }
            else
            {
                ImGui.SameLine();
                if (ImGui.Button("Set Active##npc_active")) _plugin.SetActiveCompanion(code, entity.EntityId);
            }
        }

        // Export this entity as a shareable code (copied to clipboard) for import into another vault.
        ImGui.SameLine();
        if (ImGui.SmallButton("Export##npc_export"))
        {
            ImGui.SetClipboardText(CompanionCodec.Encode(entity.DisplayName, entity.State));
            _exportMsgUntil = DateTime.Now.AddSeconds(2.5);
            _exportMsgId    = entity.EntityId;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy a shareable code for this character to the clipboard.");
        if (_exportMsgId == entity.EntityId && DateTime.Now < _exportMsgUntil)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), "Copied!");
        }

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.85f, 0.45f, 0.45f, 1f)))
            if (ImGui.SmallButton("Delete##npc_del")) _pendingDelete = entity.EntityId;

        ImGui.Separator();

        using var tabs = ImRaii.TabBar("##npcsheettabs");
        if (!tabs) return;
        if (ImGui.BeginTabItem("Stats##npc_stats"))
        {
            using (var c = ImRaii.Child("##npc_statsbody", new Vector2(-1, -1), false))
                if (c) _plugin.CharacterSheetWindow.DrawStats(code, entity.EntityId);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Skills##npc_skills"))
        {
            using (var c = ImRaii.Child("##npc_skillsbody", new Vector2(-1, -1), false))
                if (c) _skills.DrawBody(code, entity.EntityId);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Profile##npc_profile"))
        {
            using (var c = ImRaii.Child("##npc_profilebody", new Vector2(-1, -1), false))
                if (c) _plugin.CharacterSheetWindow.DrawProfile(code, entity.EntityId, editable: true);
            ImGui.EndTabItem();
        }
    }

    private void DrawDeleteConfirm(string code)
    {
        if (_pendingDelete != null) ImGui.OpenPopup("##npc_delconfirm");
        ImGui.SetNextWindowSize(new Vector2(300 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        bool open = true;
        if (!ImGui.BeginPopupModal("##npc_delconfirm", ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        var entity = _pendingDelete != null ? _plugin.Store.Character(code, _pendingDelete) : null;
        ImGui.TextWrapped($"Delete \"{entity?.DisplayName ?? "this entity"}\"? This cannot be undone.");
        ImGui.Spacing();
        float bw = 90 * ImGuiHelpers.GlobalScale;
        if (ImGui.Button("Delete##npc_delyes", new Vector2(bw, 0)))
        {
            if (_pendingDelete != null)
            {
                _ = _plugin.Network.EntityDelete(code, _pendingDelete);
                if (_plugin.ActiveCompanion(code)?.EntityId == _pendingDelete) _plugin.SetActiveCompanion(code, null);
                if (_selectedId == _pendingDelete) _selectedId = "";
            }
            _pendingDelete = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##npc_delno", new Vector2(bw, 0))) { _pendingDelete = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    // ── Import modal ─────────────────────────────────────────────────────────

    /// <summary>Paste a character code and import it. Players always import as their own companion;
    /// DMs may pick Companion vs NPC. Opened by the Import button pinned under the roster.</summary>
    private void DrawImportModal(string code, float scale)
    {
        if (_importPendingOpen) { ImGui.OpenPopup("##npc_import"); _importPendingOpen = false; }

        ImGui.SetNextWindowSize(new Vector2(360 * scale, 0), ImGuiCond.Always);
        bool open = true;
        if (!ImGui.BeginPopupModal("##npc_import", ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted("Import Character");
        ImGui.Separator(); ImGui.Spacing();

        ImGui.TextUnformatted("Paste character code:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##importcode", "RPNPC1:...", ref _importCode, 16384);

        var importKind = EntityKind.Companion;
        if (_plugin.IsDm(code))
        {
            ImGui.Spacing();
            int sel = _importAsNpc ? 1 : 0;
            if (ImGui.RadioButton("As Companion##imp_kc", ref sel, 0)) _importAsNpc = false;
            ImGui.SameLine();
            if (ImGui.RadioButton("As NPC##imp_kn", ref sel, 1)) _importAsNpc = true;
            importKind = _importAsNpc ? EntityKind.Npc : EntityKind.Companion;
        }

        bool validImport = CompanionCodec.TryDecode(_importCode, out var preview);
        if (_importCode.Length > 0 && !validImport)
            ImGui.TextColored(new Vector4(0.85f, 0.45f, 0.45f, 1f), "Not a valid character code.");
        else if (validImport)
            ImGui.TextDisabled($"Will import \"{preview.DisplayName}\".");

        ImGui.Spacing();
        float bw = 100 * scale;
        using (ImRaii.Disabled(!validImport))
            if (ImGui.Button("Import##npc_import_do", new Vector2(bw, 0)))
            {
                _ = _plugin.Network.EntityImport(code, _importCode.Trim(), importKind);
                _importCode = "";
                ImGui.CloseCurrentPopup();
            }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##npc_import_cancel", new Vector2(bw, 0))) { _importCode = ""; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
