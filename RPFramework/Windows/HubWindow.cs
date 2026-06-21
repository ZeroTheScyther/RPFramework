using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using RPFramework.Contracts;

namespace RPFramework.Windows;

public class HubWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private readonly HashSet<string> _openParties = new();
    private readonly Dictionary<string, bool> _hovered = new();
    private string _searchQuery = string.Empty;

    // Deferred flyout
    private string? _flyoutKind, _flyoutCode, _flyoutPlayer;
    private Vector2 _flyoutPos;
    private bool    _flyoutPending;

    // Modals
    private string _createName = "", _createPw = "";
    private bool   _createPending, _createOpen;
    private string _joinCode = "", _joinPw = "";
    private bool   _joinPending, _joinOpen;
    private string? _pendingLeaveCode;

    public HubWindow(Plugin plugin) : base("RP Hub##RPFramework.Hub")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 340), MaximumSize = new Vector2(440, 900) };
        Size          = new Vector2(440, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags         = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        TitleBarButtons.Add(new TitleBarButton { Icon = FontAwesomeIcon.Cog, ShowTooltip = () => ImGui.SetTooltip("Open Settings"), Click = _ => plugin.SettingsWindow.IsOpen = true });
        TitleBarButtons.Add(new TitleBarButton { Icon = FontAwesomeIcon.Heart, IconColor = new Vector4(0.93f, 0.25f, 0.25f, 1f), ShowTooltip = () => ImGui.SetTooltip("Help me keep the server running."), Click = _ => Util.OpenLink("https://ko-fi.com/zerothescyther") });
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (_createPending) { _createOpen = true; ImGui.OpenPopup("##hub_create"); _createPending = false; }
        if (_joinPending)   { _joinOpen   = true; ImGui.OpenPopup("##hub_join");   _joinPending   = false; }

        DrawIdentityHeader();
        ImGui.Separator();

        // Disconnected: hide the campaign UI entirely and offer a single centered Connect action.
        if (!plugin.Network.IsConnected)
        {
            DrawConnectPrompt();
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##hubsearch", "Search campaigns…", ref _searchQuery, 128);
        ImGuiHelpers.ScaledDummy(2f);

        ImGui.BeginChild("##hubscroll", new Vector2(-1, -1), false);
        DrawPartiesSection();
        ImGui.EndChild();

        DrawFlyout();
        DrawCreatePartyPopup();
        DrawJoinPartyPopup();
        DrawLeaveConfirmPopup();
    }

    /// <summary>The disconnected state: a single Connect button centered in the body, with the identity
    /// header above still showing connection status. No campaign UI is drawn while offline.</summary>
    private void DrawConnectPrompt()
    {
        float cw    = ContentWidth();
        float avail = ImGui.GetContentRegionAvail().Y;
        float btnW  = 150 * ImGuiHelpers.GlobalScale;
        float btnH  = ImGui.GetFrameHeight() * 1.4f;
        float lineH = ImGui.GetTextLineHeightWithSpacing();

        // Vertically center the hint + button block in the remaining space.
        float blockH = lineH + ImGuiHelpers.GlobalScale * 8f + btnH;
        if (avail > blockH) ImGui.Dummy(new Vector2(0, (avail - blockH) * 0.5f));

        const string hint = "Not connected to the relay.";
        float hintW = ImGui.CalcTextSize(hint).X;
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + (cw - hintW) * 0.5f);
        ImGui.TextDisabled(hint);
        ImGuiHelpers.ScaledDummy(8f);

        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + (cw - btnW) * 0.5f);
        if (ImGui.Button("Connect##hubconn_center", new Vector2(btnW, btnH))) plugin.Connect();
    }

    private void DrawIdentityHeader()
    {
        bool    connected = plugin.Network.IsConnected;
        string? pid       = plugin.LocalPlayerId;
        float   cw        = ContentWidth();

        if (pid != null)
        {
            var   pidColor = connected ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey3;
            float textW    = ImGui.CalcTextSize(pid).X;
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + (cw - textW) * 0.5f);
            ImGui.TextColored(pidColor, pid);
            if (ImGui.IsItemClicked()) ImGui.SetClipboardText(pid);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy your ID");
        }
        else ImGui.TextDisabled("(Log in to see your ID)");

        ImGui.AlignTextToFramePadding();
        DrawIconColored(FontAwesomeIcon.Circle, connected ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(connected ? "Connected to relay" : "Not connected  (/rpsettings to configure)");

        // While connected the header carries the Disconnect button; while disconnected the only action is
        // the big centered Connect button (see DrawConnectPrompt), so the header stays status-only.
        if (connected)
        {
            float btnW = 90 * ImGuiHelpers.GlobalScale;
            ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + cw - btnW);
            if (ImGui.Button("Disconnect##hubdisc", new Vector2(btnW, 0))) Task.Run(() => plugin.Network.DisconnectAsync());
        }
    }

    private void DrawPartiesSection()
    {
        float cw = ContentWidth(), menuBtnW = ImGui.GetFrameHeight();

        ImGui.AlignTextToFramePadding(); DrawIcon(FontAwesomeIcon.Users); ImGui.SameLine();
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("Campaigns");

        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + cw - menuBtnW);
        DrawIconButton(FontAwesomeIcon.Bars, "##partiesmenubtn");
        if (ImGui.IsItemClicked()) { _flyoutKind = "partiesmenu"; _flyoutCode = _flyoutPlayer = null; _flyoutPos = ImGui.GetItemRectMax(); _flyoutPending = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Create or join a campaign");
        ImGui.Separator();

        var parties = plugin.Store.Parties.Where(p => !p.IsPersonal)
            .Where(p => string.IsNullOrWhiteSpace(_searchQuery)
                     || p.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
                     || p.Code.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name).ToList();

        if (parties.Count == 0)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + 6f * ImGuiHelpers.GlobalScale);
            ImGui.TextDisabled("No campaigns. Use the menu above to create or join one.");
            return;
        }
        foreach (var party in parties) DrawPartyRow(party);
    }

    private void DrawPartyRow(PartyDto party)
    {
        string? localPid    = plugin.LocalPlayerId;
        int     onlineCount = party.Members.Count(m => m.Online);
        int     totalCount  = party.Members.Count;
        bool    isOpen      = _openParties.Contains(party.Code);
        string  rowId       = "pr_" + party.Code;
        float   rowW        = ContentWidth();

        bool hovered = _hovered.GetValueOrDefault(rowId);
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), hovered);
        using (ImRaii.Child(rowId, new Vector2(rowW, ImGui.GetFrameHeight()), false))
        {
            ImGui.AlignTextToFramePadding();
            DrawIcon(isOpen ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight);
            if (ImGui.IsItemClicked()) { if (isOpen) _openParties.Remove(party.Code); else _openParties.Add(party.Code); }
            ImGui.SameLine();

            var myRole = party.Members.FirstOrDefault(m => m.PlayerId == localPid)?.Role ?? PartyRole.Member;
            DrawIconColored(RoleIcon(myRole), RoleColor(myRole));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(RoleLabel(myRole));
            ImGui.SameLine();
            float leftEnd = ImGui.GetCursorPosX();

            float ellipsisW = ImGui.GetFrameHeight(), windowEndX = ImGui.GetWindowContentRegionMax().X;
            ImGui.SameLine(windowEndX - ellipsisW);
            DrawIconButton(FontAwesomeIcon.EllipsisV, "##pe_" + party.Code);
            if (ImGui.IsItemClicked()) { _flyoutKind = "party"; _flyoutCode = party.Code; _flyoutPlayer = null; _flyoutPos = ImGui.GetItemRectMax(); _flyoutPending = true; }

            bool   inCombat  = plugin.Store.Initiative(party.Code) != null;
            bool   isActive  = plugin.ActiveCampaign == party.Code;
            string combatTag = "  In Combat";
            float  combatW   = inCombat ? (ImGui.CalcTextSize(combatTag).X + ImGui.GetStyle().ItemSpacing.X) : 0f;

            ImGui.SameLine(leftEnd);
            float maxW = windowEndX - ellipsisW - ImGui.GetStyle().ItemSpacing.X - leftEnd - combatW;
            string countLabel = totalCount > onlineCount ? $"{party.Name}  [{onlineCount}/{totalCount}]" : $"{party.Name}  [{onlineCount}]";
            if (isActive) ImGui.TextColored(new Vector4(1f, 0.82f, 0.2f, 1f), Truncate(countLabel, maxW));
            else          ImGui.TextUnformatted(Truncate(countLabel, maxW));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{party.Code}   {onlineCount}/{totalCount} online{(isActive ? "  (active)" : "")}");

            if (inCombat)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.90f, 0.25f, 0.25f, 1f), combatTag);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Initiative is active");
            }
        }
        _hovered[rowId] = ImGui.IsItemHovered();

        if (isOpen)
        {
            using var indent = ImRaii.PushIndent(16f * ImGuiHelpers.GlobalScale, false);
            DrawPartyMembers(party);
            ImGui.Separator();
        }
    }

    private void DrawPartyMembers(PartyDto party)
    {
        string? localId = plugin.LocalPlayerId;
        float   memberW = ImGui.GetContentRegionAvail().X;

        var visible = string.IsNullOrWhiteSpace(_searchQuery)
            ? party.Members
            : party.Members.Where(m => m.DisplayName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
                                    || m.PlayerId.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var member in visible)
        {
            bool   isMe   = member.PlayerId == localId;
            bool   online = isMe || member.Online;
            string rowId  = "mr_" + party.Code + "_" + member.PlayerId;

            bool hovered = _hovered.GetValueOrDefault(rowId);
            using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), hovered);
            using (ImRaii.Child(rowId, new Vector2(memberW, ImGui.GetFrameHeight()), false))
            {
                ImGui.AlignTextToFramePadding();
                DrawIconColored(MemberIcon(member.Role, online), MemberColor(member.Role, isMe, online));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(MemberTooltip(member.Role, isMe, online));
                ImGui.SameLine();
                float leftEnd = ImGui.GetCursorPosX();

                float ellipsisW = ImGui.GetFrameHeight(), windowEndX = ImGui.GetWindowContentRegionMax().X;
                ImGui.SameLine(windowEndX - ellipsisW);
                DrawIconButton(FontAwesomeIcon.EllipsisV, "##me_" + party.Code + "_" + member.PlayerId);
                if (ImGui.IsItemClicked()) { _flyoutKind = "member"; _flyoutCode = party.Code; _flyoutPlayer = member.PlayerId; _flyoutPos = ImGui.GetItemRectMax(); _flyoutPending = true; }

                ImGui.SameLine(leftEnd);
                float maxW = windowEndX - ellipsisW - ImGui.GetStyle().ItemSpacing.X - leftEnd;
                string dispName = isMe ? $"{member.DisplayName} (You)" : member.DisplayName;
                using (ImRaii.PushColor(ImGuiCol.Text, isMe ? new Vector4(0.55f, 0.88f, 1f, 1f) : Vector4.One))
                    ImGui.TextUnformatted(Truncate(dispName, maxW));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(member.PlayerId);
            }
            _hovered[rowId] = ImGui.IsItemHovered();
        }
    }

    private void DrawFlyout()
    {
        if (_flyoutPending)
        {
            ImGui.SetNextWindowPos(_flyoutPos, ImGuiCond.Always, new Vector2(1f, 0f));
            ImGui.OpenPopup("##hub_flyout");
            _flyoutPending = false;
        }
        if (!ImGui.BeginPopup("##hub_flyout")) return;

        switch (_flyoutKind)
        {
            case "partiesmenu":
            {
                using (ImRaii.Disabled(!plugin.Network.IsConnected))
                {
                    if (MenuButton(FontAwesomeIcon.Plus, "Create new campaign", "pm_create")) { _createName = _createPw = ""; _createPending = true; ImGui.CloseCurrentPopup(); }
                    if (MenuButton(FontAwesomeIcon.SignInAlt, "Join a campaign", "pm_join")) { _joinCode = _joinPw = ""; _joinPending = true; ImGui.CloseCurrentPopup(); }
                }
                break;
            }
            case "party":
            {
                var party = plugin.Store.Party(_flyoutCode);
                if (party == null) { ImGui.CloseCurrentPopup(); break; }
                ImGui.TextDisabled(party.Name);
                ImGui.TextDisabled(party.Code);
                ImGui.Separator();

                bool alreadyActive = plugin.ActiveCampaign == party.Code;
                using (ImRaii.Disabled(alreadyActive))
                    if (MenuButton(FontAwesomeIcon.Star, "Set as Active Campaign", "pact_" + party.Code)) { plugin.SetActiveCampaign(party.Code); ImGui.CloseCurrentPopup(); }

                if (plugin.IsDm(party.Code))
                    if (MenuButton(FontAwesomeIcon.Table, "Edit Sheet Template", "ptmpl_" + party.Code)) { plugin.CharacterSheetWindow.OpenTemplateEditor(party.Code); ImGui.CloseCurrentPopup(); }

                ImGui.Separator();
                if (MenuButton(FontAwesomeIcon.Copy, "Copy Campaign Code", "pcp_" + party.Code)) { ImGui.SetClipboardText(party.Code); ImGui.CloseCurrentPopup(); }
                ImGui.Separator();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                    if (MenuButton(FontAwesomeIcon.SignOutAlt, "Leave Campaign", "pl_" + party.Code)) { _pendingLeaveCode = party.Code; ImGui.CloseCurrentPopup(); }
                break;
            }
            case "member":
            {
                var party  = plugin.Store.Party(_flyoutCode);
                var member = party?.Members.FirstOrDefault(m => m.PlayerId == _flyoutPlayer);
                if (party == null || member == null) { ImGui.CloseCurrentPopup(); break; }

                string? localId  = plugin.LocalPlayerId;
                var     localM   = party.Members.FirstOrDefault(m => m.PlayerId == localId);
                bool    localDm  = localM?.Role == PartyRole.Owner;
                bool    localCoDm= localM?.Role == PartyRole.CoDm;
                bool    isMe     = member.PlayerId == localId;

                ImGui.TextDisabled(member.DisplayName);
                ImGui.Separator();
                if (isMe)
                {
                    if (MenuButton(FontAwesomeIcon.Book, "Open My Character Sheet", "cs_me_" + member.PlayerId)) { plugin.OpenSheetForParty(party.Code); ImGui.CloseCurrentPopup(); }
                }
                else
                {
                    if (MenuButton(FontAwesomeIcon.Book, "Open Character Sheet", "cs_" + member.PlayerId)) { plugin.OpenPlayerSheet(member.PlayerId); ImGui.CloseCurrentPopup(); }
                    if (MenuButton(FontAwesomeIcon.Scroll, "Open Skills", "sk_" + member.PlayerId)) { plugin.OpenPlayerSkills(member.PlayerId); ImGui.CloseCurrentPopup(); }
                }

                if (!isMe && plugin.Network.IsConnected && (localDm || localCoDm))
                {
                    ImGui.Separator();
                    if (localDm && member.Role == PartyRole.Member)
                        if (MenuButton(FontAwesomeIcon.UserShield, "Promote to Co-DM", "pr_" + member.PlayerId)) { _ = plugin.Network.PartySetRole(party.Code, member.PlayerId, PartyRole.CoDm); ImGui.CloseCurrentPopup(); }
                    if (localDm && member.Role == PartyRole.CoDm)
                        if (MenuButton(FontAwesomeIcon.UserMinus, "Demote to Member", "dm_" + member.PlayerId)) { _ = plugin.Network.PartySetRole(party.Code, member.PlayerId, PartyRole.Member); ImGui.CloseCurrentPopup(); }
                    if (localDm || member.Role == PartyRole.Member)
                        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                            if (MenuButton(FontAwesomeIcon.UserSlash, $"Kick {member.DisplayName}", "ki_" + member.PlayerId)) { _ = plugin.Network.PartyKick(party.Code, member.PlayerId); ImGui.CloseCurrentPopup(); }
                }
                break;
            }
        }
        ImGui.EndPopup();
    }

    private void DrawCreatePartyPopup()
    {
        if (!_createOpen) return;
        ImGui.SetNextWindowSize(new Vector2(320 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("##hub_create", ref _createOpen, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted("Create New Campaign");
        ImGui.Separator(); ImGui.Spacing();
        ImGui.TextUnformatted("Name:"); ImGui.SetNextItemWidth(-1); ImGui.InputText("##createname", ref _createName, 100);
        ImGui.TextUnformatted("Password (optional):"); ImGui.SetNextItemWidth(-1); ImGui.InputText("##createpw", ref _createPw, 64, ImGuiInputTextFlags.Password);

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_createName)))
            if (ImGui.Button("Create##docreate", new Vector2(bw, 0)))
            {
                _ = plugin.Network.PartyCreate(_createName.Trim(), string.IsNullOrEmpty(_createPw) ? null : _createPw);
                _createOpen = false; ImGui.CloseCurrentPopup();
            }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##cancelcreate", new Vector2(bw, 0))) { _createOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private void DrawJoinPartyPopup()
    {
        if (!_joinOpen) return;
        ImGui.SetNextWindowSize(new Vector2(320 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("##hub_join", ref _joinOpen, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted("Join Campaign");
        ImGui.Separator(); ImGui.Spacing();
        ImGui.TextUnformatted("Campaign Code:"); ImGui.SetNextItemWidth(-1); ImGui.InputText("##joincode", ref _joinCode, 20);
        ImGui.TextUnformatted("Password:"); ImGui.SetNextItemWidth(-1); ImGui.InputText("##joinpw", ref _joinPw, 64, ImGuiInputTextFlags.Password);

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_joinCode)))
            if (ImGui.Button("Join##dojoin", new Vector2(bw, 0)))
            {
                _ = plugin.Network.PartyJoin(_joinCode.Trim(), string.IsNullOrEmpty(_joinPw) ? null : _joinPw);
                _joinOpen = false; ImGui.CloseCurrentPopup();
            }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##canceljoin", new Vector2(bw, 0))) { _joinOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private void DrawLeaveConfirmPopup()
    {
        if (_pendingLeaveCode != null) ImGui.OpenPopup("##hub_leave_confirm");
        ImGui.SetNextWindowSize(new Vector2(300 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        bool open = true;
        if (!ImGui.BeginPopupModal("##hub_leave_confirm", ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        var party = plugin.Store.Party(_pendingLeaveCode);
        bool isOwner = party?.OwnerPlayerId == plugin.LocalPlayerId;
        ImGui.TextWrapped($"Leave \"{party?.Name ?? _pendingLeaveCode}\"?");
        if (isOwner) ImGui.TextColored(ImGuiColors.DalamudYellow, "You are the DM — this will disband the campaign.");

        ImGui.Spacing();
        float bw = 90 * ImGuiHelpers.GlobalScale;
        if (ImGui.Button("Leave##confirmleave", new Vector2(bw, 0)))
        {
            if (isOwner) _ = plugin.Network.PartyDisband(_pendingLeaveCode!);
            else         _ = plugin.Network.PartyLeave(_pendingLeaveCode!);
            plugin.Store.RemoveParty(_pendingLeaveCode!);
            _pendingLeaveCode = null; ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##cancelleave", new Vector2(bw, 0))) { _pendingLeaveCode = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static float ContentWidth() => ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
    private static IDisposable IconFont() => Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push();

    private static void DrawIcon(FontAwesomeIcon icon) { using var _ = IconFont(); ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(icon.ToIconString()); }
    private static void DrawIconColored(FontAwesomeIcon icon, Vector4 color) { using var _ = IconFont(); ImGui.AlignTextToFramePadding(); ImGui.TextColored(color, icon.ToIconString()); }
    private static void DrawIconButton(FontAwesomeIcon icon, string id) { using var _ = IconFont(); ImGui.Button(icon.ToIconString() + id); }

    private static bool MenuButton(FontAwesomeIcon icon, string label, string id)
    {
        var   savedPos = ImGui.GetCursorPos();
        float width = ImGui.GetContentRegionAvail().X, height = ImGui.GetFrameHeight();
        bool clicked = ImGui.Selectable($"##{id}", false, ImGuiSelectableFlags.None, new Vector2(width, height));
        ImGui.SetCursorPos(new Vector2(savedPos.X + ImGui.GetStyle().FramePadding.X, savedPos.Y));
        DrawIcon(icon); ImGui.SameLine(); ImGui.TextUnformatted(label);
        return clicked;
    }

    private static string Truncate(string text, float maxPx)
    {
        if (maxPx <= 0) return string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxPx) return text;
        const string ellipsis = "…";
        float ew = ImGui.CalcTextSize(ellipsis).X;
        if (ew >= maxPx) return ellipsis;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X + ew <= maxPx) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? ellipsis : text[..lo] + ellipsis;
    }

    private static FontAwesomeIcon RoleIcon(PartyRole r) => r switch { PartyRole.Owner => FontAwesomeIcon.Crown, PartyRole.CoDm => FontAwesomeIcon.UserShield, _ => FontAwesomeIcon.Users };
    private static Vector4 RoleColor(PartyRole r) => r switch { PartyRole.Owner => new Vector4(1f, 0.82f, 0.2f, 1f), PartyRole.CoDm => new Vector4(0.7f, 0.6f, 1f, 1f), _ => new Vector4(0.6f, 0.6f, 0.6f, 1f) };
    private static string RoleLabel(PartyRole r) => r switch { PartyRole.Owner => "DM (Owner)", PartyRole.CoDm => "Co-DM", _ => "Member" };

    private static FontAwesomeIcon MemberIcon(PartyRole r, bool online) => r switch
    {
        PartyRole.Owner => FontAwesomeIcon.Crown,
        PartyRole.CoDm  => FontAwesomeIcon.UserShield,
        _               => online ? FontAwesomeIcon.User : FontAwesomeIcon.UserSlash,
    };

    private static Vector4 MemberColor(PartyRole r, bool isMe, bool online)
    {
        if (!online) return new Vector4(0.40f, 0.40f, 0.40f, 1f);
        return r switch
        {
            PartyRole.Owner => new Vector4(1f, 0.82f, 0.2f, 1f),
            PartyRole.CoDm  => new Vector4(0.7f, 0.6f, 1f, 1f),
            _               => isMe ? new Vector4(0.5f, 0.85f, 1f, 1f) : new Vector4(0.35f, 0.85f, 0.35f, 1f),
        };
    }

    private static string MemberTooltip(PartyRole r, bool isMe, bool online)
    {
        string role = r switch { PartyRole.Owner => "DM", PartyRole.CoDm => "Co-DM", _ => "Member" };
        return isMe ? $"You  ({role})" : $"{role}  ({(online ? "Online" : "Offline")})";
    }
}
