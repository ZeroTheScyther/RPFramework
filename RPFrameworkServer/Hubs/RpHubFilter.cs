using Microsoft.AspNetCore.SignalR;
using RPFramework.Contracts;
using RPFrameworkServer.Services;

namespace RPFrameworkServer.Hubs;

/// <summary>
/// Global hub filter providing two cross-cutting protections for every hub method:
///  1. Rate limiting — per-connection sliding windows, with tighter budgets for the
///     highest-risk fan-out methods (profile push, dice broadcast, BGM playback).
///  2. Exception containment — unhandled hub exceptions are logged with method context
///     instead of silently dropping the invocation. HubException is re-thrown so
///     intentional client-facing errors still propagate.
/// </summary>
public class RpHubFilter : IHubFilter
{
    private readonly RateLimiter          _limiter;
    private readonly ILogger<RpHubFilter> _log;

    /// <summary>Maps hub intents to rate-limit categories; unlisted methods use "default".</summary>
    private static readonly Dictionary<string, string> MethodCategories = new(StringComparer.Ordinal)
    {
        [HubRoutes.Intents.CharacterEditStat]  = "profile",
        [HubRoutes.Intents.CharacterEditCheck] = "profile",
        [HubRoutes.Intents.CharacterSetSkills] = "profile",
        [HubRoutes.Intents.RollDice]           = "dice",
        [HubRoutes.Intents.TemplatePublish]    = "template",
        [HubRoutes.Intents.TradeOffer]         = "trade",
        [HubRoutes.Intents.PartyCreate]        = "partyAuth",
        [HubRoutes.Intents.PartyJoin]          = "partyAuth",
        [HubRoutes.Intents.PlaybackCommand]    = "playback",
    };

    public RpHubFilter(RateLimiter limiter, ILogger<RpHubFilter> log)
    {
        _limiter = limiter;
        _log     = log;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        string method   = context.HubMethodName;
        string category = MethodCategories.GetValueOrDefault(method, "default");

        if (!_limiter.Allow(context.Context.ConnectionId, category))
        {
            _log.LogWarning("[RateLimit] {Method} throttled for connection {Conn}",
                method, context.Context.ConnectionId);
            await context.Hub.Clients.Caller
                .SendAsync(HubRoutes.Events.Error, "RateLimit", $"Too many {method} calls — slow down.");
            return null;
        }

        try
        {
            return await next(context);
        }
        catch (HubException)
        {
            throw; // intentional client-facing error (e.g. "Call Identify() first")
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] Unhandled exception in {Method} (connection {Conn})",
                method, context.Context.ConnectionId);
            await context.Hub.Clients.Caller
                .SendAsync(HubRoutes.Events.Error, method, "Internal server error.");
            return null;
        }
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        _limiter.DropConnection(context.Context.ConnectionId);
        await next(context, exception);
    }
}
