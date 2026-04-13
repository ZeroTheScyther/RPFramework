using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models;
using RPFramework.Models.Net;
using RPFramework.Services;

namespace RPFramework.Windows;

public class BgmWindow : Window, IDisposable
{
    private readonly Plugin          plugin;
    private readonly BgmService      bgmService;
    private readonly BgmPlayerWindow playerWindow;

    private int    selectedRoom = -1;
    private string newRoomName  = string.Empty;
    private bool   pendingCreate;
    private bool   createOpen   = true;
    private string joinCode     = string.Empty;
    private string joinName     = string.Empty;
    private bool   pendingJoin;
    private bool   joinOpen     = true;

    public BgmWindow(Plugin plugin, BgmService bgmService, BgmPlayerWindow playerWindow)
        : base("RP BGM##RPFramework.BGM")
    {
        this.plugin       = plugin;
        this.bgmService   = bgmService;
        this.playerWindow = playerWindow;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(260, 280),
            MaximumSize = new Vector2(600, 800),
        };
        Size          = new Vector2(300, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!plugin.Network.IsConnected)
        {
            DrawNotConnected();
            return;
        }

        if (pendingCreate) { createOpen = true; ImGui.OpenPopup("##bgm_create"); pendingCreate = false; }
        if (pendingJoin)   { joinOpen   = true; ImGui.OpenPopup("##bgm_join");   pendingJoin   = false; }

        var rooms = plugin.Configuration.Rooms;
        if (selectedRoom >= rooms.Count) selectedRoom = rooms.Count - 1;

        // Room code display
        if (selectedRoom >= 0 && selectedRoom < rooms.Count)
        {
            var room = rooms[selectedRoom];
            ImGui.TextDisabled("Room Code:");
            ImGui.SameLine();
            ImGui.TextUnformatted(room.Code);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy##bgmcode"))
                ImGui.SetClipboardText(room.Code);
        }
        else
        {
            ImGui.TextDisabled("Select or create a room");
        }

        ImGui.Separator();

        // Room list
        float listH = ImGui.GetContentRegionAvail().Y
                      - ImGui.GetFrameHeightWithSpacing()
                      - ImGui.GetStyle().ItemSpacing.Y;

        using (var child = ImRaii.Child("##bgmroomlist", new Vector2(-1, listH), true))
        {
            if (child)
            {
                for (int i = 0; i < rooms.Count; i++)
                {
                    var room = rooms[i];
                    ImGui.PushID($"##bgmr{room.Id}");

                    bool selected = selectedRoom == i;
                    if (ImGui.Selectable(room.Name, selected,
                        ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        selectedRoom = i;
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        {
                            playerWindow.OpenRoom(room, bgmService);
                            playerWindow.IsOpen = true;
                        }
                    }

                    // Right-click → delete room
                    if (ImGui.BeginPopupContextItem("##bgmrctx"))
                    {
                        if (ImGui.MenuItem("Delete room"))
                        {
                            if (bgmService.CurrentRoom == room) bgmService.Stop();
                            string roomCode = room.Code;
                            rooms.RemoveAt(i);
                            if (selectedRoom >= rooms.Count) selectedRoom = rooms.Count - 1;
                            plugin.Configuration.Save();
                            // If connected, broadcast deletion to all room members (server enforces owner-only)
                            if (plugin.Network.IsConnected)
                                Task.Run(() => plugin.Network.BgmDeleteAsync(roomCode));
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.EndPopup();
                    }

                    ImGui.PopID();
                }

                if (rooms.Count == 0)
                    ImGui.TextDisabled("No rooms. Click '+ Create Room' to start.");

                DrawPartyRooms();
            }
        }

        // Create / Join buttons
        float btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        if (ImGui.Button("+ Create##bgmcreate", new Vector2(btnW, 0)))
        {
            newRoomName = string.Empty;
            pendingCreate = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("+ Join##bgmjoin", new Vector2(btnW, 0)))
        {
            joinCode = string.Empty;
            joinName = string.Empty;
            pendingJoin = true;
        }

        DrawCreateModal(rooms);
        DrawJoinModal(rooms);
    }

    private void DrawPartyRooms()
    {
        // Collect all room codes currently occupied by party members (not self)
        string? localId = plugin.LocalPlayerId;
        var partyRooms = new List<(string RoomCode, string MemberName, string PartyName)>();

        foreach (var party in plugin.Configuration.Parties)
        {
            if (!plugin.PartyMembers.TryGetValue(party.Code, out var members)) continue;
            foreach (var member in members)
            {
                if (member.PlayerId == localId) continue;
                foreach (var roomCode in member.BgmRoomCodes)
                {
                    // Skip rooms the local player is already in
                    if (plugin.Configuration.Rooms.Any(r => r.Code == roomCode)) continue;
                    partyRooms.Add((roomCode, member.DisplayName, party.Name));
                }
            }
        }

        if (partyRooms.Count == 0) return;

        ImGui.Spacing();
        ImGui.Separator();

        bool expanded = ImGui.TreeNodeEx("##partyroomsnode",
            ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen,
            $"Party Rooms ({partyRooms.Count})");
        if (!expanded) return;

        foreach (var (roomCode, memberName, partyName) in partyRooms)
        {
            ImGui.TextDisabled($"  {memberName}");
            ImGui.SameLine();
            ImGui.TextUnformatted(roomCode);
            ImGui.SameLine();
            ImGui.TextDisabled($"({partyName})");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Join##{roomCode}_{memberName}"))
            {
                var code = roomCode;
                var name = $"Room {roomCode}";
                var room = new RpRoom { Name = name, Code = code };
                plugin.Configuration.Rooms.Add(room);
                selectedRoom = plugin.Configuration.Rooms.Count - 1;
                plugin.Configuration.Save();
                Task.Run(() => plugin.Network.BgmJoinAsync(code));
                playerWindow.OpenRoom(room, bgmService);
                playerWindow.IsOpen = true;
            }
        }

        ImGui.TreePop();
    }

    private void DrawNotConnected()
    {
        var avail  = ImGui.GetContentRegionAvail();
        float lineH = ImGui.GetTextLineHeightWithSpacing();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + avail.Y / 2 - lineH);

        const string msg = "Not connected to server";
        ImGui.SetCursorPosX((avail.X - ImGui.CalcTextSize(msg).X) / 2);
        ImGui.TextDisabled(msg);

        float btnW = 120 * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX((avail.X - btnW) / 2);
        if (ImGui.Button("Reconnect##bgmreconnect", new Vector2(btnW, 0)))
        {
            string url  = plugin.Configuration.ServerUrl;
            string? id  = plugin.LocalPlayerId;
            string name = plugin.LocalDisplayName;
            if (id != null)
                Task.Run(() => plugin.Network.ConnectAsync(url, id, name));
        }
    }

