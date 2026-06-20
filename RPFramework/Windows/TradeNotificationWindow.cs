using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;

namespace RPFramework.Windows;

public class TradeNotificationWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly List<TradeOfferDto> offers = new();

    public TradeNotificationWindow(Plugin plugin)
        : base("Incoming Trade##RPFramework.Trade", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        IsOpen      = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 100),
            MaximumSize = new Vector2(480, 600),
        };
    }

    public void Dispose() { }

    public void AddOffer(TradeOfferDto offer) => offers.Add(offer);

    public override void Draw()
    {
        if (offers.Count == 0) { IsOpen = false; return; }

        for (int i = offers.Count - 1; i >= 0; i--)
        {
            var offer = offers[i];
            ImGui.PushID($"##tradeoff{offer.OfferId}");

            ImGui.TextUnformatted($"{offer.FromDisplayName} wants to give you:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"{offer.Item.Amount}x {offer.Item.Name}");
            if (offer.IsCopy)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(copy - they keep theirs)");
            }

            if (!string.IsNullOrWhiteSpace(offer.Item.Description))
            {
                ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
                ImGui.TextDisabled(offer.Item.Description);
                ImGui.PopTextWrapPos();
            }

            float btnW = 100 * ImGuiHelpers.GlobalScale;

            if (ImGui.Button($"Accept##trad{offer.OfferId}", new Vector2(btnW, 0)))
            {
                _ = plugin.Network.TradeAccept(offer.OfferId);
                offers.RemoveAt(i);
                ImGui.PopID();
                continue;
            }
            ImGui.SameLine();
            if (ImGui.Button($"Reject##trad{offer.OfferId}", new Vector2(btnW, 0)))
            {
                _ = plugin.Network.TradeDecline(offer.OfferId);
                offers.RemoveAt(i);
                ImGui.PopID();
                continue;
            }

            if (i > 0) ImGui.Separator();
            ImGui.PopID();
        }
    }
}
