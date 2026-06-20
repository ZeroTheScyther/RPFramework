using System;
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
    private bool    _showSettings;
    private string  _npcNameBuf = "";

    public InitiativeWindow(Plugin plugin)
        : base("RP Initiative##RPFramework.Initiative",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 200),
            MaximumSize = new Vector2(800, 700),
        };
        Size          = new Vector2(560, 320);
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

        var activeCodes = _plugin.Store.Initiatives.Select(i => i.PartyCode).ToList();

        if (_code == null || (!activeCodes.Contains(_code) && activeCodes.Count > 0))
            _code = activeCodes.FirstOrDefault();
        _code ??= _plugin.ActiveCampaign;

        bool isDm = _plugin.IsDm(_code);

        if (_showSettings && _code != null)
        {
            DrawSettingsPanel(_code, isDm);
            ImGui.Separator();
        }

        if (activeCodes.Count > 1)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##ini_partypick", _plugin.Store.Party(_code)?.Name ?? _code))
            {
                foreach (var code in activeCodes)
                    if (ImGui.Selectable($"{_plugin.Store.Party(code)?.Name ?? code}##ini_pick_{code}", code == _code))
                        _code = code;
                ImGui.EndCombo();
            }
        }

        var state = _plugin.Store.Initiative(_code);
        if (_code == null || state == null) { DrawNoInitiativeView(pid); return; }

        DrawActiveInitiative(pid, state, isDm);
    }

    private void DrawNoInitiativeView(string pid)
    {
        ImGui.TextDisabled("No active initiative.");
        ImGuiHelpers.ScaledDummy(6f);

        bool anyAuthority = false;
        foreach (var party in _plugin.Store.Parties.Where(p => !p.IsPersonal))
        {
            if (!_plugin.IsDm(party.Code)) continue;
            anyAuthority = true;
            using var c  = ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.15f, 0.50f, 0.75f, 1f));
            using var ch = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.65f, 0.90f, 1f));
            if (ImGui.Button($"Start Initiative — {party.Name}##ini_start_{party.Code}", new Vector2(-1, 0)))
                _ = _plugin.Network.InitiativeStart(party.Code);
            ImGuiHelpers.ScaledDummy(2f);
        }

        if (!anyAuthority) ImGui.TextDisabled("Waiting for your DM to start initiative…");
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

    private void DrawActiveInitiative(string pid, InitiativeStateDto state, bool isDm)
    {
        float scale = ImGuiHelpers.GlobalScale;

        int  safeIdx  = state.Order.Count > 0 ? Math.Clamp(state.CurrentIndex, 0, state.Order.Count - 1) : 0;
        bool isMyTurn = state.Order.Count > 0 && state.Order[safeIdx].PlayerId == pid;

        ImGui.TextUnformatted(_plugin.Store.Party(state.PartyCode)?.Name ?? state.PartyCode);
        ImGui.SameLine();
        ImGui.TextDisabled($"— {state.Order.Count} combatant{(state.Order.Count == 1 ? "" : "s")}");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        bool showEndTurn = isMyTurn || isDm;
        int  btnCount    = (showEndTurn ? 1 : 0) + (isDm ? 2 : 0);
        float btnH       = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        float bottomH    = btnCount > 0 ? (btnCount * btnH + 4 * scale) : 2 * scale;

        using (var scroll = ImRaii.Child("##ini_list", new Vector2(-1, -bottomH), false, ImGuiWindowFlags.NoScrollbar))
        {
            if ((bool)scroll && state.Order.Count == 0)
                ImGui.TextDisabled("Waiting for rolls…");
            else if ((bool)scroll)
            {
                bool showHpAp = state.ShowHpAp;
                int  colCount = showHpAp ? 5 : 3;
                if (ImGui.BeginTable("##ini_t", colCount, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX))
                {
                    ImGui.TableSetupColumn("##ini_c0", ImGuiTableColumnFlags.WidthFixed,   24 * scale);
                    ImGui.TableSetupColumn("##ini_c1", ImGuiTableColumnFlags.WidthStretch, 1f);
                    if (showHpAp)
                    {
                        ImGui.TableSetupColumn("##ini_c2", ImGuiTableColumnFlags.WidthFixed, 56 * scale);
                        ImGui.TableSetupColumn("##ini_c3", ImGuiTableColumnFlags.WidthFixed, 56 * scale);
                    }
                    ImGui.TableSetupColumn("##ini_c4", ImGuiTableColumnFlags.WidthFixed, 80 * scale);

                    var drawList = ImGui.GetWindowDrawList();
                    for (int i = 0; i < state.Order.Count; i++)
                    {
                        var  entry         = state.Order[i];
                        bool isCurrent     = i == safeIdx;
                        bool isMe          = entry.PlayerId == pid;
                        bool incapacitated = (entry.HpMax > 0 && entry.HpCurrent <= 0) || (entry.ApMax > 0 && entry.ApCurrent <= 0);

                        ImGui.TableNextRow();
                        if (isCurrent)
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.45f, 0.65f, 0.30f)));

                        ImGui.TableSetColumnIndex(0);
                        if (isCurrent) ImGui.TextColored(new Vector4(1f, 0.85f, 0.1f, 1f), "▶");
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

                        if (entry.IsNpc && isDm && ImGui.BeginPopupContextItem($"##npc_ctx_{i}"))
                        {
                            if (ImGui.MenuItem($"Remove {entry.DisplayName}"))
                                _ = _plugin.Network.InitiativeRemove(state.PartyCode, entry.PlayerId);
                            ImGui.EndPopup();
                        }

                        if (showHpAp)
                        {
                            ImGui.TableSetColumnIndex(2);
                            if (entry.HpMax > 0)
                                ImGui.TextColored(entry.HpCurrent <= 0 ? new Vector4(0.80f, 0.20f, 0.20f, 1f) : new Vector4(0.30f, 0.80f, 0.30f, 1f), $"HP {entry.HpCurrent}/{entry.HpMax}");
                            else ImGui.TextDisabled("—");

                            ImGui.TableSetColumnIndex(3);
                            if (entry.ApMax > 0)
                                ImGui.TextColored(entry.ApCurrent <= 0 ? new Vector4(0.80f, 0.20f, 0.20f, 1f) : new Vector4(0.30f, 0.55f, 0.90f, 1f), $"AP {entry.ApCurrent}/{entry.ApMax}");
                            else ImGui.TextDisabled("—");
                        }

                        ImGui.TableSetColumnIndex(showHpAp ? 4 : 2);
                        string rollStr = entry.Bonus > 0 ? $"{entry.Total} ({entry.Roll}+{entry.Bonus})" : $"{entry.Total}";
                        if (isCurrent && isMe) ImGui.TextUnformatted(rollStr);
                        else                   ImGui.TextDisabled(rollStr);
                    }
                    ImGui.EndTable();
                }
            }
        }

        ImGuiHelpers.ScaledDummy(2f);

        if (showEndTurn)
        {
            using var g  = ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.18f, 0.55f, 0.18f, 1f));
            using var gh = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.70f, 0.25f, 1f));
            if (ImGui.Button("End Turn##ini_endturn", new Vector2(-1, 0)))
                _ = _plugin.Network.InitiativeEndTurn(state.PartyCode);
        }

        if (isDm)
        {
            ImGuiHelpers.ScaledDummy(2f);
            using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.65f, 0.15f, 0.15f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.20f, 0.20f, 1f)))
                if (ImGui.Button("End Combat##ini_endcombat", new Vector2(-1, 0)))
                    _ = _plugin.Network.InitiativeEndCombat(state.PartyCode);

            ImGuiHelpers.ScaledDummy(2f);
            float addBtnW = 80f * scale;
            ImGui.SetNextItemWidth(-addBtnW - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##ini_npcname", "NPC name…", ref _npcNameBuf, 64);
            ImGui.SameLine();
            bool canAdd = !string.IsNullOrWhiteSpace(_npcNameBuf);
            using (ImRaii.Disabled(!canAdd))
                if (ImGui.Button("Add NPC##ini_addnpc", new Vector2(addBtnW, 0)) && canAdd)
                {
                    _ = _plugin.Network.InitiativeAddNpc(state.PartyCode, _npcNameBuf.Trim(), Random.Shared.Next(1, 25), 0, 0, 0);
                    _npcNameBuf = "";
                }
        }
    }
}
