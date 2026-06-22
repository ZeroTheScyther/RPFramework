using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace RPFramework;

/// <summary>
/// Server-first client config. The client owns NO canonical game state anymore — characters,
/// parties, templates, bags, and rooms all live on the server and arrive via hydration. This
/// holds only what the client legitimately keeps locally: the relay URL, the anti-spoof identity
/// secret, the last active campaign, and UI preferences.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    // Bumped to 1 for the server-first rewrite. Older (v0) configs are ignored and replaced
    // with defaults on load (clean break — no legacy migration).
    public int Version { get; set; } = 1;

    /// <summary>Relay server URL.</summary>
    public string ServerUrl { get; set; } = "https://rpframework.fantasy-tales.uk";

    /// <summary>
    /// Per-install random secret that binds this player identity to this client on the server,
    /// preventing another client from claiming the same "Name@World". Generated once on first run.
    /// </summary>
    public string IdentitySecret { get; set; } = "";

    /// <summary>The campaign the player last had active (the world they embody). May be the personal scope.</summary>
    public string? ActiveCampaignCode { get; set; }

    // ── UI preferences (client-local) ─────────────────────────────────────────
    public float BgmVolume { get; set; } = 0.5f;

    /// <summary>Per-campaign inventory tab order (bag ids). Tab arrangement is a personal preference,
    /// so it's client-local — a shared DM inventory can sit in a different spot for each player.</summary>
    public Dictionary<string, List<Guid>> InventoryOrder { get; set; } = new();

    /// <summary>Per-campaign active companion (campaign code → companion entity id). Which companion is
    /// "out" is a personal choice swapped in the RPNPC vault; only it populates the Companions tab.</summary>
    public Dictionary<string, string> ActiveCompanions { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
