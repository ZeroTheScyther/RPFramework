using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Models;
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
        if (pendingCreate) { createOpen = true; ImGui.OpenPopup("##bgm_create"); pendingCreate = false; }

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
                            rooms.RemoveAt(i);
                            if (selectedRoom >= rooms.Count) selectedRoom = rooms.Count - 1;
                            plugin.Configuration.Save();
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.EndPopup();
                    }

                    ImGui.PopID();
                }

                if (rooms.Count == 0)
                    ImGui.TextDisabled("No rooms. Click '+ Create Room' to start.");
            }
        }

        // Create room button
        if (ImGui.Button("+ Create Room##bgmcreate", new Vector2(-1, 0)))
        {
            newRoomName = string.Empty;
            pendingCreate = true;
        }

        DrawCreateModal(rooms);
    }

    private void DrawCreateModal(List<RpRoom> rooms)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

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
            ImGui.CloseCurrentPopup();
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel##bgmroomcancel", new Vector2(96, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }
}
