using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Services;

namespace RPFramework.Windows;

/// <summary>
/// BGM room browser. Campaign rooms appear automatically for every campaign member; solo rooms are
/// discovered by code. Rooms have a name and optional password; create/join are intents.
/// </summary>
public class BgmWindow : Window, IDisposable
{
    private readonly Plugin          plugin;
    private readonly BgmService      bgmService;
    private readonly BgmPlayerWindow playerWindow;

    private bool   _pendingCreate, _createOpen;
    private string _createName = "", _createPw = "";
    private string _joinCode = "", _joinPw = "";

    public BgmWindow(Plugin plugin, BgmService bgmService, BgmPlayerWindow playerWindow)
        : base("RP BGM##RPFramework.BGM")
    {
        this.plugin       = plugin;
        this.bgmService   = bgmService;
        this.playerWindow = playerWindow;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(280, 300), MaximumSize = new Vector2(600, 800) };
        Size          = new Vector2(320, 380);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    /// <summary>The campaign a newly-created room is scoped to: the active campaign unless it's the solo scope.</summary>
    private string? ScopeCampaign()
    {
        var code = plugin.ActiveCampaign;
        return code != null && plugin.Store.Party(code)?.IsPersonal == false ? code : null;
    }

    private void OpenRoom(string code, string? pw)
    {
        _ = plugin.Network.RoomJoin(code, pw);   // idempotent if already a member; campaign members bypass the password
        playerWindow.OpenRoom(code);             // sets active + remembers for reconnect
    }

    public override void Draw()
    {
        if (!plugin.Network.IsConnected)
        {
            ImGui.TextDisabled("Not connected to server.");
            if (ImGui.Button("Connect##bgmconn")) plugin.Connect();
            return;
        }

        if (_pendingCreate) { _createOpen = true; ImGui.OpenPopup("##bgm_create"); _pendingCreate = false; }

        float scale = ImGuiHelpers.GlobalScale;
        var rooms = plugin.Store.Rooms.OrderBy(r => r.Name).ToList();

        ImGui.TextDisabled("Rooms");
        ImGui.Separator();

        using (var child = ImRaii.Child("##bgmroomlist", new Vector2(-1, -ImGui.GetFrameHeightWithSpacing() * 2 - 8), true))
        {
            if (child)
            {
                foreach (var room in rooms)
                {
                    ImGui.PushID($"##bgmr_{room.Code}");
                    bool playing = bgmService.CurrentRoomCode == room.Code && bgmService.IsPlaying;
                    bool campaign = !string.IsNullOrEmpty(room.CampaignCode);
                    string label = $"{(playing ? "♪ " : "")}{room.Name}";
                    if (ImGui.Selectable(label)) OpenRoom(room.Code, null);
                    if (campaign) { ImGui.SameLine(); ImGui.TextDisabled("[campaign]"); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Code: {room.Code}  ·  {room.Members.Count} listening");
                    if (ImGui.BeginPopupContextItem("##bgmrctx"))
                    {
                        if (ImGui.MenuItem("Open player")) OpenRoom(room.Code, null);
                        if (ImGui.MenuItem("Copy code")) ImGui.SetClipboardText(room.Code);
                        bool isOwner = room.OwnerPlayerId == plugin.LocalPlayerId;
                        if (ImGui.MenuItem(isOwner ? "Dissolve room" : "Leave room")) plugin.Bgm.LeaveRoom(room.Code);
                        ImGui.EndPopup();
                    }
                    ImGui.PopID();
                }
                if (rooms.Count == 0) ImGui.TextDisabled("No rooms. Create one or join by code.");
            }
        }

        if (ImGui.Button("+ Create Room##bgmcreate", new Vector2(-1, 0)))
        { _createName = ""; _createPw = ""; _pendingCreate = true; }

        ImGui.SetNextItemWidth(90 * scale);
        ImGui.InputTextWithHint("##bgmjoincode", "Code", ref _joinCode, 16);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-60 * scale);
        ImGui.InputTextWithHint("##bgmjoinpw", "Password (if any)", ref _joinPw, 64, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_joinCode)))
            if (ImGui.Button("Join##bgmjoin", new Vector2(50 * scale, 0)))
            {
                OpenRoom(_joinCode.Trim(), string.IsNullOrEmpty(_joinPw) ? null : _joinPw);
                _joinCode = ""; _joinPw = "";
            }

        DrawCreateModal();
    }

    private void DrawCreateModal()
    {
        if (!_createOpen) return;
        ImGui.SetNextWindowSize(new Vector2(320 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("##bgm_create", ref _createOpen, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)) return;

        bool scoped = ScopeCampaign() != null;
        ImGui.TextUnformatted("Create BGM Room");
        ImGui.Separator(); ImGui.Spacing();
        ImGui.TextDisabled(scoped ? "Visible to everyone in your active campaign." : "Solo room — share the code to let others join.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Name:");     ImGui.SetNextItemWidth(-1); ImGui.InputText("##bgmcname", ref _createName, 64);
        ImGui.TextUnformatted("Password (optional):"); ImGui.SetNextItemWidth(-1); ImGui.InputText("##bgmcpw", ref _createPw, 64, ImGuiInputTextFlags.Password);

        ImGui.Spacing();
        float bw = 100 * ImGuiHelpers.GlobalScale;
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_createName)))
            if (ImGui.Button("Create##bgmdocreate", new Vector2(bw, 0)))
            {
                _ = plugin.Network.RoomCreate(_createName.Trim(), string.IsNullOrEmpty(_createPw) ? null : _createPw, ScopeCampaign());
                _createOpen = false; ImGui.CloseCurrentPopup();
            }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##bgmcancelcreate", new Vector2(bw, 0))) { _createOpen = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
