using System;

namespace RPFramework.Models;

[Serializable]
public class RpItem
{
    public Guid   Id          { get; set; } = Guid.NewGuid();
    public string Name        { get; set; } = "New Item";
    public string Description { get; set; } = string.Empty;
    public uint   IconId      { get; set; } = 0;
    public int    Amount      { get; set; } = 1;
}
