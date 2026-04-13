using System;
using System.Collections.Generic;
using RPFramework.Models;

namespace RPFramework.Models.Net;

// ─────────────────────────────────────────────────────────────────────────────
// Wire DTOs — mirrors RPFrameworkServer/Models/Dto.cs
// Keep these in sync if you change the server protocol.
// ─────────────────────────────────────────────────────────────────────────────

public enum RoomRole { Member, Admin, Owner }
public enum NetLoopMode { None, Single, All }
public enum BagOpType  { AddItem, RemoveItem, UpdateItem, Rename, SetGil }

// ── BGM ──────────────────────────────────────────────────────────────────────

public record RpSongDto(Guid Id, string Title, string YoutubeUrl);

public record RoomMemberDto(string PlayerId, string DisplayName, RoomRole Role);

public record RoomStateDto(
    string              Code,
    string              OwnerPlayerId,
    List<RoomMemberDto> Members,
    List<RpSongDto>     Playlist,
    int                 CurrentIndex,
    bool                IsPlaying,
    double              PositionSeconds,
    long                ServerTimestamp
);

public enum PlaybackCommandType { Play, Pause, Resume, Seek, Stop, LoopChanged }

public record PlaybackCommandDto(
    PlaybackCommandType CommandType,
    int       SongIndex,
    double    PositionSeconds,
    long      ServerTimestamp,
    NetLoopMode? Loop
);

// ── Inventory ─────────────────────────────────────────────────────────────────

public record RpItemDto(
    Guid   Id,
    string Name,
    string Description,
    uint   IconId,
    int    Amount
);

public record TradeOfferDto(
    Guid      OfferId,
    string    FromPlayerId,
    string    FromDisplayName,
    RpItemDto Item,
    bool      IsCopy
);

// ── Shared Bags ───────────────────────────────────────────────────────────────

public record SharedBagDto(
    Guid            BagId,
    string          Name,
    string          OwnerPlayerId,
    List<RpItemDto> Items,
    long            Version,
    int             Gil = 0
);

public record BagOperationDto(
    Guid      BagId,
    long      BaseVersion,
    BagOpType OpType,
    RpItemDto? Item,
    Guid?     ItemId,
    string?   NewName,
    int?      Gil = null
);

// ── Character Profiles ────────────────────────────────────────────────────────

/// <summary>Skill definition as sent over the wire. Omits session-local state (cooldownRemaining, etc.).</summary>
public record RpSkillDto(
    Guid                 Id,
    string               Name,
    string               Description,
    SkillType            Type,
    int                  Cooldown,
    int                  Duration,
    List<SkillCondition> Conditions,
    List<SkillEffect>    Effects
);

/// <summary>Full character profile snapshot pushed to and retrieved from the relay server.</summary>
public record CharacterProfileDto(
    string                   PlayerId,
    string                   DisplayName,
    int                      HpCurrent, int HpMax,
    int                      ApCurrent, int ApMax,
    int                      Str, int Dex, int Spd, int Con,
    int                      Mem, int Mtl, int Int, int Cha,
    Dictionary<string, bool> Proficiencies,
    List<RpSkillDto>         Skills
);

// ── Initiative ────────────────────────────────────────────────────────────────

public record InitiativeEntryDto(string PlayerId, string DisplayName, int Roll, int SpdBonus, int Total);

public record InitiativeStateDto(
    string                   PartyCode,
    List<InitiativeEntryDto> Order,
    int                      CurrentIndex,
    bool                     IsActive
);

// ── Parties ───────────────────────────────────────────────────────────────────

public enum PartyRole { Member, CoDm, Owner }

public record PartyMemberDto(
    string       PlayerId,
    string       DisplayName,
    PartyRole    Role,
    List<string> BgmRoomCodes   // BGM room codes this member is currently in
);

public record PartyInfoDto(
    string               Code,
    string               Name,
    string               OwnerPlayerId,
    List<PartyMemberDto> Members
);
