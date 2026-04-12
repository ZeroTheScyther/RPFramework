using System;
using System.Collections.Generic;

namespace RPFramework.Models;

[Serializable]
public class RpBag
{
    public Guid         Id           { get; set; } = Guid.NewGuid();
    public string       Name         { get; set; } = "Bag";
    public List<RpItem> Items        { get; set; } = new();

    // Shared bag metadata (null = not shared)
    public string?      SharedOwner  { get; set; } = null;  // playerId of the owner
    public bool         IsShared     => SharedOwner != null;
}
