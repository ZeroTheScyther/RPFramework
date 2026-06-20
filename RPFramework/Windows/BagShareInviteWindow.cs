using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

public class BagShareInviteWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly List<BagShareInviteDto> invites = new();

    public BagShareInviteWindow(Plugin plugin)
        : base("Shared Bag Invite##RPFramework.BagInvite",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        IsOpen      = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(280, 80),
            MaximumSize = new Vector2(460, 400),
        };
    }

    public void Dispose() { }

    public void AddInvite(BagShareInviteDto invite) => invites.Add(invite);

    public override void Draw()
    {
        if (invites.Count == 0) { IsOpen = false; return; }

        for (int i = invites.Count - 1; i >= 0; i--)
        {
            var inv = invites[i];
            ImGui.PushID($"##baginv{inv.BagId}");

            ImGui.TextUnformatted($"{inv.OwnerDisplayName} wants to share a bag with you:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), $"\"{inv.BagName}\"");

            float btnW = 100 * ImGuiHelpers.GlobalScale;

            if (ImGui.Button($"Accept##baginv{inv.BagId}", new Vector2(btnW, 0)))
            {
                _ = plugin.Network.BagShareAccept(inv.BagId);
                invites.RemoveAt(i);
                ImGui.PopID();
                continue;
            }
            ImGui.SameLine();
            if (ImGui.Button($"Decline##baginv{inv.BagId}", new Vector2(btnW, 0)))
            {
                _ = plugin.Network.BagShareDecline(inv.BagId);
                invites.RemoveAt(i);
                ImGui.PopID();
                continue;
            }

            if (i > 0) ImGui.Separator();
            ImGui.PopID();
        }
    }
}
