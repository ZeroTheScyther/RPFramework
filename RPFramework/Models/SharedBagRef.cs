using System;

namespace RPFramework.Models;

/// <summary>
/// Persisted reference to a shared bag session.
/// The actual bag data lives in Configuration.Bags; this records ownership metadata.
/// </summary>
[Serializable]
public class SharedBagRef
{
    public Guid   BagId         { get; set; }
    public string OwnerPlayerId { get; set; } = string.Empty;
    public bool   IsOwner       { get; set; }
}
