using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models.Net;

namespace RPFramework.Windows;

public class InitiativeWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private string? _partyCode; // which party's initiative is currently displayed

    public InitiativeWindow(Plugin plugin)
        : base("RP Initiative##RPFramework.Initiative",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 200),
            MaximumSize = new Vector2(640, 700),
        };
        Size          = new Vector2(380, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        string? pid = _plugin.LocalPlayerId;
        if (pid == null)
        {
            ImGui.TextDisabled("Log in to use initiative.");
            return;
        }

        var states      = _plugin.InitiativeStates;
        var activeCodes = states.Keys.ToList();

        // Pick a valid displayed party
        if (_partyCode == null || !activeCodes.Contains(_partyCode))
            _partyCode = activeCodes.FirstOrDefault();

        if (_partyCode == null || !states.TryGetValue(_partyCode, out var state))
        {
            DrawNoInitiativeView(pid);
            return;
        }

        // If the player is in multiple parties that each have active initiative, show a dropdown
        if (activeCodes.Count > 1)
        {
            var selected = _plugin.Configuration.Parties.Find(p => p.Code == _partyCode);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##ini_partypick", selected?.Name ?? _partyCode))
            {
                foreach (var code in activeCodes)
                {
                    var p   = _plugin.Configuration.Parties.Find(x => x.Code == code);
                    string lbl = p?.Name ?? code;
                    if (ImGui.Selectable($"{lbl}##ini_pick_{code}", code == _partyCode))
                        _partyCode = code;
                    if (code == _partyCode)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            // Re-fetch in case dropdown changed _partyCode
            if (!states.TryGetValue(_partyCode, out state))
            {
                DrawNoInitiativeView(pid);
                return;
            }
        }

        DrawActiveInitiative(pid, state);
    }

    // ── No active initiative ──────────────────────────────────────────────────

    private void DrawNoInitiativeView(string pid)
    {
        ImGui.TextDisabled("No active initiative.");
        ImGuiHelpers.ScaledDummy(6f);

        bool anyAuthority = false;
        foreach (var party in _plugin.Configuration.Parties)
        {
            if (!_plugin.PartyMembers.TryGetValue(party.Code, out var members)) continue;
            var me = members.FirstOrDefault(m => m.PlayerId == pid);
            if (me?.Role is not (PartyRole.Owner or PartyRole.CoDm)) continue;

            anyAuthority = true;
            using var btnColor  = ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.15f, 0.50f, 0.75f, 1f));
            using var btnColorH = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.65f, 0.90f, 1f));
            if (ImGui.Button($"Start Initiative — {party.Name}##ini_start_{party.Code}",
                    new Vector2(-1, 0)))
            {
                string code = party.Code;
                Task.Run(() => _plugin.Network.PartyStartInitiativeAsync(code));
            }
            ImGuiHelpers.ScaledDummy(2f);
        }

        if (!anyAuthority)
            ImGui.TextDisabled("Waiting for your DM to start initiative…");
    }

    // ── Active initiative ─────────────────────────────────────────────────────

    private void DrawActiveInitiative(string pid, InitiativeStateDto state)
    {
        float scale = ImGuiHelpers.GlobalScale;

        // Determine local player's role in this party
        bool isDmOrCoDm = false;
        if (_plugin.PartyMembers.TryGetValue(state.PartyCode, out var members))
        {
            var me = members.FirstOrDefault(m => m.PlayerId == pid);
            isDmOrCoDm = me?.Role is PartyRole.Owner or PartyRole.CoDm;
        }

        // Is it the local player's turn right now?
        int safeIdx   = state.Order.Count > 0
            ? Math.Clamp(state.CurrentIndex, 0, state.Order.Count - 1)
            : 0;
        bool isMyTurn = state.Order.Count > 0 && state.Order[safeIdx].PlayerId == pid;

        // Header
        var partyConf = _plugin.Configuration.Parties.Find(p => p.Code == state.PartyCode);
        string partyName = partyConf?.Name ?? state.PartyCode;

        ImGui.TextUnformatted(partyName);
        ImGui.SameLine();
        ImGui.TextDisabled(
            $"— {state.Order.Count} combatant{(state.Order.Count == 1 ? "" : "s")}");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // Calculate bottom reservation for buttons
        bool showEndTurn   = isMyTurn || isDmOrCoDm;
        bool showEndCombat = isDmOrCoDm;
        int  btnCount      = (showEndTurn ? 1 : 0) + (showEndCombat ? 1 : 0);
        float btnH         = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        float bottomH      = btnCount > 0 ? (btnCount * btnH + 4 * scale) : 2 * scale;

        // Scrollable initiative list
        using (var scroll = ImRaii.Child("##ini_list", new Vector2(-1, -bottomH), false,
                   ImGuiWindowFlags.NoScrollbar))
        {
            if (scroll)
            {
                if (state.Order.Count == 0)
                {
                    ImGui.TextDisabled("Waiting for rolls…");
                }
                else if (ImGui.BeginTable("##ini_t", 3,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX))
                {
                    ImGui.TableSetupColumn("##ini_c0", ImGuiTableColumnFlags.WidthFixed,   24 * scale);
                    ImGui.TableSetupColumn("##ini_c1", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableSetupColumn("##ini_c2", ImGuiTableColumnFlags.WidthFixed,  112 * scale);

                    for (int i = 0; i < state.Order.Count; i++)
                    {
                        var  entry     = state.Order[i];
                        bool isCurrent = i == safeIdx;
                        bool isMe      = entry.PlayerId == pid;

                        ImGui.TableNextRow();

                        if (isCurrent)
                        {
                            uint rowColor = ImGui.ColorConvertFloat4ToU32(
                                new Vector4(0.25f, 0.45f, 0.65f, 0.30f));
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowColor);
                        }

                        // Column 0: turn indicator / rank
                        ImGui.TableSetColumnIndex(0);
                        if (isCurrent)
                            ImGui.TextColored(new Vector4(1f, 0.85f, 0.1f, 1f), "▶");
                        else
                            ImGui.TextDisabled($"{i + 1}.");

                        // Column 1: display name
                        ImGui.TableSetColumnIndex(1);
                        if (isCurrent)
                            ImGui.TextUnformatted(entry.DisplayName);
                        else
                            ImGui.TextDisabled(entry.DisplayName);

                        // Column 2: roll score
                        ImGui.TableSetColumnIndex(2);
                        string rollStr = entry.SpdBonus > 0
                            ? $"{entry.Total} ({entry.Roll}+{entry.SpdBonus})"
                            : $"{entry.Total}";
                        if (isCurrent && isMe)
                            ImGui.TextUnformatted(rollStr);
                        else
                            ImGui.TextDisabled(rollStr);
                    }

                    ImGui.EndTable();
                }
            }
        }

        // ── Bottom buttons ────────────────────────────────────────────────────

        ImGuiHelpers.ScaledDummy(2f);

        if (showEndTurn)
        {
            using var g  = ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.18f, 0.55f, 0.18f, 1f));
            using var gh = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.70f, 0.25f, 1f));
            if (ImGui.Button("End Turn##ini_endturn", new Vector2(-1, 0)))
            {
                // Only tick own cooldowns/durations if it's actually our turn
                if (isMyTurn)
                {
                    var ch    = _plugin.GetOrCreateCharacter(pid);
                    bool dirty = false;
                    foreach (var s in ch.Skills)
                    {
                        if (s.CooldownRemaining  > 0) { s.CooldownRemaining--;  dirty = true; }
                        if (s.DurationRemaining > 0) { s.DurationRemaining--; dirty = true; }
                    }
                    if (dirty) _plugin.Configuration.Save();
                }
                string partyCode = state.PartyCode;
                Task.Run(() => _plugin.Network.PartyEndTurnAsync(partyCode));
            }
        }

        if (showEndCombat)
        {
            ImGuiHelpers.ScaledDummy(2f);
            using var r  = ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.65f, 0.15f, 0.15f, 1f));
            using var rh = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.20f, 0.20f, 1f));
            if (ImGui.Button("End Combat##ini_endcombat", new Vector2(-1, 0)))
            {
                string partyCode = state.PartyCode;
                Task.Run(() => _plugin.Network.PartyEndCombatAsync(partyCode));
            }
        }
    }
}
