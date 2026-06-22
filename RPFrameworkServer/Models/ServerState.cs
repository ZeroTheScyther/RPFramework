using RPFramework.Contracts;

namespace RPFrameworkServer.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Live, in-memory session state — NEVER persisted and never the durable source of
// truth. Holds only ephemeral things: who is connected (presence), the active
// combat round per campaign (initiative), and pending trade offers. Everything
// else is owned by the database (see Data/DbModels.cs).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One combatant in a live encounter. May be a player character, a companion, a vault NPC, or an ad-hoc NPC.</summary>
public sealed class EncounterEntry
{
    public required string EntityId    { get; init; }   // PC playerId, companion/NPC entity GUID, or synthetic "npc:xxxx"
    public          string DisplayName { get; set; } = string.Empty;
    public          int    Roll        { get; set; }
    public          int    Bonus       { get; set; }
    public          int    Total       => Roll + Bonus;
    public          int    HpCurrent   { get; set; }
    public          int    HpMax       { get; set; }
    public          int    ApCurrent   { get; set; }
    public          int    ApMax       { get; set; }
    public          bool   IsNpc       { get; init; }
}

/// <summary>
/// A live, joinable combat encounter. A campaign may have several at once (two
/// separate fights => two independent trackers). Ephemeral — lost on server restart
/// by design. Keyed globally by <see cref="EncounterId"/>; scoped to a campaign via
/// <see cref="CampaignCode"/>.
/// </summary>
public sealed class Encounter
{
    public required string EncounterId  { get; init; }   // GUID, globally unique
    public required string CampaignCode { get; init; }
    public          string Name         { get; set; } = "Encounter";
    public List<EncounterEntry> Entries { get; } = new();
    public int  CurrentIndex { get; set; } = 0;
    public bool ShowHpAp     { get; set; } = true;
    public long Version      { get; set; } = 0;
}

/// <summary>
/// A pending item trade offer. Expires; cleaned up by the background service. The item is NOT
/// escrowed — the donor keeps it until the recipient accepts. On accept the server re-validates
/// the source (SourceBagId/SourcePath/SourceItemId) and transfers Amount, so nothing is duplicated
/// or lost even if the donor spends the stack in between (the accept just fails). For a copy, the
/// source is untouched and Amount of a fresh copy is granted.
/// </summary>
public sealed class TradeOffer
{
    public required Guid      OfferId         { get; init; }
    public required string    CampaignCode    { get; init; }
    public required string    FromPlayerId    { get; init; }
    public required string    FromDisplayName { get; init; }
    public required string    ToPlayerId      { get; init; }
    public required RpItemDto Item            { get; init; }   // display snapshot (with traded Amount)
    public required Guid      SourceBagId     { get; init; }
    public required Guid[]    SourcePath      { get; init; }
    public required Guid      SourceItemId    { get; init; }
    public required int       Amount          { get; init; }
    public          bool      IsCopy          { get; init; }
    public          DateTime  ExpiresAt       { get; init; }
}

/// <summary>Tracks which connections belong to which player (a player may have multiple clients).</summary>
public sealed class PlayerPresence
{
    public required string          PlayerId    { get; init; }
    public          string          DisplayName { get; set; } = string.Empty;
    public          HashSet<string> Connections { get; } = new();
    public          bool            IsOnline    => Connections.Count > 0;
}
