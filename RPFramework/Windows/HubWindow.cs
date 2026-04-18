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
using RPFramework.Models;
using RPFramework.Models.Net;

namespace RPFramework.Windows;

public class HubWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Expand/collapse state per party code
    private readonly HashSet<string> _openParties = new();
    // Row hover state from previous frame (per row ID)
    private readonly Dictionary<string, bool> _hovered = new();

    // Search
    private string _searchQuery = string.Empty;

    // ── Deferred flyout popup ─────────────────────────────────────────────────
    // Popups must be rendered at the top Draw() level (outside all child windows)
    // to avoid being clipped. We store context when a button is clicked and open
    // the popup at the start of the next Draw() call.

    private string? _flyoutKind;    // "partiesmenu" | "fellowmenu" | "party" | "member" | "fellow"
    private string? _flyoutCode;    // party code (for party / member)
    private string? _flyoutPlayer;  // player id  (for member / fellow)
    private Vector2 _flyoutPos;     // screen position — bottom-right of the clicked button
    private bool    _flyoutPending;

    // ── Modal popup state ─────────────────────────────────────────────────────

    private string _createName    = string.Empty;
    private string _createPw      = string.Empty;
    private string _createErr     = string.Empty;
    private bool   _createPending;
    private bool   _createOpen;

    private string _joinCode      = string.Empty;
    private string _joinPw        = string.Empty;
    private string _joinErr       = string.Empty;
    private bool   _joinPending;
    private bool   _joinOpen;

    private string? _pendingLeaveCode;

    private string _addFellowId   = string.Empty;
    private string _addFellowErr  = string.Empty;
    private bool   _addFellowPending;
    private bool   _addFellowOpen;

    public HubWindow(Plugin plugin) : base("RP Hub##RPFramework.Hub")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 340),
            MaximumSize = new Vector2(440, 900),
        };
        Size          = new Vector2(440, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags         = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon        = FontAwesomeIcon.Cog,
            ShowTooltip = () => ImGui.SetTooltip("Open Settings"),
            Click       = _ => plugin.SettingsWindow.IsOpen = true,
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon        = FontAwesomeIcon.Heart,
            IconColor   = new Vector4(0.93f, 0.25f, 0.25f, 1f),
            ShowTooltip = () => ImGui.SetTooltip("Help me keep the server running."),
            Click       = _ => Util.OpenLink("https://ko-fi.com/zerothescyther"),
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        // ── 1. Trigger modal popups ───────────────────────────────────────────
        if (_createPending)    { _createOpen    = true; ImGui.OpenPopup("##hub_create");    _createPending    = false; }
        if (_joinPending)      { _joinOpen      = true; ImGui.OpenPopup("##hub_join");      _joinPending      = false; }
        if (_addFellowPending) { _addFellowOpen = true; ImGui.OpenPopup("##hub_addfellow"); _addFellowPending = false; }

        // ── 3. Header ─────────────────────────────────────────────────────────
        DrawIdentityHeader();
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##hubsearch", "Search parties and adventurers\u2026", ref _searchQuery, 128);
        ImGuiHelpers.ScaledDummy(2f);

        // ── 4. Scrollable content (all child windows live here) ───────────────
        ImGui.BeginChild("##hubscroll", new Vector2(-1, -1), false);
        DrawPartiesSection();
        ImGuiHelpers.ScaledDummy(6f);
        DrawFellowAdventurersSection();
        ImGui.EndChild();

        // ── 5. Flyout popup — at top level, never clipped ─────────────────────
        DrawFlyout();

        // ── 6. Modal popups ───────────────────────────────────────────────────
        DrawCreatePartyPopup();
        DrawJoinPartyPopup();
        DrawLeaveConfirmPopup();
        DrawAddFellowPopup();
    }

    // ── Identity / connection header ──────────────────────────────────────────

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
        else
        {
            ImGui.TextDisabled("(Log in to see your ID)");
        }

        ImGui.AlignTextToFramePadding();
        DrawIconColored(FontAwesomeIcon.Circle,
            connected ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(connected ? "Connected to relay server" : "Not connected");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(connected ? "Connected to relay" : "Not connected  (/rpsettings to configure)");

        float btnW = 90 * ImGuiHelpers.GlobalScale;
        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + cw - btnW);
        if (connected)
        {
            if (ImGui.Button("Disconnect##hubdisc", new Vector2(btnW, 0)))
                Task.Run(() => plugin.Network.DisconnectAsync());
        }
        else
        {
            if (ImGui.Button("Connect##hubconn", new Vector2(btnW, 0)))
            {
                string url  = plugin.Configuration.ServerUrl;
                string? id  = plugin.LocalPlayerId;
                string name = plugin.LocalDisplayName;
                if (id != null) Task.Run(() => plugin.Network.ConnectAsync(url, id, name));
            }
        }
    }

    // ── Parties section ───────────────────────────────────────────────────────

    private void DrawPartiesSection()
    {
        float cw       = ContentWidth();
        float menuBtnW = ImGui.GetFrameHeight();

        ImGui.AlignTextToFramePadding();
        DrawIcon(FontAwesomeIcon.Users);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Parties");

        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + cw - menuBtnW);
        var btnScreen = ImGui.GetCursorScreenPos();
        DrawIconButton(FontAwesomeIcon.Bars, "##partiesmenubtn");
        if (ImGui.IsItemClicked())
        {
            var r = ImGui.GetItemRectMax();
            _flyoutKind = "partiesmenu"; _flyoutCode = null; _flyoutPlayer = null;
            _flyoutPos = r; _flyoutPending = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Parties menu");

        ImGui.Separator();

        var parties = plugin.Configuration.Parties;
        var filtered = string.IsNullOrWhiteSpace(_searchQuery)
            ? parties
            : parties.Where(p =>
                p.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
             || p.Code.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
             || (plugin.PartyMembers.TryGetValue(p.Code, out var ms) && ms.Any(m =>
                    m.DisplayName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
                 || m.PlayerId.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))))
                     .ToList();

        if (filtered.Count == 0)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + 6f * ImGuiHelpers.GlobalScale);
            ImGui.TextDisabled(parties.Count == 0
                ? "No parties. Use the menu above to create or join one."
                : "No parties match your search.");
        }
        else
        {
            foreach (var party in filtered.ToList())
                DrawPartyRow(party);
        }
    }

    private void DrawPartyRow(Models.RpParty party)
    {
        plugin.PartyMembers.TryGetValue(party.Code, out var members);
        string? localPid   = plugin.LocalPlayerId;
        int onlineCount = members?.Count(m =>
            m.PlayerId == localPid || plugin.KnownOnlinePlayers.Contains(m.PlayerId)) ?? 0;
        int totalCount  = members?.Count ?? 0;
        bool   isOpen      = _openParties.Contains(party.Code);
        string rowId       = "pr_" + party.Code;
        float  rowW        = ContentWidth();

        bool hovered = _hovered.GetValueOrDefault(rowId);
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), hovered);
        using (ImRaii.Child(rowId, new Vector2(rowW, ImGui.GetFrameHeight()), false))
        {
            ImGui.AlignTextToFramePadding();

            DrawIcon(isOpen ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight);
            if (ImGui.IsItemClicked())
            {
                if (isOpen) _openParties.Remove(party.Code);
                else        _openParties.Add(party.Code);
            }
            ImGui.SameLine();

            var     localMember = members?.FirstOrDefault(m => m.PlayerId == localPid);
            var     myRole      = localMember?.Role ?? PartyRole.Member;
            DrawIconColored(RoleIcon(myRole), RoleColor(myRole));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(RoleLabel(myRole));
            ImGui.SameLine();
            float leftEnd = ImGui.GetCursorPosX();

            float ellipsisW  = ImGui.GetFrameHeight();
            float windowEndX = ImGui.GetWindowContentRegionMax().X;
            ImGui.SameLine(windowEndX - ellipsisW);
            DrawIconButton(FontAwesomeIcon.EllipsisV, "##pe_" + party.Code);
            if (ImGui.IsItemClicked())
            {
                var r = ImGui.GetItemRectMax();
                _flyoutKind = "party"; _flyoutCode = party.Code; _flyoutPlayer = null;
                _flyoutPos = r; _flyoutPending = true;
            }

            bool   inCombat  = plugin.InitiativeStates.ContainsKey(party.Code);
            string combatTag = "  In Combat";
            float  combatW   = inCombat
                ? (ImGui.CalcTextSize(combatTag).X + ImGui.GetStyle().ItemSpacing.X)
                : 0f;

            ImGui.SameLine(leftEnd);
            float maxW = windowEndX - ellipsisW - ImGui.GetStyle().ItemSpacing.X - leftEnd - combatW;
            string countLabel = totalCount > onlineCount
                ? $"{party.Name}  [{onlineCount}/{totalCount}]"
                : $"{party.Name}  [{onlineCount}]";
            ImGui.TextUnformatted(Truncate(countLabel, maxW));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{party.Code}   {onlineCount}/{totalCount} online");

            if (inCombat)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.90f, 0.25f, 0.25f, 1f), combatTag);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Initiative is active in this party");
            }
        }
        _hovered[rowId] = ImGui.IsItemHovered();

        if (isOpen)
        {
            using var indent = ImRaii.PushIndent(16f * ImGuiHelpers.GlobalScale, false);
            DrawPartyMembers(party, members);
            ImGui.Separator();
        }
    }

    private void DrawPartyMembers(Models.RpParty party, List<PartyMemberDto>? members)
    {
        if (members == null || members.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("   (no members online)");
            ImGui.Spacing();
            return;
        }

        string? localId    = plugin.LocalPlayerId;
        float   memberW    = ImGui.GetContentRegionAvail().X;

        var visibleMembers = string.IsNullOrWhiteSpace(_searchQuery)
            ? members
            : members.Where(m => m.DisplayName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
                               || m.PlayerId.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
                     .ToList();

        foreach (var member in visibleMembers)
        {
            bool   isMe   = member.PlayerId == localId;
            bool   online = isMe || plugin.KnownOnlinePlayers.Contains(member.PlayerId);
            string rowId  = "mr_" + party.Code + "_" + member.PlayerId;

            bool hovered = _hovered.GetValueOrDefault(rowId);
            using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), hovered);
            using (ImRaii.Child(rowId, new Vector2(memberW, ImGui.GetFrameHeight()), false))
            {
                ImGui.AlignTextToFramePadding();

                DrawIconColored(MemberIcon(member.Role, isMe, online), MemberColor(member.Role, isMe, online));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(MemberTooltip(member.Role, isMe, online));
                ImGui.SameLine();
                float leftEnd = ImGui.GetCursorPosX();

                float ellipsisW  = ImGui.GetFrameHeight();
                float windowEndX = ImGui.GetWindowContentRegionMax().X;
                ImGui.SameLine(windowEndX - ellipsisW);
                DrawIconButton(FontAwesomeIcon.EllipsisV, "##me_" + party.Code + "_" + member.PlayerId);
                if (ImGui.IsItemClicked())
                {
                    var r = ImGui.GetItemRectMax();
                    _flyoutKind = "member"; _flyoutCode = party.Code; _flyoutPlayer = member.PlayerId;
                    _flyoutPos = r; _flyoutPending = true;
                }

                ImGui.SameLine(leftEnd);
                float maxW      = windowEndX - ellipsisW - ImGui.GetStyle().ItemSpacing.X - leftEnd;
                string dispName = isMe ? $"{member.DisplayName} (You)" : member.DisplayName;
                using (ImRaii.PushColor(ImGuiCol.Text, isMe
                    ? new Vector4(0.55f, 0.88f, 1f, 1f)
                    : Vector4.One))
                {
                    ImGui.TextUnformatted(Truncate(dispName, maxW));
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(member.PlayerId);

                foreach (var roomCode in member.BgmRoomCodes)
                {
                    ImGui.SameLine();
                    using var bc = ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.12f, 0.30f, 0.55f, 0.9f));
                    using var bh = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.42f, 0.75f, 1f));
                    if (ImGui.SmallButton($"♪ {roomCode}##bgm_{roomCode}_{member.PlayerId}"))
                        plugin.JoinBgmRoomFromParty(roomCode);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Join BGM room {roomCode}");
                }
            }
            _hovered[rowId] = ImGui.IsItemHovered();
        }
    }

    // ── Fellow Adventurers section ────────────────────────────────────────────

    private void DrawFellowAdventurersSection()
    {
        float cw       = ContentWidth();
        float menuBtnW = ImGui.GetFrameHeight();

        ImGui.AlignTextToFramePadding();
        DrawIcon(FontAwesomeIcon.UserFriends);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Fellow Adventurers");

        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + cw - menuBtnW);
        DrawIconButton(FontAwesomeIcon.Bars, "##fellowmenubtn");
        if (ImGui.IsItemClicked())
        {
            var r = ImGui.GetItemRectMax();
            _flyoutKind = "fellowmenu"; _flyoutCode = null; _flyoutPlayer = null;
            _flyoutPos = r; _flyoutPending = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fellow Adventurers menu");

        ImGui.Separator();

        var fellows  = plugin.Configuration.FellowAdventurers;
        var filtered = string.IsNullOrWhiteSpace(_searchQuery)
            ? fellows
            : fellows.Where(f => f.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
                     .ToList();

        if (filtered.Count == 0)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + 6f * ImGuiHelpers.GlobalScale);
            if (fellows.Count == 0)
            {
                ImGui.TextDisabled("No fellow adventurers yet.");
                ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + 6f * ImGuiHelpers.GlobalScale);
                ImGui.TextDisabled("Add someone using their ID (Name@World).");
            }
            else
            {
                ImGui.TextDisabled("No fellow adventurers match your search.");
            }
            return;
        }

        float rowW = ContentWidth();
        foreach (var fellowId in filtered.ToList())
            DrawFellowRow(fellowId, rowW);
    }

    private void DrawFellowRow(string fellowId, float rowW)
    {
        bool   online = plugin.KnownOnlinePlayers.Contains(fellowId);
        string rowId  = "fr_" + fellowId;
        string dn     = DisplayName(fellowId);

        bool hovered = _hovered.GetValueOrDefault(rowId);
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), hovered);
        using (ImRaii.Child(rowId, new Vector2(rowW, ImGui.GetFrameHeight()), false))
        {
            ImGui.AlignTextToFramePadding();

            DrawIconColored(
                online ? FontAwesomeIcon.User : FontAwesomeIcon.UserSlash,
                online ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(online ? "Online / profile cached" : "Offline or not yet seen");
            ImGui.SameLine();
            float leftEnd = ImGui.GetCursorPosX();

            float ellipsisW  = ImGui.GetFrameHeight();
            float windowEndX = ImGui.GetWindowContentRegionMax().X;
            ImGui.SameLine(windowEndX - ellipsisW);
            DrawIconButton(FontAwesomeIcon.EllipsisV, "##fe_" + fellowId);
            if (ImGui.IsItemClicked())
            {
                var r = ImGui.GetItemRectMax();
                _flyoutKind = "fellow"; _flyoutCode = null; _flyoutPlayer = fellowId;
                _flyoutPos = r; _flyoutPending = true;
            }

            ImGui.SameLine(leftEnd);
            float maxW = windowEndX - ellipsisW - ImGui.GetStyle().ItemSpacing.X - leftEnd;
            ImGui.TextUnformatted(Truncate(dn, maxW));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(fellowId);
        }
        _hovered[rowId] = ImGui.IsItemHovered();
    }

    // ── Flyout popup (single popup rendered at top level) ─────────────────────

    private void DrawFlyout()
    {
        // Open + position happens here — after EndChild, in the parent window context.
        // SetNextWindowPos must immediately precede BeginPopup or it gets consumed by
        // BeginChild instead.
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
                bool canAct = plugin.Network.IsConnected;
                if (!canAct) ImGui.BeginDisabled();
                if (MenuButton(FontAwesomeIcon.Plus, "Create new party", "pm_create"))
                { _createName = _createPw = _createErr = string.Empty; _createPending = true; ImGui.CloseCurrentPopup(); }
                if (MenuButton(FontAwesomeIcon.SignInAlt, "Join a party", "pm_join"))
                { _joinCode = _joinPw = _joinErr = string.Empty; _joinPending = true; ImGui.CloseCurrentPopup(); }
                if (!canAct) ImGui.EndDisabled();
                break;
            }
            case "fellowmenu":
            {
                if (MenuButton(FontAwesomeIcon.UserPlus, "Add Fellow Adventurer", "fm_add"))
                { _addFellowId = _addFellowErr = string.Empty; _addFellowPending = true; ImGui.CloseCurrentPopup(); }
                break;
            }
            case "party":
            {
                var party = plugin.Configuration.Parties.FirstOrDefault(p => p.Code == _flyoutCode);
                if (party == null) { ImGui.CloseCurrentPopup(); break; }
                ImGui.TextDisabled(party.Name);
                ImGui.TextDisabled(party.Code);
                ImGui.Separator();

                // Join Combat — shown when initiative is active and this player hasn't rolled yet
                string? localPid = plugin.LocalPlayerId;
                if (localPid != null
                    && plugin.InitiativeStates.TryGetValue(party.Code, out var iniState)
                    && !iniState.Order.Any(e => e.PlayerId == localPid))
                {
                    if (MenuButton(FontAwesomeIcon.Bolt, "Join Combat", "jc_" + party.Code))
                    {
                        var ch        = plugin.GetOrCreateCharacter(localPid);
                        int roll      = new Random().Next(1, 25);
                        int initBonus = plugin.GetInitiativeBonus(ch, plugin.Configuration.ActiveTemplate);
                        string code   = party.Code;
                        Task.Run(() => plugin.Network.PartySubmitRollAsync(code, roll, initBonus));
                        plugin.InitiativeWindow.IsOpen = true;
                        ImGui.CloseCurrentPopup();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Roll initiative and join the current combat round.");
                    ImGui.Separator();
                }

                if (MenuButton(FontAwesomeIcon.Copy, "Copy Party Code", "pcp_" + party.Code))
                { ImGui.SetClipboardText(party.Code); ImGui.CloseCurrentPopup(); }
                ImGui.Separator();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                {
                    if (MenuButton(FontAwesomeIcon.SignOutAlt, "Leave Party", "pl_" + party.Code))
                    { _pendingLeaveCode = party.Code; ImGui.CloseCurrentPopup(); }
                }
                break;
            }
            case "member":
            {
                plugin.PartyMembers.TryGetValue(_flyoutCode ?? "", out var members);
                var member = members?.FirstOrDefault(m => m.PlayerId == _flyoutPlayer);
                if (member == null) { ImGui.CloseCurrentPopup(); break; }

                string? localId     = plugin.LocalPlayerId;
                var     localMember = members?.FirstOrDefault(m => m.PlayerId == localId);
                bool    localIsDm   = localMember?.Role == PartyRole.Owner;
                bool    localIsCoDm = localMember?.Role == PartyRole.CoDm;
                bool    isMe        = member.PlayerId == localId;

                ImGui.TextDisabled(member.DisplayName);
                ImGui.Separator();
                if (MenuButton(FontAwesomeIcon.Book, "Open Character Sheet", "cs_" + member.PlayerId))
                { plugin.OpenPlayerSheet(member.PlayerId, member.DisplayName); ImGui.CloseCurrentPopup(); }
                if (MenuButton(FontAwesomeIcon.Scroll, "Open Skills", "sk_" + member.PlayerId))
                { plugin.OpenPlayerSkills(member.PlayerId, member.DisplayName); ImGui.CloseCurrentPopup(); }

                if (!isMe && plugin.Network.IsConnected && (localIsDm || localIsCoDm))
                {
                    ImGui.Separator();
                    if (localIsDm && member.Role == PartyRole.Member)
                    {
                        if (MenuButton(FontAwesomeIcon.UserShield, "Promote to Co-DM", "pr_" + member.PlayerId))
                        { Task.Run(() => plugin.Network.PartySetRoleAsync(_flyoutCode!, member.PlayerId, PartyRole.CoDm)); ImGui.CloseCurrentPopup(); }
                    }
                    if (localIsDm && member.Role == PartyRole.CoDm)
                    {
                        if (MenuButton(FontAwesomeIcon.UserMinus, "Demote to Member", "dm_" + member.PlayerId))
                        { Task.Run(() => plugin.Network.PartySetRoleAsync(_flyoutCode!, member.PlayerId, PartyRole.Member)); ImGui.CloseCurrentPopup(); }
                    }
                    if (localIsDm || member.Role == PartyRole.Member)
                    {
                        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                        {
                            if (MenuButton(FontAwesomeIcon.UserSlash, $"Kick {member.DisplayName}", "ki_" + member.PlayerId))
                            { Task.Run(() => plugin.Network.PartyKickAsync(_flyoutCode!, member.PlayerId)); ImGui.CloseCurrentPopup(); }
                        }
                    }
                }
                break;
            }
            case "fellow":
            {
                string fellowId = _flyoutPlayer ?? "";
                string dn       = DisplayName(fellowId);
                ImGui.TextDisabled(dn);
                ImGui.TextDisabled(fellowId);
                ImGui.Separator();
                if (MenuButton(FontAwesomeIcon.Book, "Open Character Sheet", "fcs_" + fellowId))
                { plugin.OpenPlayerSheet(fellowId, dn); ImGui.CloseCurrentPopup(); }
                if (MenuButton(FontAwesomeIcon.Scroll, "Open Skills", "fsk_" + fellowId))
                { plugin.OpenPlayerSkills(fellowId, dn); ImGui.CloseCurrentPopup(); }
                ImGui.Separator();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                {
                    if (MenuButton(FontAwesomeIcon.Trash, "Remove", "frm_" + fellowId))
                    {
                        plugin.Configuration.FellowAdventurers.Remove(fellowId);
                        plugin.Configuration.Save();
                        ImGui.CloseCurrentPopup();
                    }
                }
                break;
            }
        }

        ImGui.EndPopup();
    }

    // ── Modal popups ──────────────────────────────────────────────────────────

    private void DrawCreatePartyPopup()
    {
        if (!_createOpen) return;
        ImGui.SetNextWindowSize(new Vector2(320 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("##hub_create", ref _createOpen,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted("Create New Party");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Name:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##createname", ref _createName, 100);

        ImGui.TextUnformatted("Password:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##createpw", ref _createPw, 64, ImGuiInputTextFlags.Password);

        if (!string.IsNullOrEmpty(_createErr))
            ImGui.TextColored(ImGuiColors.DalamudRed, _createErr);

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;

        if (ImGui.Button("Create##docreate", new Vector2(bw, 0)))
        {
            if (string.IsNullOrWhiteSpace(_createName))
                _createErr = "Name is required.";
            else if (_createPw.Length < 4)
                _createErr = "Password must be at least 4 characters.";
            else
            {
                _createErr = string.Empty;
                string name = _createName, pw = _createPw;
                Task.Run(async () =>
                {
                    var info = await plugin.Network.PartyCreateAsync(name, pw);
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (info == null) _createErr = "Server rejected. Try again.";
                        else { plugin.OnPartyInfoReceived(info); _createOpen = false; ImGui.CloseCurrentPopup(); }
                    });
                });
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##cancelcreate", new Vector2(bw, 0)))
        { _createOpen = false; ImGui.CloseCurrentPopup(); }

        ImGui.EndPopup();
    }

    private void DrawJoinPartyPopup()
    {
        if (!_joinOpen) return;
        ImGui.SetNextWindowSize(new Vector2(320 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("##hub_join", ref _joinOpen,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted("Join Party");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Party Code:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##joincode", ref _joinCode, 20);

        ImGui.TextUnformatted("Password:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##joinpw", ref _joinPw, 64, ImGuiInputTextFlags.Password);

        if (!string.IsNullOrEmpty(_joinErr))
            ImGui.TextColored(ImGuiColors.DalamudRed, _joinErr);

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;

        if (ImGui.Button("Join##dojoin", new Vector2(bw, 0)))
        {
            string code = _joinCode.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code))         _joinErr = "Code is required.";
            else if (string.IsNullOrWhiteSpace(_joinPw)) _joinErr = "Password is required.";
            else
            {
                _joinErr = string.Empty;
                string pw = _joinPw;
                Task.Run(async () =>
                {
                    var info = await plugin.Network.PartyJoinAsync(code, pw);
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (info == null) _joinErr = "Wrong code or password.";
                        else { plugin.OnPartyInfoReceived(info); _joinOpen = false; ImGui.CloseCurrentPopup(); }
                    });
                });
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##canceljoin", new Vector2(bw, 0)))
        { _joinOpen = false; ImGui.CloseCurrentPopup(); }

        ImGui.EndPopup();
    }

    private void DrawLeaveConfirmPopup()
    {
        if (_pendingLeaveCode != null) ImGui.OpenPopup("##hub_leave_confirm");

        ImGui.SetNextWindowSize(new Vector2(300 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        bool open = true;
        if (!ImGui.BeginPopupModal("##hub_leave_confirm", ref open,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        var party = plugin.Configuration.Parties.FirstOrDefault(p => p.Code == _pendingLeaveCode);
        ImGui.TextWrapped($"Leave \"{party?.Name ?? _pendingLeaveCode}\"?");
        if (party?.OwnerPlayerId == plugin.LocalPlayerId)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "You are the DM — this will disband the party.");

        ImGui.Spacing();
        float bw = 90 * ImGuiHelpers.GlobalScale;

        if (ImGui.Button("Leave##confirmleave", new Vector2(bw, 0)))
        { plugin.LeaveParty(_pendingLeaveCode!); _pendingLeaveCode = null; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##cancelleave", new Vector2(bw, 0)))
        { _pendingLeaveCode = null; ImGui.CloseCurrentPopup(); }

        ImGui.EndPopup();
    }

    private void DrawAddFellowPopup()
    {
        if (!_addFellowOpen) return;
        ImGui.SetNextWindowSize(new Vector2(340 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("##hub_addfellow", ref _addFellowOpen,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        ImGui.TextUnformatted("Add Fellow Adventurer");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("Enter their ID in the format: Firstname Lastname@World");
        ImGui.Spacing();

        ImGui.TextUnformatted("ID:");
        ImGui.SetNextItemWidth(-1);
        bool enter = ImGui.InputText("##addfellowid", ref _addFellowId, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);

        if (!string.IsNullOrEmpty(_addFellowErr))
            ImGui.TextColored(ImGuiColors.DalamudRed, _addFellowErr);

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;

        if (ImGui.Button("Add##doaddfellow", new Vector2(bw, 0)) || enter)
        {
            string id = _addFellowId.Trim();
            if (!id.Contains('@'))
                _addFellowErr = "Must be in the format Name@World.";
            else if (id == plugin.LocalPlayerId)
                _addFellowErr = "That's you!";
            else if (plugin.Configuration.FellowAdventurers.Contains(id))
                _addFellowErr = "Already in your list.";
            else
            {
                plugin.Configuration.FellowAdventurers.Add(id);
                plugin.Configuration.Save();
                if (plugin.Network.IsConnected)
                    Task.Run(() => plugin.Network.FetchProfileAsync(id));
                _addFellowOpen = false;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##canceladdfellow", new Vector2(bw, 0)))
        { _addFellowOpen = false; ImGui.CloseCurrentPopup(); }

        ImGui.EndPopup();
    }

    // ── Drawing helpers ───────────────────────────────────────────────────────

    private static float ContentWidth()
        => ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

    private static IDisposable IconFont()
        => Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push();

    private static void DrawIcon(FontAwesomeIcon icon)
    {
        using var _ = IconFont();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(icon.ToIconString());
    }

    private static void DrawIconColored(FontAwesomeIcon icon, Vector4 color)
    {
        using var _ = IconFont();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, icon.ToIconString());
    }

    private static void DrawIconButton(FontAwesomeIcon icon, string id)
    {
        using var _ = IconFont();
        ImGui.Button(icon.ToIconString() + id);
    }

    private static bool MenuButton(FontAwesomeIcon icon, string label, string id)
    {
        // Save position, draw an invisible selectable as the hit/highlight region,
        // then restore position and draw icon + label on top with standard ImGui calls.
        var   savedPos = ImGui.GetCursorPos();
        float width    = ImGui.GetContentRegionAvail().X;
        float height   = ImGui.GetFrameHeight();

        bool clicked = ImGui.Selectable($"##{id}", false, ImGuiSelectableFlags.None,
            new Vector2(width, height));

        ImGui.SetCursorPos(new Vector2(savedPos.X + ImGui.GetStyle().FramePadding.X, savedPos.Y));
        DrawIcon(icon);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);

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
            if (ImGui.CalcTextSize(text[..mid]).X + ew <= maxPx) lo = mid;
            else hi = mid - 1;
        }
        return lo == 0 ? ellipsis : text[..lo] + ellipsis;
    }

    private static string DisplayName(string id)
        => id.Contains('@') ? id[..id.IndexOf('@')] : id;

    // ── Role helpers ──────────────────────────────────────────────────────────

    private static FontAwesomeIcon RoleIcon(PartyRole r) => r switch
    {
        PartyRole.Owner => FontAwesomeIcon.Crown,
        PartyRole.CoDm  => FontAwesomeIcon.UserShield,
        _               => FontAwesomeIcon.Users,
    };

    private static Vector4 RoleColor(PartyRole r) => r switch
    {
        PartyRole.Owner => new Vector4(1f, 0.82f, 0.2f, 1f),
        PartyRole.CoDm  => new Vector4(0.7f, 0.6f, 1f, 1f),
        _               => new Vector4(0.6f, 0.6f, 0.6f, 1f),
    };

    private static string RoleLabel(PartyRole r) => r switch
    {
        PartyRole.Owner => "DM (Owner)",
        PartyRole.CoDm  => "Co-DM",
        _               => "Member",
    };

    private static FontAwesomeIcon MemberIcon(PartyRole r, bool isMe, bool online) => r switch
    {
        PartyRole.Owner => FontAwesomeIcon.Crown,
        PartyRole.CoDm  => FontAwesomeIcon.UserShield,
        _               => online ? FontAwesomeIcon.User : FontAwesomeIcon.UserSlash,
    };

    private static Vector4 MemberColor(PartyRole r, bool isMe, bool online)
    {
        if (!online)
            return new Vector4(0.40f, 0.40f, 0.40f, 1f); // grey for all offline roles

        return r switch
        {
            PartyRole.Owner => new Vector4(1f, 0.82f, 0.2f, 1f),
            PartyRole.CoDm  => new Vector4(0.7f, 0.6f, 1f, 1f),
            _               => isMe
                ? new Vector4(0.5f, 0.85f, 1f, 1f)
                : new Vector4(0.35f, 0.85f, 0.35f, 1f),
        };
    }

    private static string MemberTooltip(PartyRole r, bool isMe, bool online)
    {
        string role   = r switch { PartyRole.Owner => "DM", PartyRole.CoDm => "Co-DM", _ => "Member" };
        string status = online ? "Online" : "Offline";
        return isMe ? $"You  ({role})" : $"{role}  ({status})";
    }
}
