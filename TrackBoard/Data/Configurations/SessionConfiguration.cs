using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrackBoard.Entities;

namespace TrackBoard.Data.Configurations;

public class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");

        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.AppVersion).HasMaxLength(40);
        builder.Property(s => s.GpsSource).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Visibility).HasConversion<string>().HasMaxLength(20);

        builder
            .HasOne(s => s.Owner)
            .WithMany()
            .HasForeignKey(s => s.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasOne(s => s.Vehicle)
            .WithMany()
            .HasForeignKey(s => s.VehicleId)
            // A vehicle with uploaded sessions cannot be deleted out from under them.
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasOne(s => s.Track)
            .WithMany(t => t.Sessions)
            .HasForeignKey(s => s.TrackId)
            // Deleting your own track keeps your sessions; they just lose the link.
            // Deleting a track other drivers use is refused in the service layer first.
            .OnDelete(DeleteBehavior.SetNull);

        builder
            .HasMany(s => s.Laps)
            .WithOne(l => l.Session)
            .HasForeignKey(l => l.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasMany(s => s.DragResults)
            .WithOne(r => r.Session)
            .HasForeignKey(r => r.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.OwnerId, s.StartedAt });
        builder.HasIndex(s => s.VehicleId);
        // The leaderboard reads every ranked, non-voided session on a track.
        builder.HasIndex(s => new { s.TrackId, s.Visibility, s.Voided });
    }
}

public class LapConfiguration : IEntityTypeConfiguration<Lap>
{
    public void Configure(EntityTypeBuilder<Lap> builder)
    {
        builder.ToTable("laps");

        builder
            .HasMany(l => l.Sectors)
            .WithOne()
            .HasForeignKey(s => s.LapId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => new { l.SessionId, l.LapNumber }).IsUnique();
    }
}

public class DragResultConfiguration : IEntityTypeConfiguration<DragResult>
{
    public void Configure(EntityTypeBuilder<DragResult> builder)
    {
        builder.ToTable("drag_results");

        builder.Property(r => r.Metric).HasConversion<string>().HasMaxLength(30);

        builder.HasIndex(r => new { r.SessionId, r.Metric }).IsUnique();
        // Every board reads one metric, fastest first.
        builder.HasIndex(r => new { r.Metric, r.TimeMs });
    }
}

public class LapSectorConfiguration : IEntityTypeConfiguration<LapSector>
{
    public void Configure(EntityTypeBuilder<LapSector> builder)
    {
        builder.ToTable("lap_sectors");

        builder.HasKey(s => new { s.LapId, s.SectorIndex });
    }
}
