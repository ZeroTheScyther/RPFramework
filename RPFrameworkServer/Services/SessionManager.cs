using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RPFramework.Contracts;
using RPFrameworkServer.Data;
using RPFrameworkServer.Models;

namespace RPFrameworkServer.Services;

/// <summary>
/// Authoritative orchestrator over the database (durable state) and the live in-memory
/// layer (presence, initiative, trades). Server-first: every aggregate is owned here and
/// every mutation bumps a version and returns the new snapshot DTO for the hub to broadcast.
/// Injected as a singleton; DB access is via short-lived contexts from the factory.
/// </summary>
public class SessionManager
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly RollService                     _rolls;
    private readonly ILogger<SessionManager>         _log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Live state (never persisted) ──────────────────────────────────────────
    private readonly ConcurrentDictionary<string, PlayerPresence>  _presence    = new(); // playerId → presence
    private readonly ConcurrentDictionary<string, string>          _connToPlayer = new(); // connId  → playerId
    private readonly ConcurrentDictionary<string, string>          _activeRoom   = new(); // connId  → BGM room code this connection is actively listening to
    private readonly ConcurrentDictionary<string, Encounter>      _encounters  = new();  // encounterId → encounter
    private readonly ConcurrentDictionary<Guid, TradeOffer>        _trades      = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim>   _locks       = new();  // per-campaign write lock
    private readonly ConcurrentDictionary<string, PrepareGate>     _gates       = new();  // room code → in-flight "waiting for members" gate

    public SessionManager(IDbContextFactory<AppDbContext> dbFactory, RollService rolls, ILogger<SessionManager> log)
    {
        _dbFactory = dbFactory;
        _rolls     = rolls;
        _log       = log;
    }

    private SemaphoreSlim LockFor(string key) => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    // ═════════════════════════════════════════════════════════════════════════
    // Presence
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Binds a connection to a player. Returns true if this is the player's first connection (came online).</summary>
    public bool AddConnection(string playerId, string displayName, string connectionId)
    {
        _connToPlayer[connectionId] = playerId;
        var p = _presence.GetOrAdd(playerId, _ => new PlayerPresence { PlayerId = playerId });
        lock (p)
        {
            p.DisplayName = displayName;
            bool wasOffline = p.Connections.Count == 0;
            p.Connections.Add(connectionId);
            return wasOffline;
        }
    }

    /// <summary>Unbinds a connection. Returns (playerId, wentOffline) so the hub can broadcast presence.</summary>
    public (string? PlayerId, bool WentOffline) RemoveConnection(string connectionId)
    {
        _activeRoom.TryRemove(connectionId, out _);
        if (!_connToPlayer.TryRemove(connectionId, out var playerId)) return (null, false);
        if (!_presence.TryGetValue(playerId, out var p)) return (playerId, false);
        lock (p)
        {
            p.Connections.Remove(connectionId);
            return (playerId, p.Connections.Count == 0);
        }
    }

    public bool IsOnline(string playerId) => _presence.TryGetValue(playerId, out var p) && p.IsOnline;

    /// <summary>Connection ids currently held by a player (for targeted sends).</summary>
    public IReadOnlyCollection<string> ConnectionsOf(string playerId)
        => _presence.TryGetValue(playerId, out var p) ? p.Connections.ToArray() : Array.Empty<string>();

    /// <summary>Declares (or clears, when code is empty) which BGM room a connection is actively listening to.</summary>
    public void SetActiveRoom(string connectionId, string? code)
    {
        if (string.IsNullOrEmpty(code)) _activeRoom.TryRemove(connectionId, out _);
        else                            _activeRoom[connectionId] = code;
    }

    /// <summary>True when any of the player's connections has declared this room as its active listening room.</summary>
    private bool IsActiveInRoom(string playerId, string code)
    {
        if (!_presence.TryGetValue(playerId, out var p)) return false;
        string[] conns; lock (p) conns = p.Connections.ToArray();
        return conns.Any(c => _activeRoom.TryGetValue(c, out var rc) && rc == code);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Players (identity + anti-spoof secret)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Binds (or first-registers) a player's secret. Returns true if the secret matches /
    /// was newly registered; false if it mismatches a previously-registered secret (spoof).
    /// </summary>
    public async Task<bool> AuthenticateAsync(string playerId, string secret, string displayName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var player = await db.Players.FindAsync(playerId);
        if (player == null)
        {
            db.Players.Add(new PlayerEntity { PlayerId = playerId, Secret = secret, DisplayName = displayName });
            await db.SaveChangesAsync();
            return true;
        }
        if (!string.IsNullOrEmpty(player.Secret) && player.Secret != secret)
            return false;

        player.Secret      = secret; // adopt secret if previously empty
        player.DisplayName = displayName;
        await db.SaveChangesAsync();
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // JSON + DTO mapping helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static SheetTemplate TemplateOf(CampaignEntity c)
        => string.IsNullOrEmpty(c.TemplateJson)
            ? SheetTemplate.Default()
            : JsonSerializer.Deserialize<SheetTemplate>(c.TemplateJson, Json) ?? SheetTemplate.Default();

    private static CharacterState StateOf(CharacterEntity e)
        => string.IsNullOrEmpty(e.StateJson)
            ? new CharacterState()
            : JsonSerializer.Deserialize<CharacterState>(e.StateJson, Json) ?? new CharacterState();

    private static List<RpItemDto> ItemsOf(BagEntity b)
        => JsonSerializer.Deserialize<List<RpItemDto>>(b.ItemsJson, Json) ?? new();

    private static List<string> ParticipantsOf(BagEntity b)
        => JsonSerializer.Deserialize<List<string>>(b.ParticipantsJson, Json) ?? new();

    /// <summary>
    /// Returns a NEW item list with <paramref name="mutate"/> applied to the container addressed by
    /// <paramref name="path"/> (a chain of bag-item ids to descend; empty = the inventory root).
    /// Records are immutable, so nested changes are threaded back up via <c>with</c>. Returns null if
    /// the path is invalid (missing id, or a non-bag item in the chain).
    /// </summary>
    private static List<RpItemDto>? MutateContainer(List<RpItemDto> list, Guid[] path, Action<List<RpItemDto>> mutate, int from = 0)
    {
        var result = new List<RpItemDto>(list);
        if (from >= path.Length) { mutate(result); return result; }

        int idx = result.FindIndex(i => i.Id == path[from]);
        if (idx < 0 || result[idx].Type != RpItemType.Bag) return null;
        var child = MutateContainer(result[idx].Contents ?? new(), path, mutate, from + 1);
        if (child == null) return null;
        result[idx] = result[idx] with { Contents = child };
        return result;
    }

    /// <summary>How many items a container may hold: a nested bag's own Capacity (clamped to the hard
    /// cap), or <see cref="Limits.BagItemsMax"/> at the inventory root.</summary>
    private static int ContainerCapacity(List<RpItemDto> root, Guid[] path)
    {
        if (path.Length == 0) return Limits.BagItemsMax;
        var bag = FindItem(root, path[..^1], path[^1]);
        if (bag == null || bag.Type != RpItemType.Bag) return 0;
        return Math.Min(Limits.BagItemsMax, Math.Max(0, bag.Capacity));
    }

    private CharacterDto ToCharacterDto(CharacterEntity e)
        => new(e.CampaignCode, e.EntityId, e.DisplayName, StateOf(e), e.Version, e.Kind, e.OwnerPlayerId, e.Visibility);

    /// <summary>
    /// Returns a copy of the character with DM-only skills removed from its State.Skills, for recipients
    /// who are neither the owner nor a DM. True server-side privacy: non-DMs never receive the data (the
    /// client UI also hides them, but that alone leaves the data on every machine). Item-embedded passives
    /// (Equipment[*].GrantedPassives) are deliberately left intact — they are independent IsDmSkill=false
    /// copies that must keep flowing via trade/equip. Returns the original when there's nothing to strip.
    /// </summary>
    public static CharacterDto StripDmSkills(CharacterDto dto)
    {
        var s = dto.State;
        if (s.Skills.Count == 0 || s.Skills.TrueForAll(sk => !sk.IsDmSkill)) return dto;
        // New State object with a filtered Skills list; the other (read-only during send) collections are
        // shared by reference — we never mutate dto's own Skills, so the full copy stays intact.
        var clone = new CharacterState
        {
            StatValues  = s.StatValues,
            CheckValues = s.CheckValues,
            TextValues  = s.TextValues,
            Equipment   = s.Equipment,
            Skills      = s.Skills.Where(sk => !sk.IsDmSkill).ToList(),
        };
        return dto with { State = clone };
    }

    /// <summary>Campaign members paired with whether they may see DM-only content (Owner or CoDm).</summary>
    public async Task<List<(string PlayerId, bool IsDm)>> CampaignViewersAsync(string code)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var members = await db.CampaignMembers.Where(m => m.Code == code)
            .Select(m => new { m.PlayerId, m.Role }).ToListAsync();
        return members.Select(m => (m.PlayerId, m.Role is PartyRole.Owner or PartyRole.CoDm)).ToList();
    }

    /// <summary>
    /// Every character of a campaign as a specific viewer should see it: DM-only entities hidden from
    /// non-DMs, and DM-only skills stripped from characters the viewer doesn't own. Used to re-push a
    /// player's view after their role changes (promotion grants, demotion revokes DM-skill visibility).
    /// </summary>
    public async Task<List<CharacterDto>> CharactersForViewerAsync(string code, string viewerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        bool isDm = await db.CampaignMembers.AnyAsync(m =>
            m.Code == code && m.PlayerId == viewerId && (m.Role == PartyRole.Owner || m.Role == PartyRole.CoDm));

        var result = new List<CharacterDto>();
        foreach (var ch in await db.Characters.Where(c => c.CampaignCode == code).ToListAsync())
        {
            if (ch.Visibility == EntityVisibility.DmOnly && !isDm) continue;
            var dto = ToCharacterDto(ch);
            if (!isDm && ch.OwnerPlayerId != viewerId) dto = StripDmSkills(dto);
            result.Add(dto);
        }
        return result;
    }

    private static TemplateDto ToTemplateDto(CampaignEntity c)
        => new(c.Code, TemplateOf(c), c.TemplateVersion);

    private BagDto ToBagDto(BagEntity b)
        => new(b.BagId, b.CampaignCode, b.OwnerPlayerId, b.Name, ItemsOf(b), ParticipantsOf(b), b.Version);

    private PartyDto ToPartyDto(CampaignEntity c, List<CampaignMemberEntity> members)
        => new(
            c.Code, c.Name, c.OwnerPlayerId,
            members.Select(m => new PartyMemberDto(
                m.PlayerId,
                _presence.TryGetValue(m.PlayerId, out var p) ? p.DisplayName : m.PlayerId,
                m.Role,
                IsOnline(m.PlayerId),
                new List<string>())).ToList(),
            c.ShowHpAp,
            c.IsPersonal,
            c.Version);

    // ═════════════════════════════════════════════════════════════════════════
    // Campaigns
    // ═════════════════════════════════════════════════════════════════════════

    private static string NewCode()
        => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Ensures the player's personal "Individual Adventurer" campaign exists; returns its code.</summary>
    public async Task<string> EnsurePersonalCampaignAsync(string playerId, string displayName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var personal = await db.Campaigns.FirstOrDefaultAsync(c => c.IsPersonal && c.OwnerPlayerId == playerId);
        if (personal != null) return personal.Code;

        string code = NewCode();
        var template = SheetTemplate.Default();
        db.Campaigns.Add(new CampaignEntity
        {
            Code           = code,
            Name           = "Individual Adventurer",
            OwnerPlayerId  = playerId,
            IsPersonal     = true,
            ShowHpAp       = true,
            TemplateJson   = JsonSerializer.Serialize(template, Json),
            TemplateVersion= 1,
            Version        = 1,
        });
        db.CampaignMembers.Add(new CampaignMemberEntity { Code = code, PlayerId = playerId, Role = PartyRole.Owner });
        db.Characters.Add(NewCharacterRow(code, playerId, displayName, template));
        await db.SaveChangesAsync();
        return code;
    }

    private static CharacterEntity NewCharacterRow(string code, string playerId, string displayName, SheetTemplate template)
    {
        // Seed a fresh sheet: Number fields start at their Min; bars/checkboxes left for the player to fill.
        var state = new CharacterState();
        foreach (var g in template.Groups)
            foreach (var f in g.Fields)
                if (f.Type == FieldType.Number)
                    state.StatValues[f.Id] = f.Min;
        return new CharacterEntity
        {
            CampaignCode  = code,
            EntityId      = playerId,
            OwnerPlayerId = playerId,
            Kind          = EntityKind.PlayerCharacter,
            Visibility    = EntityVisibility.PartyVisible,
            DisplayName   = displayName,
            StateJson     = JsonSerializer.Serialize(state, Json),
            Version       = 1,
        };
    }

    public async Task<(PartyDto Party, CharacterDto Character, TemplateDto Template)?> CreateCampaignAsync(
        string playerId, string displayName, string name, string? password)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.Campaigns.CountAsync() >= Limits.TotalParties) return null;

        string code = NewCode();
        var template = SheetTemplate.Default();
        var campaign = new CampaignEntity
        {
            Code            = code,
            Name            = InputSanitizer.SanitizeName(name, Limits.NameMax),
            OwnerPlayerId   = playerId,
            HashedPassword  = string.IsNullOrEmpty(password) ? "" : InputSanitizer.HashPassword(password),
            ShowHpAp        = true,
            TemplateJson    = JsonSerializer.Serialize(template, Json),
            TemplateVersion = 1,
            Version         = 1,
        };
        db.Campaigns.Add(campaign);
        db.CampaignMembers.Add(new CampaignMemberEntity { Code = code, PlayerId = playerId, Role = PartyRole.Owner });
        var ch = NewCharacterRow(code, playerId, displayName, template);
        db.Characters.Add(ch);
        await db.SaveChangesAsync();

        var members = await db.CampaignMembers.Where(m => m.Code == code).ToListAsync();
        return (ToPartyDto(campaign, members), ToCharacterDto(ch), ToTemplateDto(campaign));
    }

    public enum JoinResult { Ok, NotFound, BadPassword, Full, AlreadyMember }

    public async Task<(JoinResult Result, PartyDto? Party, CharacterDto? Character, TemplateDto? Template)> JoinCampaignAsync(
        string playerId, string displayName, string code, string? password)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var campaign = await db.Campaigns.FindAsync(code);
            if (campaign == null || campaign.IsPersonal) return (JoinResult.NotFound, null, null, null);
            if (!string.IsNullOrEmpty(campaign.HashedPassword)
                && !InputSanitizer.VerifyPassword(password ?? "", campaign.HashedPassword))
                return (JoinResult.BadPassword, null, null, null);

            var members = await db.CampaignMembers.Where(m => m.Code == code).ToListAsync();
            if (members.Any(m => m.PlayerId == playerId))
                return (JoinResult.AlreadyMember, null, null, null);
            if (members.Count >= Limits.MembersPerParty) return (JoinResult.Full, null, null, null);

            db.CampaignMembers.Add(new CampaignMemberEntity { Code = code, PlayerId = playerId, Role = PartyRole.Member });

            // Session zero: build a fresh character against the DM's template (no import).
            var template = TemplateOf(campaign);
            CharacterEntity ch;
            var existing = await db.Characters.FindAsync(code, playerId);
            if (existing != null) ch = existing; // rejoin keeps prior sheet
            else { ch = NewCharacterRow(code, playerId, displayName, template); db.Characters.Add(ch); }

            campaign.Version++;
            await db.SaveChangesAsync();

            members = await db.CampaignMembers.Where(m => m.Code == code).ToListAsync();
            return (JoinResult.Ok, ToPartyDto(campaign, members), ToCharacterDto(ch), ToTemplateDto(campaign));
        }
        finally { gate.Release(); }
    }

    /// <summary>Removes a member. Returns the updated party, or null if the campaign no longer exists / was disbanded.</summary>
    public async Task<PartyDto?> LeaveCampaignAsync(string code, string playerId)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var campaign = await db.Campaigns.FindAsync(code);
            if (campaign == null || campaign.IsPersonal) return null;

            var member = await db.CampaignMembers.FindAsync(code, playerId);
            if (member == null) return null;
            db.CampaignMembers.Remove(member);
            var ch = await db.Characters.FindAsync(code, playerId);
            if (ch != null) db.Characters.Remove(ch);
            // Drop the player's memberships in this campaign's rooms so they don't follow them out of the
            // campaign as stale duplicates (rooms they own are dissolved entirely below).
            await PurgeCampaignRoomMembershipAsync(db, code, playerId);
            campaign.Version++;
            await db.SaveChangesAsync();

            var members = await db.CampaignMembers.Where(m => m.Code == code).ToListAsync();
            return ToPartyDto(campaign, members);
        }
        finally { gate.Release(); }
    }

    /// <summary>Disbands a campaign (owner only). Returns the member ids that need to be notified.</summary>
    public async Task<List<string>?> DisbandCampaignAsync(string code, string requesterId)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var campaign = await db.Campaigns.FindAsync(code);
            if (campaign == null || campaign.IsPersonal || campaign.OwnerPlayerId != requesterId) return null;

            var members = await db.CampaignMembers.Where(m => m.Code == code).Select(m => m.PlayerId).ToListAsync();
            db.CampaignMembers.RemoveRange(db.CampaignMembers.Where(m => m.Code == code));
            db.Characters.RemoveRange(db.Characters.Where(m => m.CampaignCode == code));
            db.Bags.RemoveRange(db.Bags.Where(b => b.CampaignCode == code));
            // Tear down this campaign's BGM rooms too (rooms + songs + memberships), or they orphan with a
            // dangling CampaignCode and keep showing to ex-members as stale duplicates.
            var roomCodes = await db.BgmRooms.Where(r => r.CampaignCode == code).Select(r => r.Code).ToListAsync();
            if (roomCodes.Count > 0)
            {
                db.BgmSongs.RemoveRange(db.BgmSongs.Where(s => roomCodes.Contains(s.RoomCode)));
                db.BgmRoomMembers.RemoveRange(db.BgmRoomMembers.Where(m => roomCodes.Contains(m.RoomCode)));
                db.BgmRooms.RemoveRange(db.BgmRooms.Where(r => r.CampaignCode == code));
            }
            db.Campaigns.Remove(campaign);
            await db.SaveChangesAsync();
            foreach (var eid in _encounters.Where(kv => kv.Value.CampaignCode == code).Select(kv => kv.Key).ToList())
                _encounters.TryRemove(eid, out _);
            return members;
        }
        finally { gate.Release(); }
    }

    public async Task<PartyDto?> SetRoleAsync(string code, string requesterId, string targetId, PartyRole role)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var campaign = await db.Campaigns.FindAsync(code);
            if (campaign == null || campaign.OwnerPlayerId != requesterId) return null;
            if (role == PartyRole.Owner) return null; // ownership transfer not via this path
            var target = await db.CampaignMembers.FindAsync(code, targetId);
            if (target == null || target.PlayerId == campaign.OwnerPlayerId) return null;
            target.Role = role;
            campaign.Version++;
            await db.SaveChangesAsync();
            var members = await db.CampaignMembers.Where(m => m.Code == code).ToListAsync();
            return ToPartyDto(campaign, members);
        }
        finally { gate.Release(); }
    }

    public async Task<PartyDto?> SetShowHpApAsync(string code, string requesterId, bool show)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var campaign = await db.Campaigns.FindAsync(code);
            if (campaign == null) return null;
            if (!await IsDmAsync(db, code, requesterId)) return null;
            campaign.ShowHpAp = show;
            campaign.Version++;
            await db.SaveChangesAsync();
            var members = await db.CampaignMembers.Where(m => m.Code == code).ToListAsync();
            return ToPartyDto(campaign, members);
        }
        finally { gate.Release(); }
    }

    public async Task<List<string>> MemberIdsAsync(string code)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CampaignMembers.Where(m => m.Code == code).Select(m => m.PlayerId).ToListAsync();
    }

    public async Task<bool> IsMemberAsync(string code, string playerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CampaignMembers.AnyAsync(m => m.Code == code && m.PlayerId == playerId);
    }

    private static async Task<bool> IsDmAsync(AppDbContext db, string code, string playerId)
    {
        var m = await db.CampaignMembers.FindAsync(code, playerId);
        return m is { Role: PartyRole.Owner or PartyRole.CoDm };
    }

    public async Task<bool> IsDmAsync(string code, string playerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await IsDmAsync(db, code, playerId);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Characters + templates
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Whether <paramref name="callerId"/> may edit the given entity: a player edits only their
    /// own PC; a companion is editable by its owner or any DM; an NPC is editable by any DM.</summary>
    private static async Task<bool> CanEditEntityAsync(AppDbContext db, string code, string callerId, CharacterEntity entity)
    {
        if (entity.Kind == EntityKind.PlayerCharacter) return entity.EntityId == callerId;
        if (entity.Kind == EntityKind.Companion)       return entity.OwnerPlayerId == callerId || await IsDmAsync(db, code, callerId);
        if (entity.Kind == EntityKind.Npc)             return await IsDmAsync(db, code, callerId);
        return false;
    }

    /// <summary>Applies a mutation to an entity's state, gated by <see cref="CanEditEntityAsync"/>. Returns
    /// null if the entity is missing or the caller may not edit it.</summary>
    public async Task<CharacterDto?> MutateEntityAsync(string code, string callerId, string entityId, Action<CharacterState> mutate)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var ch = await db.Characters.FindAsync(code, entityId);
            if (ch == null || !await CanEditEntityAsync(db, code, callerId, ch)) return null;
            var state = StateOf(ch);
            mutate(state);
            var clean = InputSanitizer.SanitizeState(state) ?? state;
            ch.StateJson = JsonSerializer.Serialize(clean, Json);
            ch.Version++;
            await db.SaveChangesAsync();
            return ToCharacterDto(ch);
        }
        finally { gate.Release(); }
    }

    public Task<CharacterDto?> EditStatAsync(string code, string callerId, string entityId, string key, int value)
        => MutateEntityAsync(code, callerId, entityId, s => s.StatValues[key] = value);

    public Task<CharacterDto?> EditCheckAsync(string code, string callerId, string entityId, string fieldId, bool value)
        => MutateEntityAsync(code, callerId, entityId, s => s.CheckValues[fieldId] = value);

    public Task<CharacterDto?> EditTextAsync(string code, string callerId, string entityId, string fieldId, string value)
        => MutateEntityAsync(code, callerId, entityId, s => s.TextValues[fieldId] = value);

    public Task<CharacterDto?> SetSkillsAsync(string code, string callerId, string entityId, List<RpSkill> skills)
        => MutateEntityAsync(code, callerId, entityId, s =>
        {
            // The client owns skill DEFINITIONS; the server owns runtime state. Carry forward the
            // toggle/cooldown/duration of any skill that still exists (matched by Id) so a definition
            // edit never silently disables a toggled passive or resets a cooldown. A removed skill is
            // simply dropped (and, since passives contribute only live, its effect disappears with it).
            var prev = s.Skills.ToDictionary(k => k.Id);
            foreach (var sk in skills)
                if (prev.TryGetValue(sk.Id, out var old))
                {
                    sk.Active            = old.Active;
                    sk.CooldownRemaining = old.CooldownRemaining;
                    sk.DurationRemaining = old.DurationRemaining;
                }
            s.Skills = skills;
        });

    // ── Entity lifecycle (companions / NPCs) ──────────────────────────────────

    /// <summary>Creates a companion (owned by the caller) or an NPC (DM-only, campaign-owned). PCs are
    /// auto-created on join and cannot be made here. Returns null if not permitted or over the cap.</summary>
    public async Task<CharacterDto?> CreateEntityAsync(string code, string callerId, EntityKind kind, string name)
    {
        if (kind == EntityKind.PlayerCharacter) return null;
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            if (await db.CampaignMembers.FindAsync(code, callerId) == null) return null;   // must be a member
            bool isDm = await IsDmAsync(db, code, callerId);
            if (kind == EntityKind.Npc && !isDm) return null;                              // only DMs make NPCs

            int existing = kind == EntityKind.Companion
                ? await db.Characters.CountAsync(c => c.CampaignCode == code && c.Kind == EntityKind.Companion && c.OwnerPlayerId == callerId)
                : await db.Characters.CountAsync(c => c.CampaignCode == code && c.Kind == EntityKind.Npc);
            int cap = kind == EntityKind.Companion ? Limits.CompanionsPerPlayer : Limits.NpcsPerCampaign;
            if (existing >= cap) return null;

            var ch = new CharacterEntity
            {
                CampaignCode  = code,
                EntityId      = Guid.NewGuid().ToString("N"),
                OwnerPlayerId = kind == EntityKind.Companion ? callerId : "",
                Kind          = kind,
                Visibility    = EntityVisibility.PartyVisible,
                DisplayName   = name,
                StateJson     = "",
                Version       = 1,
            };
            db.Characters.Add(ch);
            await db.SaveChangesAsync();
            return ToCharacterDto(ch);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Imports an exported character (base64 code) into the campaign as a Companion or, for DMs, an NPC.
    /// Companions are owned by the caller; NPCs are DM-owned. Always given a fresh id and clean combat
    /// runtime. Gated like <see cref="CreateEntityAsync"/> (member, DM-for-NPC, per-kind cap).
    /// </summary>
    public async Task<CharacterDto?> ImportEntityAsync(string code, string callerId, string exportCode, EntityKind kind)
    {
        if (kind == EntityKind.PlayerCharacter) return null;
        if (!CompanionCodec.TryDecode(exportCode, out var export)) return null;

        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            if (await db.CampaignMembers.FindAsync(code, callerId) == null) return null;   // must be a member
            bool isDm = await IsDmAsync(db, code, callerId);
            if (kind == EntityKind.Npc && !isDm) return null;                              // only DMs make NPCs

            int existing = kind == EntityKind.Companion
                ? await db.Characters.CountAsync(c => c.CampaignCode == code && c.Kind == EntityKind.Companion && c.OwnerPlayerId == callerId)
                : await db.Characters.CountAsync(c => c.CampaignCode == code && c.Kind == EntityKind.Npc);
            int cap = kind == EntityKind.Companion ? Limits.CompanionsPerPlayer : Limits.NpcsPerCampaign;
            if (existing >= cap) return null;

            // Start the import clean: no carried-over cooldown/duration/toggle from its old home.
            var state = export.State;
            foreach (var sk in state.Skills)
            {
                sk.CooldownRemaining = 0;
                sk.DurationRemaining = 0;
                sk.Active            = false;
            }
            var clean = InputSanitizer.SanitizeState(state) ?? new CharacterState();

            var ch = new CharacterEntity
            {
                CampaignCode  = code,
                EntityId      = Guid.NewGuid().ToString("N"),
                OwnerPlayerId = kind == EntityKind.Companion ? callerId : "",
                Kind          = kind,
                Visibility    = EntityVisibility.PartyVisible,
                DisplayName   = InputSanitizer.SanitizeName(export.DisplayName),
                StateJson     = JsonSerializer.Serialize(clean, Json),
                Version       = 1,
            };
            db.Characters.Add(ch);
            await db.SaveChangesAsync();
            return ToCharacterDto(ch);
        }
        finally { gate.Release(); }
    }

    /// <summary>Renames a companion/NPC (or a PC's display name). Gated by <see cref="CanEditEntityAsync"/>.</summary>
    public async Task<CharacterDto?> RenameEntityAsync(string code, string callerId, string entityId, string name)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var ch = await db.Characters.FindAsync(code, entityId);
            if (ch == null || !await CanEditEntityAsync(db, code, callerId, ch)) return null;
            ch.DisplayName = name;
            ch.Version++;
            await db.SaveChangesAsync();
            return ToCharacterDto(ch);
        }
        finally { gate.Release(); }
    }

    /// <summary>Deletes a companion/NPC. PCs cannot be deleted. Returns true if removed.</summary>
    public async Task<bool> DeleteEntityAsync(string code, string callerId, string entityId)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var ch = await db.Characters.FindAsync(code, entityId);
            if (ch == null || ch.Kind == EntityKind.PlayerCharacter) return false;
            if (!await CanEditEntityAsync(db, code, callerId, ch)) return false;
            db.Characters.Remove(ch);
            await db.SaveChangesAsync();
            return true;
        }
        finally { gate.Release(); }
    }

    /// <summary>Activates a skill: applies its effects + sets cooldown/duration (server-authoritative).</summary>
    public async Task<CharacterDto?> UseSkillAsync(string code, string callerId, string entityId, Guid skillId)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var campaign = await db.Campaigns.FindAsync(code);
            var ch = await db.Characters.FindAsync(code, entityId);
            if (campaign == null || ch == null || !await CanEditEntityAsync(db, code, callerId, ch)) return null;
            var state = StateOf(ch);
            var skill = state.Skills.FirstOrDefault(s => s.Id == skillId);
            if (skill == null) return null;
            if (skill.Type == SkillType.Passive)
            {
                // Passives toggle on/off; effects are applied live via the mod layer, never baked.
                skill.Active = !skill.Active;
            }
            else
            {
                if (skill.CooldownRemaining > 0 || skill.DurationRemaining > 0) return null;
                StatMath.ApplyEffects(skill, state, TemplateOf(campaign));
            }
            ch.StateJson = JsonSerializer.Serialize(state, Json);
            ch.Version++;
            await db.SaveChangesAsync();
            return ToCharacterDto(ch);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Uses a Consumable from a bag: applies its effects to the requester's character in that campaign
    /// (instantaneous, via the skill engine) and consumes one from the stack. Both the character and the
    /// bag are updated atomically in one transaction. Returns the updated snapshots to broadcast.
    /// </summary>
    public async Task<(CharacterDto Character, BagDto Bag)?> UseItemAsync(Guid bagId, string requesterId, Guid[] path, Guid itemId)
    {
        var bagGate = LockFor("bag:" + bagId);
        await bagGate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bag = await db.Bags.FindAsync(bagId);
            if (bag == null || !ParticipantsOf(bag).Contains(requesterId)) return null;
            string code = bag.CampaignCode;

            var item = FindItem(ItemsOf(bag), path, itemId);
            if (item == null || item.Type != RpItemType.Consumable) return null;

            var charGate = LockFor(code);
            await charGate.WaitAsync();
            try
            {
                var campaign = await db.Campaigns.FindAsync(code);
                var ch = await db.Characters.FindAsync(code, requesterId);
                if (campaign == null || ch == null) return null;

                // Apply the consumable's effects instantly (Duration 0 = permanent application) via the skill engine.
                var state = StateOf(ch);
                if (item.Effects is { Count: > 0 } || item.Blocks is { Count: > 0 })
                {
                    var transient = new RpSkill
                    {
                        Effects           = item.Effects ?? new(),
                        ConditionalBlocks = item.Blocks  ?? new(),
                        Duration          = 0,
                        Cooldown          = 0,
                    };
                    StatMath.ApplyEffects(transient, state, TemplateOf(campaign));
                    ch.StateJson = JsonSerializer.Serialize(state, Json);
                    ch.Version++;
                }

                // Consume one from the stack.
                var items = MutateContainer(ItemsOf(bag), path, c =>
                {
                    int idx = c.FindIndex(i => i.Id == itemId);
                    if (idx < 0) return;
                    var it = c[idx];
                    if (it.Amount <= 1) c.RemoveAt(idx);
                    else                c[idx] = it with { Amount = it.Amount - 1 };
                });
                if (items != null) { bag.ItemsJson = JsonSerializer.Serialize(items, Json); bag.Version++; }

                await db.SaveChangesAsync();
                return (ToCharacterDto(ch), ToBagDto(bag));
            }
            finally { charGate.Release(); }
        }
        finally { bagGate.Release(); }
    }

    /// <summary>
    /// Equips an item from a bag into the requester's character slot (the slot is the item's own
    /// equip category). Any item already in that slot is returned to the same container, so the move
    /// is slot-for-slot. Character + bag are updated atomically; returns both to broadcast.
    /// </summary>
    public async Task<(CharacterDto Character, BagDto Bag)?> EquipItemAsync(Guid bagId, string requesterId, Guid[] path, Guid itemId)
    {
        var bagGate = LockFor("bag:" + bagId);
        await bagGate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bag = await db.Bags.FindAsync(bagId);
            if (bag == null || !ParticipantsOf(bag).Contains(requesterId)) return null;
            string code = bag.CampaignCode;

            var item = FindItem(ItemsOf(bag), path, itemId);
            if (item == null || !item.Type.IsEquippable()) return null;
            var slot = item.Type;

            var charGate = LockFor(code);
            await charGate.WaitAsync();
            try
            {
                var ch = await db.Characters.FindAsync(code, requesterId);
                if (ch == null) return null;
                var state = StateOf(ch);
                state.Equipment.TryGetValue(slot, out var previous);

                // Pull the item out of its container; drop any previously-equipped item back in its place.
                bool removed = false;
                var items = MutateContainer(ItemsOf(bag), path, c =>
                {
                    int idx = c.FindIndex(i => i.Id == itemId);
                    if (idx < 0) return;
                    c.RemoveAt(idx); removed = true;
                    if (previous != null) c.Add(previous);
                });
                if (items == null || !removed) return null;

                state.Equipment[slot] = item with { Contents = null };
                ch.StateJson  = JsonSerializer.Serialize(state, Json); ch.Version++;
                bag.ItemsJson = JsonSerializer.Serialize(items, Json); bag.Version++;
                await db.SaveChangesAsync();
                return (ToCharacterDto(ch), ToBagDto(bag));
            }
            finally { charGate.Release(); }
        }
        finally { bagGate.Release(); }
    }

    /// <summary>
    /// Unequips the item in <paramref name="slot"/> back into a bag's root (requester must participate
    /// in that bag, which must be in the same campaign). Character + bag are updated atomically.
    /// </summary>
    public async Task<(CharacterDto Character, BagDto Bag)?> UnequipItemAsync(string code, string requesterId, RpItemType slot, Guid toBagId)
    {
        var bagGate = LockFor("bag:" + toBagId);
        await bagGate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bag = await db.Bags.FindAsync(toBagId);
            if (bag == null || bag.CampaignCode != code || !ParticipantsOf(bag).Contains(requesterId)) return null;

            var charGate = LockFor(code);
            await charGate.WaitAsync();
            try
            {
                var ch = await db.Characters.FindAsync(code, requesterId);
                if (ch == null) return null;
                var state = StateOf(ch);
                if (!state.Equipment.TryGetValue(slot, out var item) || item == null) return null;

                var items = ItemsOf(bag);
                if (items.Count >= Limits.BagItemsMax) return null; // destination full
                items.Add(item);

                state.Equipment.Remove(slot);
                ch.StateJson  = JsonSerializer.Serialize(state, Json); ch.Version++;
                bag.ItemsJson = JsonSerializer.Serialize(items, Json); bag.Version++;
                await db.SaveChangesAsync();
                return (ToCharacterDto(ch), ToBagDto(bag));
            }
            finally { charGate.Release(); }
        }
        finally { bagGate.Release(); }
    }

    /// <summary>Publishes a new template for a campaign (DM only). Returns the new template snapshot.</summary>
    public async Task<TemplateDto?> PublishTemplateAsync(string code, string requesterId, SheetTemplate template)
    {
        var gate = LockFor(code);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var campaign = await db.Campaigns.FindAsync(code);
            if (campaign == null || campaign.IsPersonal) return null; // personal ruleset is locked
            if (!await IsDmAsync(db, code, requesterId)) return null;
            var clean = InputSanitizer.SanitizeTemplate(template);
            if (clean == null) return null;
            campaign.TemplateJson = JsonSerializer.Serialize(clean, Json);
            campaign.TemplateVersion++;
            await db.SaveChangesAsync();
            return ToTemplateDto(campaign);
        }
        finally { gate.Release(); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Dice (server-authoritative via RollService)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Rolls for an entity (the caller's own PC, one of their companions, or — for a DM — an NPC).
    /// The roll is attributed to the entity's name. Returns null if the caller may not roll as that entity.</summary>
    public async Task<DiceRollResultDto?> RollAsync(
        string code, string callerId, string entityId, string fallbackName,
        int die, RollMode mode, string? statFieldId, string? specFieldId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var campaign = await db.Campaigns.FindAsync(code);
        if (campaign == null) return null;
        var ch = await db.Characters.FindAsync(code, entityId);
        // Same gate as editing: your own PC, your companion, or any NPC if you're a DM. A missing entity
        // (e.g. a PC not yet created) rolls an empty state under the caller's own name.
        if (ch != null && !await CanEditEntityAsync(db, code, callerId, ch)) return null;
        var    state = ch != null ? StateOf(ch) : new CharacterState();
        string name  = ch != null && ch.DisplayName.Length > 0 ? ch.DisplayName : fallbackName;
        return _rolls.Roll(code, callerId, name, die, mode, statFieldId, specFieldId, state, TemplateOf(campaign));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Hydration
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Builds the full snapshot a player can see: their campaigns + every member's character + templates + bags.</summary>
    public async Task<SnapshotDto> BuildSnapshotAsync(string playerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var myCodes = await db.CampaignMembers.Where(m => m.PlayerId == playerId)
                                              .Select(m => m.Code).ToListAsync();

        var parties     = new List<PartyDto>();
        var characters  = new List<CharacterDto>();
        var templates   = new List<TemplateDto>();
        var encounters  = new List<EncounterDto>();

        foreach (var code in myCodes)
        {
            var campaign = await db.Campaigns.FindAsync(code);
            if (campaign == null) continue;
            var members = await db.CampaignMembers.Where(m => m.Code == code).ToListAsync();
            parties.Add(ToPartyDto(campaign, members));
            templates.Add(ToTemplateDto(campaign));
            bool isDm = members.Any(m => m.PlayerId == playerId && m.Role is PartyRole.Owner or PartyRole.CoDm);
            foreach (var ch in await db.Characters.Where(c => c.CampaignCode == code).ToListAsync())
            {
                if (ch.Visibility == EntityVisibility.DmOnly && !isDm) continue;   // hidden NPCs stay DM-only
                var dto = ToCharacterDto(ch);
                if (!isDm && ch.OwnerPlayerId != playerId) dto = StripDmSkills(dto); // hide DM-only skills from non-DM viewers
                characters.Add(dto);
            }
            foreach (var enc in _encounters.Values.Where(e => e.CampaignCode == code))
                encounters.Add(BuildEncounterDto(enc));
        }

        var bags = await db.Bags
            .Where(b => myCodes.Contains(b.CampaignCode) && b.ParticipantsJson.Contains(playerId))
            .ToListAsync();

        var rooms = new List<RoomStateDto>();
        var seenRooms = new HashSet<string>();
        // Campaign rooms are visible to every member of the campaign — no join required to discover them.
        foreach (var room in await db.BgmRooms.Where(r => myCodes.Contains(r.CampaignCode)).ToListAsync())
        {
            rooms.Add(await BuildRoomDtoAsync(db, room));
            seenRooms.Add(room.Code);
        }
        // Rooms the player is a persistent member of (e.g. joined by code from the solo scope).
        // Campaign-scoped rooms are gated solely by *current* campaign membership above — a lingering
        // BgmRoomMembers row must never resurface a campaign room after the player left that campaign,
        // or the room follows them around as a stale duplicate (different code than the live room).
        var memberCodes = await db.BgmRoomMembers.Where(m => m.PlayerId == playerId).Select(m => m.RoomCode).ToListAsync();
        foreach (var code in memberCodes)
        {
            if (!seenRooms.Add(code)) continue;
            var room = await db.BgmRooms.FindAsync(code);
            if (room == null || !string.IsNullOrEmpty(room.CampaignCode)) continue;
            rooms.Add(await BuildRoomDtoAsync(db, room));
        }

        return new SnapshotDto(
            parties, characters, templates,
            bags.Select(ToBagDto).ToList(),
            rooms,
            encounters);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Initiative encounters (ephemeral, server-owned; multiple per campaign)
    // ═════════════════════════════════════════════════════════════════════════

    private static EncounterDto BuildEncounterDto(Encounter e)
        => new(
            e.CampaignCode, e.EncounterId, e.Name,
            e.Entries.Select(x => new EncounterEntryDto(
                x.EntityId, x.DisplayName, x.Roll, x.Bonus, x.Total,
                x.HpCurrent, x.HpMax, x.ApCurrent, x.ApMax, x.IsNpc)).ToList(),
            e.CurrentIndex, e.ShowHpAp, e.Version);

    private static void Resort(Encounter e)
        => e.Entries.Sort((a, b) => b.Total != a.Total ? b.Total - a.Total : b.Roll - a.Roll);

    /// <summary>Builds an encounter entry from a character entity: rolls d24 + initiative bonus and
    /// snapshots gear-aware HP/AP from the entity's current state.</summary>
    private static EncounterEntry RollEntry(CharacterEntity ch, SheetTemplate template)
    {
        var st = StateOf(ch);
        var hp = template.FindHpBar();
        var ap = template.FindApBar();
        return new EncounterEntry
        {
            EntityId    = ch.EntityId,
            DisplayName = ch.DisplayName,
            Roll        = Random.Shared.Next(1, 25), // d24
            Bonus       = StatMath.InitiativeBonus(st, template),
            HpCurrent   = hp != null && st.StatValues.TryGetValue(hp.Id + ":cur", out var hc) ? hc : 0,
            HpMax       = hp != null ? StatMath.EffectiveBarMax(st, hp, template) : 0,
            ApCurrent   = ap != null && st.StatValues.TryGetValue(ap.Id + ":cur", out var ac) ? ac : 0,
            ApMax       = ap != null ? StatMath.EffectiveBarMax(st, ap, template) : 0,
            IsNpc       = ch.Kind == EntityKind.Npc,
        };
    }

    /// <summary>DM creates a new, empty named encounter. Players then join it (or are added) and roll.</summary>
    public async Task<EncounterDto?> CreateEncounterAsync(string code, string requesterId, string name)
    {
        if (!await IsDmAsync(code, requesterId)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var campaign = await db.Campaigns.FindAsync(code);
        if (campaign == null) return null;
        if (_encounters.Values.Count(e => e.CampaignCode == code) >= Limits.EncountersPerCampaign) return null;

        string clean = InputSanitizer.SanitizeName(name);
        var enc = new Encounter
        {
            EncounterId  = Guid.NewGuid().ToString("N"),
            CampaignCode = code,
            Name         = clean.Length > 0 ? clean : "Encounter",
            ShowHpAp     = campaign.ShowHpAp,
            Version      = 1,
        };
        _encounters[enc.EncounterId] = enc;
        return BuildEncounterDto(enc);
    }

    /// <summary>DM ends/deletes an encounter (its tracker disappears for everyone).</summary>
    public async Task<bool> DeleteEncounterAsync(string code, string requesterId, string encounterId)
    {
        if (!await IsDmAsync(code, requesterId)) return false;
        if (!_encounters.TryGetValue(encounterId, out var enc) || enc.CampaignCode != code) return false;
        return _encounters.TryRemove(encounterId, out _);
    }

    /// <summary>A player opts their own PC into an encounter (rolls on join).</summary>
    public Task<EncounterDto?> JoinEncounterAsync(string code, string requesterId, string encounterId)
        => AddEntityToEncounterAsync(code, requesterId, encounterId, requesterId);

    /// <summary>Adds a roster entity (PC / companion / vault NPC) to an encounter, rolling its initiative.
    /// Gated by <see cref="CanEditEntityAsync"/>: a player may add their own PC and companions; a DM may add anyone.</summary>
    public async Task<EncounterDto?> AddEntityToEncounterAsync(string code, string requesterId, string encounterId, string entityId)
    {
        if (!_encounters.TryGetValue(encounterId, out var enc) || enc.CampaignCode != code) return null;
        if (enc.Entries.Count >= Limits.InitiativeEntriesMax) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var campaign = await db.Campaigns.FindAsync(code);
        if (campaign == null) return null;
        var ch = await db.Characters.FindAsync(code, entityId);
        if (ch == null || !await CanEditEntityAsync(db, code, requesterId, ch)) return null;
        if (enc.Entries.Any(x => x.EntityId == entityId)) return BuildEncounterDto(enc); // already present
        enc.Entries.Add(RollEntry(ch, TemplateOf(campaign)));
        Resort(enc);
        enc.Version++;
        return BuildEncounterDto(enc);
    }

    /// <summary>A player removes their own PC from an encounter.</summary>
    public Task<EncounterDto?> LeaveEncounterAsync(string code, string requesterId, string encounterId)
    {
        if (!_encounters.TryGetValue(encounterId, out var enc) || enc.CampaignCode != code) return Task.FromResult<EncounterDto?>(null);
        if (enc.Entries.RemoveAll(x => x.EntityId == requesterId) == 0) return Task.FromResult<EncounterDto?>(null);
        if (enc.CurrentIndex >= enc.Entries.Count) enc.CurrentIndex = 0;
        enc.Version++;
        return Task.FromResult<EncounterDto?>(BuildEncounterDto(enc));
    }

    public async Task<(EncounterDto State, CharacterDto? Ticked)?> EndTurnAsync(string code, string requesterId, string encounterId)
    {
        if (!_encounters.TryGetValue(encounterId, out var enc) || enc.CampaignCode != code) return null;
        if (enc.Entries.Count == 0) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ending = enc.Entries[Math.Clamp(enc.CurrentIndex, 0, enc.Entries.Count - 1)];

        // The entity whose turn is ending (null for ad-hoc "npc:" entries that have no character).
        var ch = await db.Characters.FindAsync(code, ending.EntityId);

        // Members may only end a turn they own (their PC or one of their companions); DMs may end anyone's.
        if (!await IsDmAsync(db, code, requesterId))
            if (ch == null || !await CanEditEntityAsync(db, code, requesterId, ch)) return null;

        var campaign = await db.Campaigns.FindAsync(code);

        // End-of-turn upkeep for the entity whose turn is ending: tick cooldown/duration and fire
        // TriggerOnTurnEnd passives. Ad-hoc NPC entries have no character entity, so they are skipped.
        CharacterDto? ticked = null;
        if (campaign != null && ch != null)
        {
            var cs       = StateOf(ch);
            var template = TemplateOf(campaign);
            if (TickTurnEnd(cs, template))
            {
                ch.StateJson = JsonSerializer.Serialize(cs, Json);
                ch.Version++;
                await db.SaveChangesAsync();
                ticked = ToCharacterDto(ch);
            }
            SyncEntryVitals(ending, cs, template); // regen/heals reflect in the tracker
        }

        enc.CurrentIndex = (enc.CurrentIndex + 1) % enc.Entries.Count;
        enc.Version++;
        return (BuildEncounterDto(enc), ticked);
    }

    /// <summary>
    /// End-of-turn upkeep for one character: decrement every skill's cooldown/duration remaining, then
    /// fire each off-cooldown skill's turn-end effects (the legacy TriggerOnTurnEnd flag and any "On Turn
    /// End" conditional block - regen, dots, etc.). Returns true if anything changed (so the caller
    /// persists + broadcasts).
    /// </summary>
    private static bool TickTurnEnd(CharacterState state, SheetTemplate template)
    {
        bool changed = false;
        foreach (var skill in state.Skills)
        {
            if (skill.CooldownRemaining > 0) { skill.CooldownRemaining--; changed = true; }
            if (skill.DurationRemaining > 0) { skill.DurationRemaining--; changed = true; }
        }
        foreach (var skill in state.Skills)
            if (skill.CooldownRemaining == 0 && StatMath.ApplyTurnEndEffects(skill, state, template))
                changed = true;
        return changed;
    }

    /// <summary>Refreshes an initiative entry's HP/AP from the character's current state (gear-aware max).</summary>
    private static void SyncEntryVitals(EncounterEntry entry, CharacterState st, SheetTemplate template)
    {
        var hp = template.FindHpBar();
        var ap = template.FindApBar();
        if (hp != null)
        {
            if (st.StatValues.TryGetValue(hp.Id + ":cur", out var hc)) entry.HpCurrent = hc;
            entry.HpMax = StatMath.EffectiveBarMax(st, hp, template);
        }
        if (ap != null)
        {
            if (st.StatValues.TryGetValue(ap.Id + ":cur", out var ac)) entry.ApCurrent = ac;
            entry.ApMax = StatMath.EffectiveBarMax(st, ap, template);
        }
    }

    /// <summary>DM removes any entry from an encounter by its entity id.</summary>
    public async Task<EncounterDto?> RemoveEntryAsync(string code, string requesterId, string encounterId, string entityId)
    {
        if (!await IsDmAsync(code, requesterId)) return null;
        if (!_encounters.TryGetValue(encounterId, out var enc) || enc.CampaignCode != code) return null;
        if (enc.Entries.RemoveAll(e => e.EntityId == entityId) == 0) return null;
        if (enc.CurrentIndex >= enc.Entries.Count) enc.CurrentIndex = 0;
        enc.Version++;
        return BuildEncounterDto(enc);
    }

    /// <summary>DM adds an ad-hoc NPC combatant (no backing entity) to an encounter.</summary>
    public async Task<EncounterDto?> AddNpcAsync(string code, string requesterId, string encounterId, string name, int roll, int bonus, int hp, int ap)
    {
        if (!await IsDmAsync(code, requesterId)) return null;
        if (!_encounters.TryGetValue(encounterId, out var enc) || enc.CampaignCode != code) return null;
        if (enc.Entries.Count >= Limits.InitiativeEntriesMax) return null;
        enc.Entries.Add(new EncounterEntry
        {
            EntityId = "npc:" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = InputSanitizer.SanitizeName(name), Roll = roll, Bonus = bonus,
            HpCurrent = hp, HpMax = hp, ApCurrent = ap, ApMax = ap, IsNpc = true,
        });
        Resort(enc);
        enc.Version++;
        return BuildEncounterDto(enc);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Bags + trades
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<BagEntity?> LoadBagAsync(AppDbContext db, Guid bagId, string requesterId)
    {
        var bag = await db.Bags.FindAsync(bagId);
        if (bag == null) return null;
        if (!ParticipantsOf(bag).Contains(requesterId)) return null; // not a participant
        return bag;
    }

    public async Task<BagDto?> CreateBagAsync(string code, string ownerId, string name, bool isDmBag)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (!await db.CampaignMembers.AnyAsync(m => m.Code == code && m.PlayerId == ownerId)) return null;
        if (isDmBag && !await IsDmAsync(db, code, ownerId)) return null;
        if (await db.Bags.CountAsync(b => b.CampaignCode == code && b.OwnerPlayerId == ownerId) >= Limits.BagsPerOwner) return null;

        var participants = isDmBag
            ? await db.CampaignMembers.Where(m => m.Code == code && (m.Role == PartyRole.Owner || m.Role == PartyRole.CoDm))
                                      .Select(m => m.PlayerId).ToListAsync()
            : new List<string> { ownerId };

        var bag = new BagEntity
        {
            BagId = Guid.NewGuid(), CampaignCode = code,
            OwnerPlayerId = isDmBag ? "" : ownerId, IsDmBag = isDmBag,
            Name = InputSanitizer.SanitizeName(name),
            ItemsJson = "[]", ParticipantsJson = JsonSerializer.Serialize(participants, Json), Version = 1,
        };
        db.Bags.Add(bag);
        await db.SaveChangesAsync();
        return ToBagDto(bag);
    }

    public async Task<BagDto?> MutateBagAsync(Guid bagId, string requesterId, Action<BagEntity> mutate)
    {
        var gate = LockFor("bag:" + bagId);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bag = await LoadBagAsync(db, bagId, requesterId);
            if (bag == null) return null;
            mutate(bag);
            bag.Version++;
            await db.SaveChangesAsync();
            return ToBagDto(bag);
        }
        finally { gate.Release(); }
    }

    public Task<BagDto?> AddItemAsync(Guid bagId, string requesterId, Guid[] path, RpItemDto item)
        => MutateBagAsync(bagId, requesterId, b =>
        {
            if (path.Length >= Limits.ItemTreeDepthMax) return;
            var clean = InputSanitizer.SanitizeItem(item);
            if (clean == null) return;
            if (path.Length > 0 && clean.Type == RpItemType.Bag) return; // bags only at the inventory root
            int cap = ContainerCapacity(ItemsOf(b), path);
            var items = MutateContainer(ItemsOf(b), path, c =>
            { if (c.Count < cap) c.Add(clean with { Id = Guid.NewGuid() }); });
            if (items != null) b.ItemsJson = JsonSerializer.Serialize(items, Json);
        });

    public Task<BagDto?> RemoveItemAsync(Guid bagId, string requesterId, Guid[] path, Guid itemId)
        => MutateBagAsync(bagId, requesterId, b =>
        {
            var items = MutateContainer(ItemsOf(b), path, c => c.RemoveAll(i => i.Id == itemId));
            if (items != null) b.ItemsJson = JsonSerializer.Serialize(items, Json);
        });

    public Task<BagDto?> UpdateItemAsync(Guid bagId, string requesterId, Guid[] path, RpItemDto item)
        => MutateBagAsync(bagId, requesterId, b =>
        {
            var clean = InputSanitizer.SanitizeItem(item);
            if (clean == null) return;
            if (path.Length > 0 && clean.Type == RpItemType.Bag) return; // can't turn a nested item into a bag
            var items = MutateContainer(ItemsOf(b), path, c =>
            {
                int idx = c.FindIndex(i => i.Id == item.Id);
                // Preserve the target's existing nested contents — edits change name/icon/etc., never contents.
                if (idx >= 0) c[idx] = clean with { Contents = c[idx].Contents };
            });
            if (items != null) b.ItemsJson = JsonSerializer.Serialize(items, Json);
        });

    /// <summary>
    /// Moves an item between two containers (requester must participate in both bags). The containers
    /// may be the inventory root or a nested bag (addressed by path), in the same or different bags —
    /// this powers "take out", "put into bag", and cross-inventory moves. Removing before re-inserting
    /// makes moving a bag into its own subtree impossible (the id is gone), so cycles can't form.
    /// </summary>
    public async Task<(BagDto From, BagDto To)?> MoveItemAsync(
        Guid fromBag, Guid[] fromPath, Guid toBag, Guid[] toPath, string requesterId, Guid itemId)
    {
        if (toPath.Length >= Limits.ItemTreeDepthMax) return null;

        if (fromBag == toBag)
        {
            // Single-bag move (e.g. take an item out of a nested bag into the root) — one entity, one lock.
            var bag = await MutateBagAsync(fromBag, requesterId, b =>
            {
                RpItemDto? moved = null;
                var afterRemove = MutateContainer(ItemsOf(b), fromPath, c =>
                { int i = c.FindIndex(x => x.Id == itemId); if (i >= 0) { moved = c[i]; c.RemoveAt(i); } });
                if (afterRemove == null || moved == null) return;
                if (toPath.Length > 0 && moved.Type == RpItemType.Bag) return; // no bag-in-bag
                int cap = ContainerCapacity(afterRemove, toPath);
                bool added = false;
                var afterAdd = MutateContainer(afterRemove, toPath, c =>
                { if (c.Count < cap) { c.Add(moved); added = true; } });
                if (afterAdd == null || !added) return; // destination full or path broke — leave the item where it was
                b.ItemsJson = JsonSerializer.Serialize(afterAdd, Json);
            });
            return bag == null ? null : (bag, bag);
        }

        // Lock both bags in a stable order to avoid deadlock.
        var first  = fromBag.CompareTo(toBag) < 0 ? fromBag : toBag;
        var second = fromBag.CompareTo(toBag) < 0 ? toBag : fromBag;
        var g1 = LockFor("bag:" + first); var g2 = LockFor("bag:" + second);
        await g1.WaitAsync(); await g2.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var from = await db.Bags.FindAsync(fromBag);
            var to   = await db.Bags.FindAsync(toBag);
            if (from == null || to == null) return null;
            if (!ParticipantsOf(from).Contains(requesterId) || !ParticipantsOf(to).Contains(requesterId)) return null;

            RpItemDto? moved = null;
            var fromItems = MutateContainer(ItemsOf(from), fromPath, c =>
            { int i = c.FindIndex(x => x.Id == itemId); if (i >= 0) { moved = c[i]; c.RemoveAt(i); } });
            if (fromItems == null || moved == null) return null;
            if (toPath.Length > 0 && moved.Type == RpItemType.Bag) return null; // no bag-in-bag

            int cap = ContainerCapacity(ItemsOf(to), toPath);
            bool added = false;
            var toItems = MutateContainer(ItemsOf(to), toPath, c =>
            { if (c.Count < cap) { c.Add(moved); added = true; } });
            if (toItems == null || !added) return null; // destination full — abort, nothing persisted

            from.ItemsJson = JsonSerializer.Serialize(fromItems, Json); from.Version++;
            to.ItemsJson   = JsonSerializer.Serialize(toItems, Json);   to.Version++;
            await db.SaveChangesAsync();
            return (ToBagDto(from), ToBagDto(to));
        }
        finally { g2.Release(); g1.Release(); }
    }

    /// <summary>Splits <paramref name="amount"/> off a stackable item into a new stack in the same
    /// container: the original drops by amount, a copy of amount is added beside it.</summary>
    public Task<BagDto?> SplitItemAsync(Guid bagId, string requesterId, Guid[] path, Guid itemId, int amount)
        => MutateBagAsync(bagId, requesterId, b =>
        {
            int cap = ContainerCapacity(ItemsOf(b), path);
            var items = MutateContainer(ItemsOf(b), path, c =>
            {
                int idx = c.FindIndex(i => i.Id == itemId);
                if (idx < 0) return;
                var it = c[idx];
                if (!it.Type.IsStackable() || amount < 1 || amount >= it.Amount) return; // must leave >= 1
                if (c.Count >= cap) return; // splitting needs a free slot
                c[idx] = it with { Amount = it.Amount - amount };
                c.Add(it with { Id = Guid.NewGuid(), Amount = amount, Contents = null });
            });
            if (items != null) b.ItemsJson = JsonSerializer.Serialize(items, Json);
        });

    public Task<BagDto?> RenameBagAsync(Guid bagId, string requesterId, string name)
        => MutateBagAsync(bagId, requesterId, b => b.Name = InputSanitizer.SanitizeName(name));

    public async Task<(BagDto? Bag, List<string> Participants)?> DeleteBagAsync(Guid bagId, string requesterId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var bag = await db.Bags.FindAsync(bagId);
        if (bag == null) return null;
        bool allowed = bag.OwnerPlayerId == requesterId || (bag.IsDmBag && await IsDmAsync(db, bag.CampaignCode, requesterId));
        if (!allowed) return null;
        var participants = ParticipantsOf(bag);
        db.Bags.Remove(bag);
        await db.SaveChangesAsync();
        return (null, participants);
    }

    public async Task<List<string>> BagParticipantsAsync(Guid bagId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var bag = await db.Bags.FindAsync(bagId);
        return bag == null ? new() : ParticipantsOf(bag);
    }

    /// <summary>Builds an invite payload (inviter must be a participant of the bag).</summary>
    public async Task<BagShareInviteDto?> InviteToBagAsync(Guid bagId, string requesterId, string inviterDisplay)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var bag = await db.Bags.FindAsync(bagId);
        if (bag == null || !ParticipantsOf(bag).Contains(requesterId)) return null;
        return new BagShareInviteDto(bagId, requesterId, inviterDisplay, bag.Name);
    }

    /// <summary>Resolves who to notify when a share invite is declined: the bag's owner and its name.
    /// Returns null for owner-less (DM) bags or if the decliner is the owner.</summary>
    public async Task<(string OwnerId, string BagName)?> DeclineBagShareAsync(Guid bagId, string declinerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var bag = await db.Bags.FindAsync(bagId);
        if (bag == null || string.IsNullOrEmpty(bag.OwnerPlayerId) || bag.OwnerPlayerId == declinerId) return null;
        return (bag.OwnerPlayerId, bag.Name);
    }

    /// <summary>Adds a player to a bag's participants (they must be a member of the bag's campaign).</summary>
    public async Task<BagDto?> AcceptBagShareAsync(Guid bagId, string playerId)
    {
        var gate = LockFor("bag:" + bagId);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bag = await db.Bags.FindAsync(bagId);
            if (bag == null) return null;
            if (!await db.CampaignMembers.AnyAsync(m => m.Code == bag.CampaignCode && m.PlayerId == playerId)) return null;
            var participants = ParticipantsOf(bag);
            if (participants.Count >= Limits.BagParticipants) return null;
            if (!participants.Contains(playerId))
            {
                participants.Add(playerId);
                bag.ParticipantsJson = JsonSerializer.Serialize(participants, Json);
                bag.Version++;
                await db.SaveChangesAsync();
            }
            return ToBagDto(bag);
        }
        finally { gate.Release(); }
    }

    /// <summary>Removes a non-owner participant from a bag. Returns (updated bag, the leaver) to notify.</summary>
    public async Task<BagDto?> LeaveBagAsync(Guid bagId, string playerId)
    {
        var gate = LockFor("bag:" + bagId);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bag = await db.Bags.FindAsync(bagId);
            if (bag == null || bag.OwnerPlayerId == playerId) return null; // owner can't "leave"; they delete
            var participants = ParticipantsOf(bag);
            if (!participants.Remove(playerId)) return null;
            bag.ParticipantsJson = JsonSerializer.Serialize(participants, Json);
            bag.Version++;
            await db.SaveChangesAsync();
            return ToBagDto(bag);
        }
        finally { gate.Release(); }
    }

    // ── Trades ────────────────────────────────────────────────────────────────

    /// <summary>Read-only walk to the item at (path, id) within an item tree. Null if not found.</summary>
    private static RpItemDto? FindItem(List<RpItemDto> root, Guid[] path, Guid id)
    {
        var list = root;
        foreach (var p in path)
        {
            var it = list.FirstOrDefault(i => i.Id == p);
            if (it == null || it.Type != RpItemType.Bag) return null;
            list = it.Contents ?? new();
        }
        return list.FirstOrDefault(i => i.Id == id);
    }

    private static void Replace(List<BagDto> list, BagDto dto)
    {
        list.RemoveAll(b => b.BagId == dto.BagId);
        list.Add(dto);
    }

    /// <summary>
    /// Records a trade offer. The item is NOT escrowed — the donor keeps it until accept. We only
    /// validate the source here and snapshot a display item carrying the chosen Amount (bags/non-stackables
    /// trade as a single whole unit). Re-validation + the actual transfer happen in <see cref="AcceptTradeAsync"/>.
    /// </summary>
    public async Task<TradeOffer?> CreateTradeAsync(string code, string fromId, string fromName, string toId,
        Guid sourceBagId, Guid[] sourcePath, Guid itemId, int amount, bool isCopy)
    {
        if (_trades.Count(t => t.Value.FromPlayerId == fromId) >= Limits.PendingTradesPerPlayer) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var bag = await db.Bags.FindAsync(sourceBagId);
        if (bag == null || !ParticipantsOf(bag).Contains(fromId)) return null;
        var item = FindItem(ItemsOf(bag), sourcePath, itemId);
        if (item == null) return null;

        bool whole = item.Type != RpItemType.Normal;          // bags/non-stackables go as one whole unit
        int amt = whole ? 1 : Math.Clamp(amount, 1, item.Amount);
        var display = InputSanitizer.SanitizeItem(item with { Amount = amt });
        if (display == null) return null;

        var offer = new TradeOffer
        {
            OfferId = Guid.NewGuid(), CampaignCode = code,
            FromPlayerId = fromId, FromDisplayName = fromName, ToPlayerId = toId,
            Item = display, SourceBagId = sourceBagId, SourcePath = sourcePath, SourceItemId = itemId,
            Amount = amt, IsCopy = isCopy, ExpiresAt = DateTime.UtcNow.AddMinutes(2),
        };
        _trades[offer.OfferId] = offer;
        return offer;
    }

    /// <summary>
    /// Accepts a trade. Drops the item into the recipient's first owned inventory (creating one if they
    /// have none). For a give, re-validates the donor's source and atomically moves/splits Amount across;
    /// if the donor no longer has it (spent/moved) the accept fails cleanly with no duplication. For a
    /// copy the source is untouched. Returns the bag snapshots to broadcast.
    /// </summary>
    public async Task<List<BagDto>> AcceptTradeAsync(Guid offerId, string requesterId)
    {
        var result = new List<BagDto>();
        if (!_trades.TryRemove(offerId, out var offer) || offer.ToPlayerId != requesterId) return result;

        // Resolve (or create) the recipient's destination inventory.
        Guid destBagId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var dest = await db.Bags.FirstOrDefaultAsync(b => b.CampaignCode == offer.CampaignCode && b.OwnerPlayerId == requesterId);
            if (dest != null) destBagId = dest.BagId;
            else
            {
                var created = await CreateBagAsync(offer.CampaignCode, requesterId, "Inventory", false);
                if (created == null) return result;   // couldn't make a bag (limits) -> abort, donor keeps item
                destBagId = created.BagId;
                Replace(result, created);
            }
        }

        if (offer.IsCopy)
        {
            var copy = offer.Item with { Id = Guid.NewGuid() };
            var destBag = await MutateBagAsync(destBagId, requesterId, b =>
            {
                var items = ItemsOf(b);
                if (items.Count < Limits.BagItemsMax) { items.Add(copy); b.ItemsJson = JsonSerializer.Serialize(items, Json); }
            });
            if (destBag != null) Replace(result, destBag);
            return result;
        }

        // Give: lock source + dest (stable order) and transfer.
        bool sameBag = offer.SourceBagId == destBagId;
        var first  = offer.SourceBagId.CompareTo(destBagId) <= 0 ? offer.SourceBagId : destBagId;
        var second = offer.SourceBagId.CompareTo(destBagId) <= 0 ? destBagId : offer.SourceBagId;
        var g1 = LockFor("bag:" + first); await g1.WaitAsync();
        SemaphoreSlim? g2 = null;
        if (!sameBag) { g2 = LockFor("bag:" + second); await g2.WaitAsync(); }
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var src = await db.Bags.FindAsync(offer.SourceBagId);
            var dst = sameBag ? src : await db.Bags.FindAsync(destBagId);
            if (src == null || dst == null) return result;
            if (!ParticipantsOf(src).Contains(offer.FromPlayerId)) return result; // donor lost access
            if (!ParticipantsOf(dst).Contains(requesterId)) return result;

            RpItemDto? moved = null;
            var srcItems = MutateContainer(ItemsOf(src), offer.SourcePath, c =>
            {
                int idx = c.FindIndex(i => i.Id == offer.SourceItemId);
                if (idx < 0) return;
                var it = c[idx];
                if (it.Type == RpItemType.Normal)
                {
                    if (it.Amount < offer.Amount) return;                         // not enough anymore
                    if (it.Amount == offer.Amount) { moved = it with { Id = Guid.NewGuid() }; c.RemoveAt(idx); }
                    else { c[idx] = it with { Amount = it.Amount - offer.Amount }; moved = it with { Id = Guid.NewGuid(), Amount = offer.Amount, Contents = null }; }
                }
                else { moved = it; c.RemoveAt(idx); }                             // whole bag/item
            });
            if (srcItems == null || moved == null) return result;

            var dstItems = sameBag ? srcItems : ItemsOf(dst);
            if (dstItems.Count >= Limits.BagItemsMax) return result;              // dest full -> abort, donor keeps
            dstItems.Add(moved);

            src.ItemsJson = JsonSerializer.Serialize(srcItems, Json); src.Version++;
            if (!sameBag) { dst.ItemsJson = JsonSerializer.Serialize(dstItems, Json); dst.Version++; }
            await db.SaveChangesAsync();

            Replace(result, ToBagDto(src));
            if (!sameBag) Replace(result, ToBagDto(dst));
            return result;
        }
        finally { g2?.Release(); g1.Release(); }
    }

    public TradeOffer? TakeTrade(Guid offerId, string requesterId)
        => _trades.TryRemove(offerId, out var offer) && offer.ToPlayerId == requesterId ? offer : null;

    public void PurgeStaleTrades()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, offer) in _trades)
            if (offer.ExpiresAt < now) _trades.TryRemove(id, out _);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // BGM rooms — fully persisted: playlist, playback state, AND membership.
    // Membership lives in the DB (BgmRoomMembers), so once you join you stay a member
    // across disconnects/reconnects with no re-auth. Disconnect does NOT remove you.
    // ═════════════════════════════════════════════════════════════════════════

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// When a player leaves a campaign, sever their tie to that campaign's BGM rooms: dissolve rooms
    /// they own (room + songs + all memberships) and drop their own membership in the rest. Caller saves.
    /// </summary>
    private static async Task PurgeCampaignRoomMembershipAsync(AppDbContext db, string campaignCode, string playerId)
    {
        var rooms = await db.BgmRooms.Where(r => r.CampaignCode == campaignCode).ToListAsync();
        foreach (var room in rooms)
        {
            if (room.OwnerPlayerId == playerId)
            {
                db.BgmSongs.RemoveRange(db.BgmSongs.Where(s => s.RoomCode == room.Code));
                db.BgmRoomMembers.RemoveRange(db.BgmRoomMembers.Where(m => m.RoomCode == room.Code));
                db.BgmRooms.Remove(room);
            }
            else
            {
                var m = await db.BgmRoomMembers.FindAsync(room.Code, playerId);
                if (m != null) db.BgmRoomMembers.Remove(m);
            }
        }
    }

    /// <summary>Room codes a player is a persistent member of (used to re-add them to room groups on Identify).</summary>
    public async Task<List<string>> PlayerRoomCodesAsync(string playerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.BgmRoomMembers.Where(m => m.PlayerId == playerId).Select(m => m.RoomCode).ToListAsync();
    }

    /// <summary>Owner, or a member with Owner/Admin role, may control playback and playlist.</summary>
    private static async Task<bool> CanControlAsync(AppDbContext db, string code, string playerId, BgmRoomEntity room)
    {
        if (room.OwnerPlayerId == playerId) return true;
        var m = await db.BgmRoomMembers.FindAsync(code, playerId);
        return m is { Role: RoomRole.Owner or RoomRole.Admin };
    }

    /// <summary>The live playhead (seconds) right now: the stored anchor plus elapsed time while playing.</summary>
    private static double LivePos(BgmRoomEntity room)
        => room.IsPlaying ? room.PositionSecs + Math.Max(0, NowMs() - room.LastTimestampMs) / 1000.0 : room.PositionSecs;

    private async Task<RoomStateDto> BuildRoomDtoAsync(AppDbContext db, BgmRoomEntity room)
    {
        var playlist = await db.BgmSongs.Where(s => s.RoomCode == room.Code)
            .OrderBy(s => s.SortIndex)
            .Select(s => new RpSongDto(s.Id, s.Title, s.YoutubeUrl)).ToListAsync();

        var memberEntities = await db.BgmRoomMembers.Where(m => m.RoomCode == room.Code).ToListAsync();
        var members = memberEntities
            .Select(m => new RoomMemberDto(
                m.PlayerId,
                _presence.TryGetValue(m.PlayerId, out var p) ? p.DisplayName : m.PlayerId,
                m.Role))
            .ToList();

        bool   isPlaying = room.IsPlaying;
        double pos       = LivePos(room);
        bool   preparing = _gates.TryGetValue(room.Code, out var gate);
        int    ready = 0, total = 0;
        if (preparing)
        {
            // While gating, the room is logically not playing yet and sits at the cue start.
            isPlaying = false;
            pos       = 0;
            var active = memberEntities.Select(m => m.PlayerId).Where(id => IsActiveInRoom(id, room.Code)).ToList();
            total = active.Count;
            lock (gate!) ready = active.Count(id => gate.Ready.Contains(id));
        }

        return new RoomStateDto(
            room.Code, room.CampaignCode, room.Name, room.OwnerPlayerId, members, playlist,
            room.CurrentIndex, isPlaying, pos, NowMs(), room.Loop, room.Version,
            preparing, ready, total);
    }

    /// <summary>Member ids who are online AND have declared this room their active listening room.</summary>
    private async Task<List<string>> ActiveMemberIdsAsync(AppDbContext db, string code)
    {
        var memberIds = await db.BgmRoomMembers.Where(m => m.RoomCode == code).Select(m => m.PlayerId).ToListAsync();
        return memberIds.Where(id => IsActiveInRoom(id, code)).ToList();
    }

    public async Task<RoomStateDto?> CreateRoomAsync(string playerId, string name, string? password, string? campaignCode)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.BgmRooms.CountAsync() >= Limits.TotalRooms) return null;

        // A campaign-scoped room requires the creator to be a member of that campaign.
        string campaign = "";
        if (!string.IsNullOrEmpty(campaignCode))
        {
            if (!await db.CampaignMembers.AnyAsync(m => m.Code == campaignCode && m.PlayerId == playerId)) return null;
            campaign = campaignCode;
        }

        string code = NewCode();
        var room = new BgmRoomEntity
        {
            Code           = code,
            CampaignCode   = campaign,
            Name           = string.IsNullOrWhiteSpace(name) ? "Room " + code : InputSanitizer.SanitizeName(name),
            HashedPassword = string.IsNullOrEmpty(password) ? "" : InputSanitizer.HashPassword(password),
            OwnerPlayerId  = playerId,
            CurrentIndex   = -1,
            Version        = 1,
        };
        db.BgmRooms.Add(room);
        db.BgmRoomMembers.Add(new BgmRoomMemberEntity { RoomCode = code, PlayerId = playerId, Role = RoomRole.Owner });
        await db.SaveChangesAsync();
        return await BuildRoomDtoAsync(db, room);
    }

    public async Task<RoomStateDto?> JoinRoomAsync(string code, string playerId, string? password)
    {
        if (!InputSanitizer.IsValidCode(code)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var room = await db.BgmRooms.FindAsync(code);
        if (room == null) return null;

        // Already a member? Then no password needed — you're in, you stay in.
        if (await db.BgmRoomMembers.FindAsync(code, playerId) == null)
        {
            if (!string.IsNullOrEmpty(room.HashedPassword))
            {
                // Campaign members may join their campaign's room without the password.
                bool campaignMember = !string.IsNullOrEmpty(room.CampaignCode)
                    && await db.CampaignMembers.AnyAsync(m => m.Code == room.CampaignCode && m.PlayerId == playerId);
                if (!campaignMember && !InputSanitizer.VerifyPassword(password ?? "", room.HashedPassword))
                    return null;
            }
            if (await db.BgmRoomMembers.CountAsync(m => m.RoomCode == code) >= Limits.MembersPerRoom) return null;
            db.BgmRoomMembers.Add(new BgmRoomMemberEntity
            {
                RoomCode = code, PlayerId = playerId,
                Role = room.OwnerPlayerId == playerId ? RoomRole.Owner : RoomRole.Member,
            });
            await db.SaveChangesAsync();
        }
        return await BuildRoomDtoAsync(db, room);
    }

    /// <summary>Leaves a room. Returns the updated room, or Removed=true if the room is now empty and was deleted.</summary>
    public async Task<(RoomStateDto? Room, bool Removed)> LeaveRoomAsync(string code, string playerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var room   = await db.BgmRooms.FindAsync(code);
        var member = await db.BgmRoomMembers.FindAsync(code, playerId);

        // The owner leaving dissolves the room entirely (songs + every membership), so it never gets
        // orphaned with a dangling OwnerPlayerId. Any other member just removes their own membership.
        if (room != null && room.OwnerPlayerId == playerId)
        {
            db.BgmRoomMembers.RemoveRange(db.BgmRoomMembers.Where(m => m.RoomCode == code));
            db.BgmSongs.RemoveRange(db.BgmSongs.Where(s => s.RoomCode == code));
            db.BgmRooms.Remove(room);
            await db.SaveChangesAsync();
            _gates.TryRemove(code, out _);
            return (null, true);
        }

        if (member == null) return (null, false);
        db.BgmRoomMembers.Remove(member);
        await db.SaveChangesAsync();

        if (room == null) return (null, true);

        if (!await db.BgmRoomMembers.AnyAsync(m => m.RoomCode == code))
        {
            db.BgmSongs.RemoveRange(db.BgmSongs.Where(s => s.RoomCode == code));
            db.BgmRooms.Remove(room);
            await db.SaveChangesAsync();
            return (null, true);
        }
        return (await BuildRoomDtoAsync(db, room), false);
    }

    public async Task<RoomStateDto?> PromoteRoomAsync(string code, string requesterId, string targetId, RoomRole role)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var room = await db.BgmRooms.FindAsync(code);
        if (room == null || !await CanControlAsync(db, code, requesterId, room)) return null;
        if (role == RoomRole.Owner || targetId == room.OwnerPlayerId) return null;
        var target = await db.BgmRoomMembers.FindAsync(code, targetId);
        if (target == null) return null;
        target.Role = role;
        room.Version++;
        await db.SaveChangesAsync();
        return await BuildRoomDtoAsync(db, room);
    }

    public async Task<RoomStateDto?> PlaylistAddAsync(string code, string requesterId, string title, string url)
    {
        if (!InputSanitizer.IsAllowedYoutubeUrl(url)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var room = await db.BgmRooms.FindAsync(code);
        if (room == null || !await CanControlAsync(db, code, requesterId, room)) return null;
        int count = await db.BgmSongs.CountAsync(s => s.RoomCode == code);
        if (count >= Limits.PlaylistMax) return null;
        db.BgmSongs.Add(new BgmSongEntity
        {
            Id = Guid.NewGuid(), RoomCode = code, SortIndex = count,
            Title = InputSanitizer.SanitizeName(title, 200),
            YoutubeUrl = url[..Math.Min(url.Length, Limits.UrlMax)],
        });
        room.Version++;
        await db.SaveChangesAsync();
        return await BuildRoomDtoAsync(db, room);
    }

    public async Task<RoomStateDto?> PlaylistRemoveAsync(string code, string requesterId, Guid songId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var room = await db.BgmRooms.FindAsync(code);
        if (room == null || !await CanControlAsync(db, code, requesterId, room)) return null;
        var song = await db.BgmSongs.FirstOrDefaultAsync(s => s.RoomCode == code && s.Id == songId);
        if (song == null) return null;
        db.BgmSongs.Remove(song);
        room.Version++;
        await db.SaveChangesAsync();
        return await BuildRoomDtoAsync(db, room);
    }

    public async Task<RoomStateDto?> PlaybackCommandAsync(string code, string requesterId, PlaybackCommandDto cmd)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var room = await db.BgmRooms.FindAsync(code);
        if (room == null || !await CanControlAsync(db, code, requesterId, room)) return null;

        switch (cmd.CommandType)
        {
            case PlaybackCommandType.Play:
                // Don't start now — open a "waiting for members" gate. Playback begins (synchronized)
                // once every active listener reports ready, or the gate times out (see BgmGateService).
                room.CurrentIndex = cmd.SongIndex;
                room.PositionSecs = 0;
                room.IsPlaying    = false;
                break;
            case PlaybackCommandType.Pause:
                room.PositionSecs = LivePos(room);   // capture the live playhead server-side
                room.IsPlaying    = false;
                _gates.TryRemove(code, out _);
                break;
            case PlaybackCommandType.Resume:
                room.IsPlaying    = true;            // keep PositionSecs (where we paused); LastTimestampMs reset below
                break;
            case PlaybackCommandType.Seek:
                room.PositionSecs = cmd.PositionSeconds;
                break;
            case PlaybackCommandType.Stop:
                room.IsPlaying    = false;
                room.PositionSecs = 0;
                _gates.TryRemove(code, out _);
                break;
            case PlaybackCommandType.LoopChanged:
                if (cmd.Loop.HasValue) room.Loop = cmd.Loop.Value;
                break;
        }
        room.LastTimestampMs = NowMs();
        room.Version++;
        if (cmd.CommandType == PlaybackCommandType.Play)
            _gates[code] = new PrepareGate { Version = room.Version, SongIndex = cmd.SongIndex, DeadlineMs = NowMs() + GatePrepareMs };
        await db.SaveChangesAsync();
        return await BuildRoomDtoAsync(db, room);
    }

    // ── "Waiting for members" prepare gate ────────────────────────────────────

    // How long to wait for every active listener to download+ready the cued song before starting anyway.
    private const long GatePrepareMs = 15_000;

    private sealed class PrepareGate
    {
        public required long            Version    { get; init; }
        public required int             SongIndex  { get; init; }
        public required long            DeadlineMs { get; init; }
        public          HashSet<string> Ready      { get; } = new();
    }

    /// <summary>
    /// A client reports it has the cued song ready. Returns the room DTO to broadcast: either updated
    /// progress (still preparing) or the committed synchronized-start state, or null to skip a broadcast
    /// (stale cue / already committed).
    /// </summary>
    public async Task<RoomStateDto?> MarkReadyAsync(string code, string playerId, int songIndex, long version)
    {
        if (!_gates.TryGetValue(code, out var gate) || gate.Version != version) return null;

        bool complete;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var active = await ActiveMemberIdsAsync(db, code);
            lock (gate) { gate.Ready.Add(playerId); complete = active.Count > 0 && active.All(id => gate.Ready.Contains(id)); }
        }

        if (complete) return await CommitGateAsync(code, gate);

        await using var db2 = await _dbFactory.CreateDbContextAsync();
        var room = await db2.BgmRooms.FindAsync(code);
        return room == null ? null : await BuildRoomDtoAsync(db2, room);
    }

    /// <summary>Background sweep: commit any gate whose deadline passed or whose active listeners are all ready.</summary>
    public async Task<List<RoomStateDto>> TickGatesAsync()
    {
        var committed = new List<RoomStateDto>();
        foreach (var (code, gate) in _gates.ToArray())
        {
            bool due = NowMs() >= gate.DeadlineMs;
            if (!due)
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var active = await ActiveMemberIdsAsync(db, code);
                lock (gate) due = active.Count == 0 || active.All(id => gate.Ready.Contains(id));
            }
            if (due)
            {
                var dto = await CommitGateAsync(code, gate);
                if (dto != null) committed.Add(dto);
            }
        }
        return committed;
    }

    /// <summary>Atomically removes the gate (so exactly one caller commits) and starts synchronized playback.</summary>
    private async Task<RoomStateDto?> CommitGateAsync(string code, PrepareGate gate)
    {
        if (!_gates.TryRemove(code, out var g) || !ReferenceEquals(g, gate)) return null; // someone else committed

        await using var db = await _dbFactory.CreateDbContextAsync();
        var room = await db.BgmRooms.FindAsync(code);
        if (room == null) return null;

        room.CurrentIndex    = gate.SongIndex;
        room.PositionSecs    = 0;
        room.IsPlaying       = true;
        room.LastTimestampMs = NowMs();   // playback clock anchor — pos 0 corresponds to this instant
        room.Version++;
        await db.SaveChangesAsync();
        return await BuildRoomDtoAsync(db, room);
    }
}