    private void DrawJoinModal(List<RpRoom> rooms)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        float w0 = 300 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSizeConstraints(new Vector2(w0, 0), new Vector2(w0, float.MaxValue));

        if (!ImGui.BeginPopupModal("##bgm_join", ref joinOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        ImGui.TextUnformatted("Join Room");
        ImGui.Separator();

        ImGui.TextDisabled("Room Code");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##bgmjoincode", ref joinCode, 16);
        joinCode = joinCode.Trim().ToUpperInvariant();

        ImGui.TextDisabled("Local Name (optional)");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        bool enter = ImGui.InputText("##bgmjoinname", ref joinName, 48,
            ImGuiInputTextFlags.EnterReturnsTrue);

        bool canJoin = joinCode.Length > 0;
        if (!canJoin) ImGui.BeginDisabled();
        if ((ImGui.Button("Join##bgmjoinok", new Vector2(96, 0)) || enter) && canJoin)
        {
            string name = string.IsNullOrWhiteSpace(joinName) ? $"Room {joinCode}" : joinName.Trim();
            var room = new RpRoom { Name = name, Code = joinCode };
            rooms.Add(room);
            selectedRoom = rooms.Count - 1;
            plugin.Configuration.Save();
            playerWindow.OpenRoom(room, bgmService);
            playerWindow.IsOpen = true;
            ImGui.CloseCurrentPopup();
        }
        if (!canJoin) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel##bgmjoincancel", new Vector2(96, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawCreateModal(List<RpRoom> rooms)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        float w0 = 300 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSizeConstraints(new Vector2(w0, 0), new Vector2(w0, float.MaxValue));

        if (!ImGui.BeginPopupModal("##bgm_create", ref createOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        ImGui.TextUnformatted("Create Room");
        ImGui.Separator();

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        bool enter = ImGui.InputText("##bgmroomname", ref newRoomName, 48,
            ImGuiInputTextFlags.EnterReturnsTrue);

        bool canCreate = !string.IsNullOrWhiteSpace(newRoomName);
        if (!canCreate) ImGui.BeginDisabled();
        if ((ImGui.Button("Create##bgmroomok", new Vector2(96, 0)) || enter) && canCreate)
        {
            var room = new RpRoom { Name = newRoomName.Trim() };
            rooms.Add(room);
            selectedRoom = rooms.Count - 1;
            plugin.Configuration.Save();
            string code = room.Code;
            Task.Run(() => plugin.Network.BgmJoinAsync(code, isCreator: true));
            playerWindow.OpenRoom(room, bgmService, autoJoin: false);
            playerWindow.IsOpen = true;
            ImGui.CloseCurrentPopup();
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel##bgmroomcancel", new Vector2(96, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }
}
