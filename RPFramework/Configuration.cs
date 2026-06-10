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

    // Per-party sheet templates, keyed by party code.
    // Populated when a DM publishes a template; falls back to SheetTemplate.Default() if absent.
    public Dictionary<string, SheetTemplate> PartyTemplates { get; set; } = new();

    // Per-party character data, keyed by "{partyCode}/{playerId}".
    public Dictionary<string, RpCharacter> PartyCharacters { get; set; } = new();

    // Which party is currently "active" — used by context menu, dice, skills, and profile push.
    public string? ActivePartyCode { get; set; }

    // Global/fallback template — kept for users not yet in any party and for legacy saves.
    // Newtonsoft.Json is the actual serializer (Dalamud config); use its attributes.
    [Newtonsoft.Json.JsonIgnore]
    public SheetTemplate ActiveTemplate
    {
        get => _activeTemplate ??= SheetTemplate.Default();
        set => _activeTemplate = value;
    }

    // Newtonsoft serializes this as "ActiveTemplateSerialized". On deserialization
    // the getter returns null (backing field not yet set), so Newtonsoft creates a
    // fresh SheetTemplate from JSON rather than merging into a pre-populated Default().
    public SheetTemplate? ActiveTemplateSerialized
    {
        get => _activeTemplate;
        set => _activeTemplate = value;
    }

    [Newtonsoft.Json.JsonIgnore]
    private SheetTemplate? _activeTemplate;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
