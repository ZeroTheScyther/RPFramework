using System;

namespace RPFramework.Models;

[Serializable]
public class RpSong
{
    public Guid   Id         { get; set; } = Guid.NewGuid();
    public string Title      { get; set; } = "Unknown";
    public string YoutubeUrl { get; set; } = string.Empty;
}
