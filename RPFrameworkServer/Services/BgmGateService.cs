using Microsoft.AspNetCore.SignalR;
using RPFramework.Contracts;
using RPFrameworkServer.Hubs;

namespace RPFrameworkServer.Services;

/// <summary>
/// Drives the "waiting for members" prepare gates to completion. A gate normally commits the instant the
/// last active listener reports ready (handled inline in the hub), but a slow/stuck/AFK member must not
/// freeze the room — so this sweep commits any gate whose deadline has passed (or whose active listeners
/// have all become ready after a member dropped). Runs once a second; broadcasts the synchronized-start
/// state to the room group.
/// </summary>
public class BgmGateService(SessionManager sessions, IHubContext<RpHub> hub, ILogger<BgmGateService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            try
            {
                foreach (var room in await sessions.TickGatesAsync())
                    await hub.Clients.Group("room:" + room.Code).SendAsync(HubRoutes.Events.RoomUpdated, room, ct);
            }
            catch (Exception ex) { log.LogWarning(ex, "BGM gate sweep failed"); }
        }
    }
}
