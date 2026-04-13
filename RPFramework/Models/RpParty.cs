using System;

namespace RPFramework.Models;

/// <summary>
/// A party the local player is a member of.
/// Code, Name and OwnerPlayerId are persisted; the live member list
/// is in-memory and refreshed from the server on connect / join.
/// </summary>
[Serializable]
public class RpParty
{
    public string Code          { get; set; } = string.Empty;
    public string Name          { get; set; } = string.Empty;
    public string OwnerPlayerId { get; set; } = string.Empty;
}
