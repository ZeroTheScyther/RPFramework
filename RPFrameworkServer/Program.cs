using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RPFramework.Contracts;
using RPFrameworkServer.Data;
using RPFrameworkServer.Hubs;
using RPFrameworkServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(opt =>
{
    opt.EnableDetailedErrors      = builder.Environment.IsDevelopment();
    opt.MaximumReceiveMessageSize = 512 * 1024; // 512 KB — enough for template / bag snapshots
    opt.ClientTimeoutInterval     = TimeSpan.FromSeconds(60);
    opt.KeepAliveInterval         = TimeSpan.FromSeconds(15);
    // Cross-cutting per-method rate limiting + exception containment/logging
    opt.AddFilter<RpHubFilter>();
});

builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<RpHubFilter>();
builder.Services.AddSingleton<RollService>();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=rpframework;Username=rpframework;Password=rpframework";
builder.Services.AddDbContextFactory<AppDbContext>(opt => opt.UseNpgsql(connectionString));

builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<BgmAudioService>();

// Periodic trade-offer cleanup
builder.Services.AddHostedService<TradeCleanupService>();
// Periodic BGM cache eviction (cold/stale tracks + global size ceiling).
builder.Services.AddHostedService<BgmCacheSweepService>();
// Drives the BGM "waiting for members" gates to a synchronized start (timeout / drop fallback).
builder.Services.AddHostedService<BgmGateService>();

var app = builder.Build();

// Server-first clean-break schema. While the schema is still evolving we use
// EnsureCreated against a fresh database; once it stabilizes this becomes a proper
// EF Core migration (db.Database.Migrate()). Retries because Postgres may still be
// starting up when the server boots (e.g. under docker-compose).
{
    var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var db = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
    for (int attempt = 1; ; attempt++)
    {
        try { db.Database.EnsureCreated(); break; }
        catch (Exception ex) when (attempt < 12)
        {
            startupLog.LogWarning("Database not ready (attempt {Attempt}): {Message}. Retrying in 5s…", attempt, ex.Message);
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
    }

    // Additive schema patches for databases created before these fields/tables existed (EnsureCreated
    // does not alter existing tables). Idempotent — no-ops on a fresh DB. Bridge until EF migrations.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE "BgmRooms" ADD COLUMN IF NOT EXISTS "CampaignCode"   text NOT NULL DEFAULT '';
        ALTER TABLE "BgmRooms" ADD COLUMN IF NOT EXISTS "Name"           text NOT NULL DEFAULT '';
        ALTER TABLE "BgmRooms" ADD COLUMN IF NOT EXISTS "HashedPassword" text NOT NULL DEFAULT '';
        CREATE TABLE IF NOT EXISTS "BgmRoomMembers" (
            "RoomCode" character varying(20) NOT NULL,
            "PlayerId" text NOT NULL,
            "Role"     integer NOT NULL DEFAULT 0,
            CONSTRAINT "PK_BgmRoomMembers" PRIMARY KEY ("RoomCode", "PlayerId")
        );
        ALTER TABLE "Bags" DROP COLUMN IF EXISTS "Gil";
    """);

    // Purge stale rooms with no members (e.g. left over from before persistent membership).
    db.Database.ExecuteSqlRaw("""
        DELETE FROM "BgmSongs" WHERE "RoomCode" NOT IN (SELECT "RoomCode" FROM "BgmRoomMembers");
        DELETE FROM "BgmRooms" WHERE "Code"     NOT IN (SELECT "RoomCode" FROM "BgmRoomMembers");
    """);
    db.Dispose();
}

app.MapHub<RpHub>(HubRoutes.Path);

// Health check endpoint
app.MapGet("/", () => Results.Ok(new { status = "RPFramework Relay", time = DateTime.UtcNow }));

// BGM audio: server-prepared WAV for a YouTube video id. Gated by the HMAC-signed URL minted over the
// hub (ResolveBgmAudio); range-enabled so clients can seek. The id is validated before it ever reaches
// the cache path or yt-dlp.
app.MapGet("/bgm/{videoId}", async (string videoId, long exp, string? sig, BgmAudioService audio, CancellationToken ct) =>
{
    if (!BgmAudioService.IsValidVideoId(videoId)) return Results.BadRequest();
    if (!audio.Verify(videoId, exp, sig))         return Results.Unauthorized();
    var path = await audio.GetWavPathAsync(videoId, ct);
    return path == null ? Results.NotFound() : Results.File(path, "audio/wav", enableRangeProcessing: true);
});

app.Run();

// ── Hosted service: clean up expired trade offers every 30 s ─────────────────

public class TradeCleanupService(SessionManager sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            sessions.PurgeStaleTrades();
        }
    }
}

// ── Hosted service: evict stale / over-ceiling BGM cache files every hour ─────
// Download-time eviction handles active growth; this catches the cold case (a busy day fills the cache,
// then nobody plays anything for a month - the stale TTL still reclaims it without any new download).

public class BgmCacheSweepService(BgmAudioService audio) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await audio.EnforceCacheLimitsAsync(); // once at startup
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(1), ct);
            await audio.EnforceCacheLimitsAsync();
        }
    }
}
