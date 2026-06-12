using System;
using System.Collections.Generic;

namespace RPFramework.Models;

public enum RpItemType { Normal, Bag }

[Serializable]
public class RpItem
{
    public Guid         Id          { get; set; } = Guid.NewGuid();
    public string       Name        { get; set; } = "New Item";
    public string       Description { get; set; } = string.Empty;
    public uint         IconId      { get; set; } = 0;
    public int          Amount      { get; set; } = 1;
    public RpItemType   Type        { get; set; } = RpItemType.Normal;
    public int          Capacity    { get; set; } = 10;
    public List<RpItem> Contents    { get; set; } = new();
}
