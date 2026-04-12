using System;
using System.Collections.Generic;

namespace SamplePlugin.Models;

[Serializable]
public class RpRoom
{
    public Guid         Id           { get; set; } = Guid.NewGuid();
    public string       Name         { get; set; } = "New Room";
    public string       Code         { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public List<RpSong> Playlist     { get; set; } = new();
    public int          CurrentIndex { get; set; } = -1;
    public float        Volume       { get; set; } = 1.0f;
    public LoopMode     Loop         { get; set; } = LoopMode.None;
}
