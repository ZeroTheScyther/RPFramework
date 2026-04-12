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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
