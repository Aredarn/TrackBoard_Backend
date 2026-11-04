using Microsoft.EntityFrameworkCore;
using TrackBoard.Entities;

namespace TrackBoard.Data;

public class TrackBoardDbContext(DbContextOptions<TrackBoardDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<Circuit> Circuits => Set<Circuit>();

    public DbSet<Series> Series => Set<Series>();

    public DbSet<PointsScheme> PointsSchemes => Set<PointsScheme>();

    public DbSet<PointsSchemeEntry> PointsSchemeEntries => Set<PointsSchemeEntry>();

    public DbSet<RaceEvent> RaceEvents => Set<RaceEvent>();

    public DbSet<Result> Results => Set<Result>();

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<TrackPoint> TrackPoints => Set<TrackPoint>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Lap> Laps => Set<Lap>();

    public DbSet<LapSector> LapSectors => Set<LapSector>();

    public DbSet<DragResult> DragResults => Set<DragResult>();

    public DbSet<TrackEvent> Events => Set<TrackEvent>();

    public DbSet<EventGroup> EventGroups => Set<EventGroup>();

    public DbSet<EventEntry> EventEntries => Set<EventEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TrackBoardDbContext).Assembly);
    }
}
