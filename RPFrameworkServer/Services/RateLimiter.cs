using System.Collections.Concurrent;

namespace RPFrameworkServer.Services;

/// <summary>
/// Per-connection sliding-window rate limiter. Buckets are keyed by
/// (connectionId, category); state for a connection is dropped on disconnect.
/// Singleton, thread-safe.
/// </summary>
public class RateLimiter
{
    private sealed class Window
    {
        public long WindowStartTicks;
        public int  Count;
    }

    /// <summary>maxCalls per window for each category.</summary>
    private static readonly Dictionary<string, (int MaxCalls, TimeSpan Window)> Policies = new()
    {
        // Highest-risk fan-out vectors get tight limits
        ["profile"]   = (5,  TimeSpan.FromSeconds(10)),   // PushProfile (client throttles to 1/3s)
        ["dice"]      = (10, TimeSpan.FromSeconds(10)),   // BroadcastDiceRoll
        ["playback"]  = (20, TimeSpan.FromSeconds(10)),   // BgmSync* commands
        ["trade"]     = (10, TimeSpan.FromSeconds(30)),   // trade offers
        ["partyAuth"] = (10, TimeSpan.FromMinutes(1)),    // PartyCreate / PartyJoin (password guessing)
        ["template"]  = (5,  TimeSpan.FromMinutes(1)),    // PushSheetTemplate (large payloads)
        ["fetch"]     = (30, TimeSpan.FromSeconds(10)),   // FetchProfile
        ["default"]   = (60, TimeSpan.FromSeconds(10)),   // everything else
    };

    private readonly ConcurrentDictionary<(string Conn, string Category), Window> _windows = new();

    /// <summary>Returns true if the call is allowed, false if the connection exceeded its budget.</summary>
    public bool Allow(string connectionId, string category)
    {
        if (!Policies.TryGetValue(category, out var policy))
            policy = Policies["default"];

        var window = _windows.GetOrAdd((connectionId, category), _ => new Window());
        long now = DateTime.UtcNow.Ticks;

        lock (window)
        {
            if (now - window.WindowStartTicks > policy.Window.Ticks)
            {
                window.WindowStartTicks = now;
                window.Count = 0;
            }
            if (window.Count >= policy.MaxCalls) return false;
            window.Count++;
            return true;
        }
    }

    public void DropConnection(string connectionId)
    {
        foreach (var key in _windows.Keys)
            if (key.Conn == connectionId)
                _windows.TryRemove(key, out _);
    }
}
