using Microsoft.EntityFrameworkCore;

namespace RPFrameworkServer.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<PlayerEntity>         Players         { get; set; } = null!;
    public DbSet<CampaignEntity>       Campaigns       { get; set; } = null!;
    public DbSet<CampaignMemberEntity> CampaignMembers { get; set; } = null!;
    public DbSet<CharacterEntity>      Characters      { get; set; } = null!;
    public DbSet<BagEntity>            Bags            { get; set; } = null!;
    public DbSet<BgmRoomEntity>        BgmRooms        { get; set; } = null!;
    public DbSet<BgmRoomMemberEntity>  BgmRoomMembers  { get; set; } = null!;
    public DbSet<BgmSongEntity>        BgmSongs        { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Composite PK for campaign membership
        modelBuilder.Entity<CampaignMemberEntity>()
            .HasKey(e => new { e.Code, e.PlayerId });

        // Composite PK for in-world entities: one row per (campaign, entity)
        modelBuilder.Entity<CharacterEntity>()
            .HasKey(e => new { e.CampaignCode, e.EntityId });

        // Composite PK for BGM room membership
        modelBuilder.Entity<BgmRoomMemberEntity>()
            .HasKey(e => new { e.RoomCode, e.PlayerId });

        modelBuilder.Entity<CampaignMemberEntity>().HasIndex(e => e.PlayerId);
        modelBuilder.Entity<CharacterEntity>().HasIndex(e => e.CampaignCode);
        modelBuilder.Entity<BagEntity>().HasIndex(e => e.CampaignCode);
        modelBuilder.Entity<BgmSongEntity>().HasIndex(e => e.RoomCode);
        modelBuilder.Entity<BgmRoomMemberEntity>().HasIndex(e => e.PlayerId);
    }
}
