using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using RPFramework.Models;

namespace RPFramework;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int         Version   { get; set; } = 0;
    public List<RpBag> Bags      { get; set; } = new();
    public List<RpRoom> Rooms    { get; set; } = new();
    public int         Gil       { get; set; } = 0;

    // Networking
    public string ServerUrl { get; set; } = "https://rpframework.example.com";

    // Shared bags — persisted so we can re-join on plugin reload
    public List<SharedBagRef> SharedBags { get; set; } = new();

    // Per-character RPG stats, keyed by "Name@World"
    public Dictionary<string, RpCharacter> Characters { get; set; } = new();

    // Parties — persisted for offline display; member lists are live from server
    public List<RpParty> Parties { get; set; } = new();

    // Fellow Adventurers — 1:1 individual sync pairs, stored as "Name@World" IDs
    public List<string> FellowAdventurers { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
