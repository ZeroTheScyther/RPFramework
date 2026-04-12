using System;
using System.Collections.Generic;

namespace RPFramework.Models.Net;

// ─────────────────────────────────────────────────────────────────────────────
// Wire DTOs — mirrors RPFrameworkServer/Models/Dto.cs
// Keep these in sync if you change the server protocol.
// ─────────────────────────────────────────────────────────────────────────────

public enum RoomRole { Member, Admin, Owner }
public enum NetLoopMode { None, Single, All }
public enum BagOpType  { AddItem, RemoveItem, UpdateItem, Rename }

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
    long            Version
);

public record BagOperationDto(
    Guid      BagId,
    long      BaseVersion,
    BagOpType OpType,
    RpItemDto? Item,
    Guid?     ItemId,
    string?   NewName
);
