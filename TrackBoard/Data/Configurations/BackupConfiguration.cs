using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrackBoard.Entities;

namespace TrackBoard.Data.Configurations;

public class BackupSessionConfiguration : IEntityTypeConfiguration<BackupSession>
{
    public void Configure(EntityTypeBuilder<BackupSession> builder)
    {
        builder.ToTable("backup_sessions");

        // The id is the app's, so it is only unique within one driver's backups.
        builder.HasKey(b => new { b.OwnerId, b.Id });

        builder.Property(b => b.Path).IsRequired().HasMaxLength(300);
        builder.Property(b => b.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(b => b.Kind).HasConversion<string>().HasMaxLength(10);

        builder
            .HasOne(b => b.Owner)
            .WithMany()
            .HasForeignKey(b => b.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        // The list endpoint pages a driver's backups newest first.
        builder.HasIndex(b => new { b.OwnerId, b.StartedAt });
    }
}

public class BackupSettingsConfiguration : IEntityTypeConfiguration<BackupSettings>
{
    public void Configure(EntityTypeBuilder<BackupSettings> builder)
    {
        builder.ToTable("backup_settings");

        builder.HasKey(s => s.OwnerId);

        // Plain text rather than jsonb: the server never queries inside it, and text keeps the
        // document exactly as the app wrote it.
        builder.Property(s => s.Data).IsRequired();

        builder
            .HasOne(s => s.Owner)
            .WithOne()
            .HasForeignKey<BackupSettings>(s => s.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
