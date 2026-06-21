using System;
using System.Collections.Generic;
using System.Linq;
using RPFramework.Contracts;

namespace RPFramework.Services;

/// <summary>
/// The client's authoritative local mirror of server state. The client owns NO canonical
/// data — this is a cache populated from the server's hydration <see cref="SnapshotDto"/> and
/// kept current by per-aggregate authoritative updates. Windows read from here every frame
/// (ImGui is immediate-mode) and never mutate it; mutations only ever arrive from the server.
///
/// All mutations must happen on the Dalamud framework thread (NetworkService marshals callbacks
/// there) so window reads see a consistent picture.
/// </summary>
public sealed class RpStateStore
{
    private readonly Dictionary<string, PartyDto>             _parties     = new();   // by campaign code
    private readonly Dictionary<string, TemplateDto>          _templates   = new();   // by campaign code
    private readonly Dictionary<string, InitiativeStateDto>   _initiatives = new();   // by campaign code
    private readonly Dictionary<(string, string), CharacterDto> _characters = new();  // by (code, entityId)
    private readonly Dictionary<Guid, BagDto>                 _bags        = new();   // by bag id
    private readonly Dictionary<string, RoomStateDto>         _rooms       = new();   // by room code

    /// <summary>The campaign the player is currently embodying (drives sheet, dice, initiative).</summary>
    public string? ActiveCampaign { get; set; }

    /// <summary>Fired after any change. Mostly informational — windows re-read each frame regardless.</summary>
    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    // ── Reads ─────────────────────────────────────────────────────────────────

    public IReadOnlyCollection<PartyDto> Parties => _parties.Values.ToList();
    public PartyDto? Party(string? code) => code != null && _parties.TryGetValue(code, out var p) ? p : null;

    public TemplateDto? Template(string? code) => code != null && _templates.TryGetValue(code, out var t) ? t : null;
    public SheetTemplate TemplateOrDefault(string? code) => Template(code)?.Template ?? SheetTemplate.Default();

    public CharacterDto? Character(string code, string entityId)
        => _characters.TryGetValue((code, entityId), out var c) ? c : null;
    public IEnumerable<CharacterDto> CharactersIn(string code)
        => _characters.Values.Where(c => c.PartyCode == code);

    /// <summary>Companion entities in a campaign owned by the given player.</summary>
    public IEnumerable<CharacterDto> CompanionsOf(string code, string ownerPlayerId)
        => _characters.Values.Where(c => c.PartyCode == code
                                      && c.Kind == EntityKind.Companion
                                      && c.OwnerPlayerId == ownerPlayerId);

    /// <summary>NPC entities visible to this client in a campaign (DmOnly NPCs are already filtered server-side).</summary>
    public IEnumerable<CharacterDto> NpcsIn(string code)
        => _characters.Values.Where(c => c.PartyCode == code && c.Kind == EntityKind.Npc);

    public InitiativeStateDto? Initiative(string? code) => code != null && _initiatives.TryGetValue(code, out var i) ? i : null;
    public IReadOnlyCollection<InitiativeStateDto> Initiatives => _initiatives.Values.ToList();

    public IReadOnlyCollection<BagDto> Bags => _bags.Values.ToList();
    public BagDto? Bag(Guid id) => _bags.TryGetValue(id, out var b) ? b : null;
    public IEnumerable<BagDto> BagsIn(string code) => _bags.Values.Where(b => b.CampaignCode == code);

    public IReadOnlyCollection<RoomStateDto> Rooms => _rooms.Values.ToList();
    public RoomStateDto? Room(string? code) => code != null && _rooms.TryGetValue(code, out var r) ? r : null;

    /// <summary>True once at least one party (campaign) is known — i.e. hydration has happened.</summary>
    public bool Hydrated { get; private set; }

    // ── Full hydration ──────────────────────────────────────────────────────────

    public void ApplySnapshot(SnapshotDto s)
    {
        _parties.Clear(); _templates.Clear(); _initiatives.Clear();
        _characters.Clear(); _bags.Clear(); _rooms.Clear();

        foreach (var p in s.Parties)     _parties[p.Code]     = p;
        foreach (var t in s.Templates)   _templates[t.PartyCode] = t;
        foreach (var c in s.Characters)  _characters[(c.PartyCode, c.EntityId)] = c;
        foreach (var i in s.Initiatives) _initiatives[i.PartyCode] = i;
        foreach (var b in s.Bags)        _bags[b.BagId]       = b;
        foreach (var r in s.Rooms)       _rooms[r.Code]       = r;

        Hydrated = true;
        Raise();
    }

    public void Clear()
    {
        _parties.Clear(); _templates.Clear(); _initiatives.Clear();
        _characters.Clear(); _bags.Clear(); _rooms.Clear();
        Hydrated = false;
        Raise();
    }

    // ── Per-aggregate updates (version-checked: stale/out-of-order messages dropped) ──

    public void ApplyParty(PartyDto p)
    {
        if (_parties.TryGetValue(p.Code, out var cur) && cur.Version > p.Version) return;
        _parties[p.Code] = p;
        Raise();
    }

    public void RemoveParty(string code)
    {
        bool any = _parties.Remove(code);
        _templates.Remove(code);
        _initiatives.Remove(code);
        foreach (var key in _characters.Keys.Where(k => k.Item1 == code).ToList()) _characters.Remove(key);
        if (ActiveCampaign == code) ActiveCampaign = null;
        if (any) Raise();
    }

    public void ApplyCharacter(CharacterDto c)
    {
        var key = (c.PartyCode, c.EntityId);
        if (_characters.TryGetValue(key, out var cur) && cur.Version > c.Version) return;
        _characters[key] = c;
        Raise();
    }

    public void RemoveCharacter(string code, string entityId)
    {
        if (_characters.Remove((code, entityId))) Raise();
    }

    public void ApplyTemplate(TemplateDto t)
    {
        if (_templates.TryGetValue(t.PartyCode, out var cur) && cur.Version > t.Version) return;
        _templates[t.PartyCode] = t;
        Raise();
    }

    public void ApplyInitiative(InitiativeStateDto i)
    {
        if (_initiatives.TryGetValue(i.PartyCode, out var cur) && cur.Version > i.Version) return;
        _initiatives[i.PartyCode] = i;
        Raise();
    }

    public void RemoveInitiative(string code)
    {
        if (_initiatives.Remove(code)) Raise();
    }

    public void ApplyBag(BagDto b)
    {
        if (_bags.TryGetValue(b.BagId, out var cur) && cur.Version > b.Version) return;
        _bags[b.BagId] = b;
        Raise();
    }

    public void RemoveBag(Guid id)
    {
        if (_bags.Remove(id)) Raise();
    }

    public void ApplyRoom(RoomStateDto r)
    {
        if (_rooms.TryGetValue(r.Code, out var cur) && cur.Version > r.Version) return;
        _rooms[r.Code] = r;
        Raise();
    }

    public void RemoveRoom(string code)
    {
        if (_rooms.Remove(code)) Raise();
    }
}
