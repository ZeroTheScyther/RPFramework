using System.ComponentModel.DataAnnotations;
using RPFramework.Contracts;

namespace RPFrameworkServer.Data;

// ─────────────────────────────────────────────────────────────────────────────
// EF Core entities — the authoritative, persisted state. Server-first: this is the
// single source of truth. Complex nested objects (CharacterState, SheetTemplate,
// item trees, participant lists) are stored as JSON string columns; the
// SessionManager owns (de)serialization to/from the Contracts types.
//
// Scope rule: the CAMPAIGN is the boundary for almost everything. Characters,
// bags, and (future) NPCs/pets all carry a CampaignCode. Nothing crosses campaigns.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A known player identity. Secret binds a connection to this id (anti-spoof).</summary>
public class PlayerEntity
{
    [Key] [MaxLength(96)] public string PlayerId    { get; set; } = "";
    [MaxLength(128)]      public string Secret      { get; set; } = "";
    [MaxLength(64)]       public string DisplayName { get; set; } = "";
}

/// <summary>
/// A campaign (party) — the scope a player embodies. A player's "Individual Adventurer"
/// is just a personal campaign (IsPersonal = true, OwnerPlayerId = the player) whose
/// template is locked to SheetTemplate.Default().
/// </summary>
public class CampaignEntity
{
    [Key] [MaxLength(20)] public string Code           { get; set; } = "";
    [MaxLength(100)]      public string Name           { get; set; } = "";
    public                       string OwnerPlayerId  { get; set; } = "";
    public                       string HashedPassword { get; set; } = "";
    public                       bool   ShowHpAp       { get; set; } = true;
    public                       bool   IsPersonal     { get; set; } = false;

    /// <summary>JSON of the party's SheetTemplate (the DM's ruleset). Locked to Default() when IsPersonal.</summary>
    public string TemplateJson    { get; set; } = "";
    public long   TemplateVersion { get; set; } = 0;

    /// <summary>Bumped on any party-level change (members, roles, name, ShowHpAp).</summary>
    public long   Version         { get; set; } = 0;
}

public class CampaignMemberEntity
{
    [MaxLength(20)] public string    Code     { get; set; } = "";
    public                string    PlayerId { get; set; } = "";
    public                PartyRole Role     { get; set; } = PartyRole.Member;
}

/// <summary>
/// A template-conformant in-world entity: a player character, an NPC, or a pet.
/// For PlayerCharacter, EntityId == OwnerPlayerId (one PC per player per campaign).
/// For Npc/Companion, EntityId is a GUID string.
/// </summary>
public class CharacterEntity
{
    [MaxLength(20)] public string           CampaignCode { get; set; } = "";
    [MaxLength(96)] public string           EntityId     { get; set; } = "";
    public                 string           OwnerPlayerId{ get; set; } = "";   // empty = campaign/DM-owned
    public                 EntityKind       Kind         { get; set; } = EntityKind.PlayerCharacter;
    public                 EntityVisibility Visibility   { get; set; } = EntityVisibility.PartyVisible;
    [MaxLength(64)] public string           DisplayName  { get; set; } = "";

    /// <summary>JSON of the Contracts CharacterState (StatValues, CheckValues, Skills).</summary>
    public string StateJson { get; set; } = "";
    public long   Version   { get; set; } = 0;
}

/// <summary>
/// A bag — campaign-scoped. OwnerPlayerId empty + IsDmBag = the role-gated DM loot bag
/// (participants are the DM + co-DMs). Otherwise owned by a player who may share it.
/// </summary>
public class BagEntity
{
    [Key]           public Guid   BagId           { get; set; }
    [MaxLength(20)] public string CampaignCode    { get; set; } = "";
    public                string OwnerPlayerId    { get; set; } = "";
    public                bool   IsDmBag          { get; set; } = false;
    [MaxLength(64)] public string Name            { get; set; } = "";

    /// <summary>JSON List&lt;RpItemDto&gt;.</summary>
    public string ItemsJson        { get; set; } = "[]";
    /// <summary>JSON List&lt;string&gt; of participant player ids (always includes the owner).</summary>
    public string ParticipantsJson { get; set; } = "[]";
    public long   Version          { get; set; } = 0;
}

// ── BGM (independent audio-sync rooms, not campaign-scoped) ────────────────────

public class BgmRoomEntity
{
    [Key] [MaxLength(20)] public string   Code            { get; set; } = "";
    [MaxLength(20)]       public string   CampaignCode    { get; set; } = "";   // "" = individual/global room
    [MaxLength(64)]       public string   Name            { get; set; } = "";
    public                       string   HashedPassword  { get; set; } = "";
    public                       string   OwnerPlayerId   { get; set; } = "";
    public                       int      CurrentIndex    { get; set; } = -1;
    public                       bool     IsPlaying       { get; set; }
    public                       double   PositionSecs    { get; set; }
    public                       long     LastTimestampMs { get; set; }
    public                       LoopMode Loop            { get; set; } = LoopMode.None;
    public                       long     Version         { get; set; } = 0;
}

/// <summary>Persistent room membership — once you join, you stay a member across reconnects.</summary>
public class BgmRoomMemberEntity
{
    [MaxLength(20)] public string   RoomCode { get; set; } = "";
    public                string   PlayerId { get; set; } = "";
    public                RoomRole Role     { get; set; } = RoomRole.Member;
}

public class BgmSongEntity
{
    [Key]            public Guid   Id         { get; set; }
    [MaxLength(20)]  public string RoomCode   { get; set; } = "";
    public                 int    SortIndex   { get; set; }
    [MaxLength(200)] public string Title      { get; set; } = "";
    [MaxLength(512)] public string YoutubeUrl { get; set; } = "";
}
