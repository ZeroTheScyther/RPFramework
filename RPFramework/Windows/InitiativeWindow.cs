using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

public class InitiativeWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private string? _code;
    private string? _encounterId;
    private bool    _showSettings;
    private string  _newEncBuf = "";
    private string  _npcNameBuf = "";

    public InitiativeWindow(Plugin plugin)
        : base("RP Initiative##RPFramework.Initiative",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 240),
            MaximumSize = new Vector2(800, 800),
        };
        Size          = new Vector2(560, 380);
        SizeCondition = ImGuiCond.FirstUseEver;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon        = FontAwesomeIcon.Cog,
            ShowTooltip = () => ImGui.SetTooltip("Initiative settings"),
            Click       = _ => _showSettings = !_showSettings,
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        string? pid = _plugin.LocalPlayerId;
        if (pid == null) { ImGui.TextDisabled("Log in to use initiative."); return; }

        var parties = _plugin.Store.Parties.Where(p => !p.IsPersonal).OrderBy(p => p.Name).ToList();
        if (parties.Count == 0) { ImGui.TextDisabled("Join a campaign to run encounters."); return; }

        if (_code == null || parties.All(p => p.Code != _code))
            _code = _plugin.ActiveCampaign != null && parties.Any(p => p.Code == _plugin.ActiveCampaign)
                  ? _plugin.ActiveCampaign : parties[0].Code;

        bool isDm = _plugin.IsDm(_code);

        if (_showSettings)
        {
            DrawSettingsPanel(_code!, isDm);
            ImGui.Separator();
        }

        // Campaign picker (only when the player has more than one campaign).
        if (parties.Count > 1)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##ini_partypick", _plugin.Store.Party(_code)?.Name ?? _code))
            {
                foreach (var p in parties)
                    if (ImGui.Selectable($"{p.Name}##ini_pick_{p.Code}", p.Code == _code))
                    { _code = p.Code; _encounterId = null; }
                ImGui.EndCombo();
            }
        }

        var encounters = _plugin.Store.EncountersIn(_code);

        // Keep the selected encounter valid.
        if (_encounterId == null || encounters.All(e => e.EncounterId != _encounterId))
            _encounterId = encounters.Count > 0 ? encounters[0].EncounterId : null;

        DrawEncounterBar(_code!, encounters, isDm);

        var enc = encounters.FirstOrDefault(e => e.EncounterId == _encounterId);
        if (enc == null)
        {
            ImGuiHelpers.ScaledDummy(6f);
            ImGui.TextDisabled(isDm ? "No encounters yet — create one above." : "No active encounters. Waiting for your DM…");
            return;
        }

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);
        DrawEncounter(pid, enc, isDm);
    }

    private void DrawEncounterBar(string code, IReadOnlyList<EncounterDto> encounters, bool isDm)
    {
        float scale = ImGuiHelpers.GlobalScale;

        if (encounters.Count > 0)
        {
            float delW = isDm ? 28f * scale + ImGui.GetStyle().ItemSpacing.X : 0f;
            ImGui.SetNextItemWidth(delW > 0 ? -delW : -1);
            var cur = encounters.FirstOrDefault(e => e.EncounterId == _encounterId);
            if (ImGui.BeginCombo("##ini_encpick", cur?.Name ?? "Encounter"))
            {
                foreach (var e in encounters)
                    if (ImGui.Selectable($"{e.Name} ({e.Order.Count})##ini_enc_{e.EncounterId}", e.EncounterId == _encounterId))
                        _encounterId = e.EncounterId;
                ImGui.EndCombo();
            }

            if (isDm)
            {
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.55f, 0.13f, 0.13f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.18f, 0.18f, 1f)))
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    if (cur != null && ImGui.Button($"{FontAwesomeIcon.Trash.ToIconString()}##ini_delenc", new Vector2(28f * scale, 0)))
                        _ = _plugin.Network.EncounterDelete(code, cur.EncounterId);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this encounter");
            }
        }

        if (isDm)
        {
            float addW = 70f * scale;
            ImGui.SetNextItemWidth(-addW - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##ini_newenc", "New encounter name…", ref _newEncBuf, 48);
            ImGui.SameLine();
            bool canCreate = !string.IsNullOrWhiteSpace(_newEncBuf);
            using (ImRaii.Disabled(!canCreate))
            using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.15f, 0.50f, 0.75f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.65f, 0.90f, 1f)))
                if (ImGui.Button("Create##ini_createenc", new Vector2(addW, 0)) && canCreate)
                {
                    _ = _plugin.Network.EncounterCreate(code, _newEncBuf.Trim());
                    _newEncBuf = "";
                }
        }
    }

    private void DrawSettingsPanel(string code, bool isDm)
    {
        float scale = ImGuiHelpers.GlobalScale;
        ImGuiHelpers.ScaledDummy(2f);
        using var indent = ImRaii.PushIndent(4f * scale, false);
        ImGui.TextDisabled("Settings");
        ImGuiHelpers.ScaledDummy(2f);

        bool showHpAp = _plugin.Store.Party(code)?.ShowHpAp ?? true;
        using (ImRaii.Disabled(!isDm))
            if (ImGui.Checkbox("Show HP / AP##ini_showhpap", ref showHpAp) && isDm)
                _ = _plugin.Network.PartySetShowHpAp(code, showHpAp);
        if (!isDm && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Only the DM can change this setting.");
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawEncounter(string pid, EncounterDto enc, bool isDm)
    {
        float scale = ImGuiHelpers.GlobalScale;

        int  safeIdx  = enc.Order.Count > 0 ? Math.Clamp(enc.CurrentIndex, 0, enc.Order.Count - 1) : 0;
        bool inEnc    = enc.Order.Any(e => e.EntityId == pid);
        var  current  = enc.Order.Count > 0 ? enc.Order[safeIdx] : null;
        bool myTurn   = current != null && CanControl(enc.CampaignCode, current.EntityId, pid, isDm);

        // Build the "add combatant" roster (entities not already in the encounter).
        var addList = BuildAddList(enc, pid, isDm);

        // Bottom button rows: Join/Leave, End Turn, Add combatant, Add NPC.
        int rows = 1;                       // Join / Leave (always available to players)
        if (myTurn) rows++;                 // End Turn
        if (addList.Count > 0) rows++;      // Add combatant combo
        if (isDm) rows++;                   // Add NPC
        float rowH    = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        float bottomH = rows * rowH + 6 * scale;

        using (var scroll = ImRaii.Child("##ini_list", new Vector2(-1, -bottomH), false, ImGuiWindowFlags.NoScrollbar))
        {
            if ((bool)scroll && enc.Order.Count == 0)
                ImGui.TextDisabled("No combatants yet. Join or add some below.");
            else if ((bool)scroll)
                DrawTable(pid, enc, safeIdx, isDm, scale);
        }

        ImGuiHelpers.ScaledDummy(2f);

        // Join / Leave (player controls their own PC's presence in this encounter).
        if (inEnc)
        {
            using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.55f, 0.40f, 0.13f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.55f, 0.18f, 1f)))
                if (ImGui.Button("Leave Encounter##ini_leave", new Vector2(-1, 0)))
                    _ = _plugin.Network.EncounterLeave(enc.CampaignCode, enc.EncounterId);
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.15f, 0.50f, 0.75f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.65f, 0.90f, 1f)))
                if (ImGui.Button("Join Encounter##ini_join", new Vector2(-1, 0)))
                    _ = _plugin.Network.EncounterJoin(enc.CampaignCode, enc.EncounterId);
        }

        if (myTurn)
        {
            using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.18f, 0.55f, 0.18f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.70f, 0.25f, 1f)))
                if (ImGui.Button("End Turn##ini_endturn", new Vector2(-1, 0)))
                    _ = _plugin.Network.EncounterEndTurn(enc.CampaignCode, enc.EncounterId);
        }

        if (addList.Count > 0)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##ini_addcombatant", "+ Add combatant…"))
            {
                foreach (var (id, label) in addList)
                    if (ImGui.Selectable($"{label}##ini_add_{id}"))
                        _ = _plugin.Network.EncounterAddEntity(enc.CampaignCode, enc.EncounterId, id);
                ImGui.EndCombo();
            }
        }

        if (isDm)
        {
            float addBtnW = 80f * scale;
            ImGui.SetNextItemWidth(-addBtnW - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##ini_npcname", "Ad-hoc NPC name…", ref _npcNameBuf, 64);
            ImGui.SameLine();
            bool canAdd = !string.IsNullOrWhiteSpace(_npcNameBuf);
            using (ImRaii.Disabled(!canAdd))
                if (ImGui.Button("Add NPC##ini_addnpc", new Vector2(addBtnW, 0)) && canAdd)
                {
                    _ = _plugin.Network.EncounterAddNpc(enc.CampaignCode, enc.EncounterId, _npcNameBuf.Trim(),
                                                        Random.Shared.Next(1, 25), 0, 0, 0);
                    _npcNameBuf = "";
                }
        }
    }

    private void DrawTable(string pid, EncounterDto enc, int safeIdx, bool isDm, float scale)
    {
        bool showHpAp = enc.ShowHpAp;
        int  colCount = showHpAp ? 5 : 3;
        if (!ImGui.BeginTable("##ini_t", colCount, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("##ini_c0", ImGuiTableColumnFlags.WidthFixed,   24 * scale);
        ImGui.TableSetupColumn("##ini_c1", ImGuiTableColumnFlags.WidthStretch, 1f);
        if (showHpAp)
        {
            // Wide enough for three-digit pools ("AP 100/100") so the value isn't clipped by the roll column.
            ImGui.TableSetupColumn("##ini_c2", ImGuiTableColumnFlags.WidthFixed, 76 * scale);
            ImGui.TableSetupColumn("##ini_c3", ImGuiTableColumnFlags.WidthFixed, 76 * scale);
        }
        ImGui.TableSetupColumn("##ini_c4", ImGuiTableColumnFlags.WidthFixed, 80 * scale);

        var drawList = ImGui.GetWindowDrawList();
        for (int i = 0; i < enc.Order.Count; i++)
        {
            var  entry         = enc.Order[i];
            bool isCurrent     = i == safeIdx;
            bool isMe          = entry.EntityId == pid;
            bool incapacitated = (entry.HpMax > 0 && entry.HpCurrent <= 0) || (entry.ApMax > 0 && entry.ApCurrent <= 0);

            ImGui.TableNextRow();
            if (isCurrent)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.45f, 0.65f, 0.30f)));

            ImGui.TableSetColumnIndex(0);
            if (isCurrent) ImGui.TextColored(new Vector4(1f, 0.85f, 0.1f, 1f), ">");
            else           ImGui.TextDisabled($"{i + 1}.");

            ImGui.TableSetColumnIndex(1);
            var nameColor = incapacitated ? new Vector4(0.55f, 0.55f, 0.55f, 1f)
                          : isCurrent      ? new Vector4(1f, 1f, 1f, 1f)
                                           : new Vector4(0.60f, 0.60f, 0.60f, 1f);
            if (entry.IsNpc) { ImGui.TextColored(new Vector4(0.85f, 0.65f, 0.30f, 1f), "[NPC] "); ImGui.SameLine(0f, 2f); }
            ImGui.TextColored(nameColor, entry.DisplayName);
            if (incapacitated)
            {
                var rMin = ImGui.GetItemRectMin(); var rMax = ImGui.GetItemRectMax();
                float midY = (rMin.Y + rMax.Y) * 0.5f;
                drawList.AddLine(new Vector2(rMin.X, midY), new Vector2(rMax.X, midY),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.80f, 0.20f, 0.20f, 0.85f)), 1.5f * scale);
            }

            if (isDm && ImGui.BeginPopupContextItem($"##ini_ctx_{i}"))
            {
                if (ImGui.MenuItem($"Remove {entry.DisplayName}"))
                    _ = _plugin.Network.EncounterRemove(enc.CampaignCode, enc.EncounterId, entry.EntityId);
                ImGui.EndPopup();
            }

            if (showHpAp)
            {
                ImGui.TableSetColumnIndex(2);
                if (entry.HpMax > 0)
                    ImGui.TextColored(entry.HpCurrent <= 0 ? new Vector4(0.80f, 0.20f, 0.20f, 1f) : new Vector4(0.30f, 0.80f, 0.30f, 1f), $"HP {entry.HpCurrent}/{entry.HpMax}");
                else ImGui.TextDisabled("-");

                ImGui.TableSetColumnIndex(3);
                if (entry.ApMax > 0)
                    ImGui.TextColored(entry.ApCurrent <= 0 ? new Vector4(0.80f, 0.20f, 0.20f, 1f) : new Vector4(0.30f, 0.55f, 0.90f, 1f), $"AP {entry.ApCurrent}/{entry.ApMax}");
                else ImGui.TextDisabled("-");
            }

            ImGui.TableSetColumnIndex(showHpAp ? 4 : 2);
            string rollStr = entry.Bonus > 0 ? $"{entry.Total} ({entry.Roll}+{entry.Bonus})" : $"{entry.Total}";
            if (isCurrent && isMe) ImGui.TextUnformatted(rollStr);
            else                   ImGui.TextDisabled(rollStr);
        }
        ImGui.EndTable();
    }

    /// <summary>True if the player may end the given entity's turn (own PC, own companion, or DM).</summary>
    private bool CanControl(string code, string entityId, string pid, bool isDm)
    {
        if (isDm || entityId == pid) return true;
        var ch = _plugin.Store.Character(code, entityId);
        return ch != null && ch.Kind == EntityKind.Companion && ch.OwnerPlayerId == pid;
    }

    /// <summary>Combatants the caller may add that are not already in the encounter.</summary>
    private List<(string Id, string Label)> BuildAddList(EncounterDto enc, string pid, bool isDm)
    {
        var present = enc.Order.Select(e => e.EntityId).ToHashSet();
        var list = new List<(string, string)>();
        string code = enc.CampaignCode;

        // The caller's own companions.
        foreach (var c in _plugin.Store.CompanionsOf(code, pid).OrderBy(c => c.DisplayName))
            if (!present.Contains(c.EntityId)) list.Add((c.EntityId, c.DisplayName));

        if (isDm)
        {
            foreach (var c in _plugin.Store.CharactersIn(code)
                                          .Where(c => c.Kind == EntityKind.PlayerCharacter)
                                          .OrderBy(c => c.DisplayName))
                if (!present.Contains(c.EntityId)) list.Add((c.EntityId, c.DisplayName));
            foreach (var n in _plugin.Store.NpcsIn(code).OrderBy(n => n.DisplayName))
                if (!present.Contains(n.EntityId)) list.Add((n.EntityId, $"{n.DisplayName} (NPC)"));
        }

        return list;
    }
}
