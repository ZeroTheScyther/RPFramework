namespace RPFramework.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// Wire DTOs — the ONLY definition, shared by client and server. Server-first:
// the server broadcasts authoritative aggregate snapshots; clients send intents
// (primitive params, plus the few payload types below) and render snapshots.
//
// Each aggregate snapshot carries a monotonically increasing Version so clients
// can apply updates idempotently and drop stale/out-of-order messages.
// ─────────────────────────────────────────────────────────────────────────────

// ── Parties ──────────────────────────────────────────────────────────────────

public record PartyMemberDto(
    string       PlayerId,
    string       DisplayName,
    PartyRole    Role,
    bool         Online,
    List<string> BgmRoomCodes
);

public record PartyDto(
    string               Code,
    string               Name,
    string               OwnerPlayerId,
    List<PartyMemberDto> Members,
    bool                 ShowHpAp,
    bool                 IsPersonal,   // the player's locked-ruleset "Individual Adventurer" scope
    long                 Version
);

// ── Character & template ─────────────────────────────────────────────────────

public record CharacterDto(
    string         PartyCode,
    string         PlayerId,
    string         DisplayName,
    CharacterState State,
    long           Version
);

public record TemplateDto(
    string        PartyCode,
    SheetTemplate Template,
    long          Version
);

// ── Dice ─────────────────────────────────────────────────────────────────────

/// <summary>Server-computed dice result broadcast to a party.</summary>
public record DiceRollResultDto(
    string    PartyCode,
    string    PlayerId,
    string    DisplayName,
    int       Die,
    List<int> Rolls,        // one entry normally, two for advantage/disadvantage
    int       Kept,         // the die face actually used
    int       Modifier,     // stat modifier + spec + AP penalty, combined
    int       Total,        // Kept + Modifier
    RollMode  Mode,
    string    Message       // pre-formatted human-readable summary
);

// ── Initiative ───────────────────────────────────────────────────────────────

public record InitiativeEntryDto(
    string PlayerId,
    string DisplayName,
    int    Roll,
    int    Bonus,
    int    Total,
    int    HpCurrent,
    int    HpMax,
    int    ApCurrent,
    int    ApMax,
    bool   IsNpc
);

public record InitiativeStateDto(
    string                   PartyCode,
    List<InitiativeEntryDto> Order,
    int                      CurrentIndex,
    bool                     IsActive,
    bool                     ShowHpAp,
    long                     Version
);

// ── Inventory (personal + shared bags are unified) ───────────────────────────

public record RpItemDto(
    Guid                  Id,
    string                Name,
    string                Description,
    uint                  IconId,
    int                   Amount,
    RpItemType            Type       = RpItemType.Normal,
    int                   Capacity   = 10,
    List<RpItemDto>?      Contents   = null,
    List<SkillEffect>?    Effects    = null,  // stat effects for equippable/consumable items (null = none)
    List<SkillCondition>? Conditions = null   // equipped gear: effects apply only while ALL are met (null/empty = always-on)
);

public record BagDto(
    Guid            BagId,
    string          CampaignCode,
    string          OwnerPlayerId,
    string          Name,
    List<RpItemDto> Items,
    List<string>    ParticipantIds,   // players who share this bag (always includes owner)
    long            Version
);

public record TradeOfferDto(
    Guid      OfferId,
    string    FromPlayerId,
    string    FromDisplayName,
    string    ToPlayerId,
    RpItemDto Item,
    bool      IsCopy
);

public record BagShareInviteDto(
    Guid   BagId,
    string OwnerPlayerId,
    string OwnerDisplayName,
    string BagName
);

public record BagShareDeclinedDto(
    Guid   BagId,
    string BagName,
    string DeclinerPlayerId,
    string DeclinerDisplayName
);

// ── BGM ──────────────────────────────────────────────────────────────────────

public record RpSongDto(Guid Id, string Title, string YoutubeUrl);

public record RoomMemberDto(string PlayerId, string DisplayName, RoomRole Role);

public record RoomStateDto(
    string              Code,
    string              CampaignCode,      // "" = individual/global room (discovered by code), else a campaign's room
    string              Name,
    string              OwnerPlayerId,
    List<RoomMemberDto> Members,
    List<RpSongDto>     Playlist,
    int                 CurrentIndex,
    bool                IsPlaying,
    double              PositionSeconds,
    long                ServerTimestamp,   // UTC ms — receivers adjust for transit latency
    LoopMode            Loop,
    long                Version
);

public record PlaybackCommandDto(
    PlaybackCommandType CommandType,
    int                 SongIndex,
    double              PositionSeconds,
    long                ServerTimestamp,
    LoopMode?           Loop
);

// ── Hydration ────────────────────────────────────────────────────────────────

/// <summary>
/// Full picture of everything the connecting player can currently see. Sent once
/// right after Identify so clients never have a "republish to see it" gap.
/// </summary>
public record SnapshotDto(
    List<PartyDto>           Parties,
    List<CharacterDto>       Characters,    // every member's character across the player's parties
    List<TemplateDto>        Templates,     // one per party
    List<BagDto>             Bags,          // owned + shared bags the player participates in
    List<RoomStateDto>       Rooms,         // rooms the player is a member of
    List<InitiativeStateDto> Initiatives    // active combats in the player's parties
);
