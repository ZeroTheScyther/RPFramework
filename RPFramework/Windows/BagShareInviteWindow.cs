using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using RPFramework.Models;
using RPFramework.Models.Net;

namespace RPFramework.Windows;

public class BagShareInviteWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private readonly record struct Invite(SharedBagDto Bag, string FromId, string FromName);
    private readonly List<Invite> invites = new();

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

    public void AddInvite(SharedBagDto bag, string fromId, string fromName)
        => invites.Add(new Invite(bag, fromId, fromName));

    public override void Draw()
    {
        if (invites.Count == 0) { IsOpen = false; return; }

        for (int i = invites.Count - 1; i >= 0; i--)
        {
            var inv = invites[i];
            ImGui.PushID($"##baginv{inv.Bag.BagId}");

            ImGui.TextUnformatted($"{inv.FromName} wants to share a bag with you:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), $"\"{inv.Bag.Name}\"");
            ImGui.TextDisabled($"  ({inv.Bag.Items.Count} item{(inv.Bag.Items.Count == 1 ? "" : "s")})");

            float btnW = 100 * ImGuiHelpers.GlobalScale;

            if (ImGui.Button($"Accept##baginv{inv.Bag.BagId}", new Vector2(btnW, 0)))
            {
                AcceptInvite(inv);
                invites.RemoveAt(i);
                ImGui.PopID();
                continue;
            }
            ImGui.SameLine();
            if (ImGui.Button($"Decline##baginv{inv.Bag.BagId}", new Vector2(btnW, 0)))
            {
                _ = plugin.Network.RejectBagShare(inv.Bag.BagId);
                invites.RemoveAt(i);
                ImGui.PopID();
                continue;
            }

            if (i > 0) ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void AcceptInvite(Invite inv)
    {
        // Create a local RpBag from the snapshot
        var bag = new RpBag
        {
            Id          = inv.Bag.BagId,
            Name        = inv.Bag.Name,
            SharedOwner = inv.Bag.OwnerPlayerId,
            Gil         = inv.Bag.Gil,
        };
        foreach (var dto in inv.Bag.Items)
            bag.Items.Add(Plugin.DtoToItem(dto));

        plugin.Configuration.Bags.Add(bag);
        plugin.Configuration.SharedBags.Add(new SharedBagRef
        {
            BagId         = inv.Bag.BagId,
            OwnerPlayerId = inv.Bag.OwnerPlayerId,
            IsOwner       = false,
        });
        plugin.Configuration.Save();

        _ = plugin.Network.AcceptBagShare(inv.Bag.BagId);
    }
}
