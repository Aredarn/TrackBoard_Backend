using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TrackBoard.Common;
using TrackBoard.Data;
using TrackBoard.Dtos;
using TrackBoard.Entities;
using TrackBoard.Storage;

namespace TrackBoard.Services;

/// <summary>Limits for the personal backup, from the <c>Backup</c> section.</summary>
public class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Largest single archive. Default 50 MiB.</summary>
    [Range(1, long.MaxValue)]
    public long MaxArchiveBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Total archive bytes one driver may keep. Default 500 MiB.</summary>
    [Range(1, long.MaxValue)]
    public long QuotaBytesPerUser { get; set; } = 500L * 1024 * 1024;
}

public interface IBackupService
{
    Task<BackupUploadResponse> CreateUploadAsync(Guid userId, Guid id, long sizeBytes, CancellationToken ct);

    Task<UpsertResult<BackupSessionResponse>> CommitAsync(Guid userId, Guid id, CommitBackupRequest request, CancellationToken ct);

    Task<PagedResult<BackupSessionResponse>> ListAsync(Guid userId, BackupPageQuery page, CancellationToken ct);

    Task<BackupDownloadResponse> CreateDownloadAsync(Guid userId, Guid id, CancellationToken ct);

    Task DeleteAsync(Guid userId, Guid id, CancellationToken ct);

    Task<BackupSummaryResponse> GetSummaryAsync(Guid userId, CancellationToken ct);

    Task<BackupSettingsResponse?> GetSettingsAsync(Guid userId, CancellationToken ct);

    Task<BackupSettingsResponse> PutSettingsAsync(Guid userId, PutBackupSettingsRequest request, CancellationToken ct);
}

/// <summary>
/// The driver's personal backup: one archive per recorded session in a private bucket, plus a
/// preferences document. Archives go to storage directly through signed URLs, so the API only
/// ever handles metadata.
/// </summary>
public partial class BackupService(
    TrackBoardDbContext db,
    IBackupStorage storage,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider) : IBackupService
{
    /// <summary>The preferences document's limit, in UTF-8 bytes.</summary>
    public const int MaxSettingsBytes = 64 * 1024;

    /// <summary>Long enough to start a several-MB download on a slow connection, short enough not to share.</summary>
    public static readonly TimeSpan DownloadLifetime = TimeSpan.FromMinutes(10);

    private readonly BackupOptions _options = options.Value;

    public async Task<BackupUploadResponse> CreateUploadAsync(
        Guid userId,
        Guid id,
        long sizeBytes,
        CancellationToken ct)
    {
        await EnsureReadyAsync(userId, ct);
        await EnsureFitsAsync(userId, id, sizeBytes, ct);

        // A fresh object per upload: the previous archive stays readable until the new one is
        // committed, so an interrupted upload never leaves the session with nothing.
        var signed = await storage.CreateSignedUploadAsync($"{Folder(userId, id)}{Guid.NewGuid():N}.json.gz", ct);
        return new BackupUploadResponse(signed.UploadUrl, signed.Path);
    }

    public async Task<UpsertResult<BackupSessionResponse>> CommitAsync(
        Guid userId,
        Guid id,
        CommitBackupRequest request,
        CancellationToken ct)
    {
        await EnsureReadyAsync(userId, ct);

        if (!IsArchivePath(request.Path, userId, id))
        {
            throw new ForbiddenException("That archive was not uploaded for this session.");
        }

        var existing = await db.BackupSessions.FirstOrDefaultAsync(b => b.OwnerId == userId && b.Id == id, ct);
        var replacing = existing is not null && existing.Path != request.Path;

        var info = await storage.GetInfoAsync(request.Path, ct)
            ?? throw new ConflictException(
                "Nothing has been uploaded to that path. Upload the archive before committing it.",
                "BackupObjectMissing");

        // The size the bucket stored is what counts, not the one the app declared: a signed
        // upload does not limit how much is sent through it.
        var sizeBytes = info.SizeBytes ?? request.SizeBytes!.Value;

        try
        {
            await EnsureFitsAsync(userId, id, sizeBytes, ct);
        }
        catch (Exception ex) when (ex is PayloadTooLargeException or ConflictException && existing?.Path != request.Path)
        {
            // Refused, so nothing will ever point at the new object.
            await storage.DeleteAsync([request.Path], ct);
            throw;
        }

        var previousPath = existing?.Path;
        var backup = existing ?? db.BackupSessions.Add(new BackupSession { OwnerId = userId, Id = id }).Entity;

        backup.Path = request.Path;
        backup.ContentHash = request.ContentHash.ToLowerInvariant();
        backup.SizeBytes = sizeBytes;
        backup.SchemaVersion = request.SchemaVersion!.Value;
        backup.Kind = request.Kind!.Value;
        backup.StartedAt = request.StartedAt!.Value;

        await db.SaveChangesAsync(ct);

        if (replacing)
        {
            await storage.DeleteAsync([previousPath], ct);
        }

        return new UpsertResult<BackupSessionResponse>(ToResponse(backup), existing is null);
    }

    public async Task<PagedResult<BackupSessionResponse>> ListAsync(
        Guid userId,
        BackupPageQuery page,
        CancellationToken ct)
    {
        await EnsureReadyAsync(userId, ct);

        var query = db.BackupSessions.AsNoTracking().Where(b => b.OwnerId == userId);
        var total = await query.LongCountAsync(ct);

        var items = await query
            .OrderByDescending(b => b.StartedAt)
            .ThenBy(b => b.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(b => new BackupSessionResponse(
                b.Id, b.ContentHash, b.SizeBytes, b.SchemaVersion, b.Kind, b.StartedAt, b.UpdatedAt))
            .ToListAsync(ct);

        return new PagedResult<BackupSessionResponse>(items, page.Page, page.PageSize, total);
    }

    public async Task<BackupDownloadResponse> CreateDownloadAsync(Guid userId, Guid id, CancellationToken ct)
    {
        await EnsureReadyAsync(userId, ct);

        // Another driver's id and no id at all look the same from here: both are not found.
        var path = await db.BackupSessions
            .AsNoTracking()
            .Where(b => b.OwnerId == userId && b.Id == id)
            .Select(b => b.Path)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Backup", id);

        var expiresAt = timeProvider.GetUtcNow() + DownloadLifetime;
        var url = await storage.CreateSignedDownloadAsync(path, DownloadLifetime, ct);

        return new BackupDownloadResponse(url, expiresAt);
    }

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        await EnsureReadyAsync(userId, ct);

        var backup = await db.BackupSessions.FirstOrDefaultAsync(b => b.OwnerId == userId && b.Id == id, ct);

        if (backup is not null)
        {
            db.BackupSessions.Remove(backup);
            await db.SaveChangesAsync(ct);
            await storage.DeleteAsync([backup.Path], ct);
        }

        // Also catches uploads that were sent but never committed.
        await storage.DeleteFolderAsync(Folder(userId, id), ct);
    }

    public async Task<BackupSummaryResponse> GetSummaryAsync(Guid userId, CancellationToken ct)
    {
        await EnsureReadyAsync(userId, ct);

        var backups = db.BackupSessions.Where(b => b.OwnerId == userId);
        var count = await backups.CountAsync(ct);
        var total = await backups.SumAsync(b => (long?)b.SizeBytes, ct) ?? 0;

        var settingsUpdatedAt = await db.BackupSettings
            .Where(s => s.OwnerId == userId)
            .Select(s => (DateTimeOffset?)s.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        return new BackupSummaryResponse(count, total, _options.QuotaBytesPerUser, settingsUpdatedAt);
    }

    public async Task<BackupSettingsResponse?> GetSettingsAsync(Guid userId, CancellationToken ct)
    {
        await EnsureReadyAsync(userId, ct);

        var settings = await db.BackupSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerId == userId, ct);
        return settings is null ? null : ToResponse(settings);
    }

    public async Task<BackupSettingsResponse> PutSettingsAsync(
        Guid userId,
        PutBackupSettingsRequest request,
        CancellationToken ct)
    {
        await EnsureReadyAsync(userId, ct);

        var data = request.Data.GetRawText();

        if (Encoding.UTF8.GetByteCount(data) > MaxSettingsBytes)
        {
            throw new PayloadTooLargeException(
                $"The settings document is over {MaxSettingsBytes / 1024} KB.",
                "BackupSettingsTooLarge");
        }

        var settings = await db.BackupSettings.FirstOrDefaultAsync(s => s.OwnerId == userId, ct)
            ?? db.BackupSettings.Add(new BackupSettings { OwnerId = userId }).Entity;

        settings.SchemaVersion = request.SchemaVersion!.Value;
        settings.Data = data;
        settings.UpdatedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return ToResponse(settings);
    }

    /// <summary>The folder every archive of one session lives in.</summary>
    public static string Folder(Guid userId, Guid id) => $"users/{userId:N}/sessions/{id:N}/";

    /// <summary>Every archive the driver has, across all sessions.</summary>
    public static string UserFolder(Guid userId) => $"users/{userId:N}/sessions/";

    public static BackupSessionResponse ToResponse(BackupSession b) =>
        new(b.Id, b.ContentHash, b.SizeBytes, b.SchemaVersion, b.Kind, b.StartedAt, b.UpdatedAt);

    public static BackupSettingsResponse ToResponse(BackupSettings s)
    {
        using var document = JsonDocument.Parse(s.Data);
        return new BackupSettingsResponse(s.SchemaVersion, document.RootElement.Clone(), s.UpdatedAt);
    }

    /// <summary>
    /// Only a path this server would have signed for the caller and this session: one object
    /// directly in the session's folder, named the way <see cref="CreateUploadAsync"/> names it.
    /// Without this, a driver could commit someone else's object and later delete it.
    /// </summary>
    private static bool IsArchivePath(string path, Guid userId, Guid id)
    {
        var folder = Folder(userId, id);
        return path.StartsWith(folder, StringComparison.Ordinal)
            && ArchiveName().IsMatch(path.AsSpan(folder.Length));
    }

    /// <summary>
    /// Refuses an archive over the per-archive limit, or one that would take the driver past
    /// their quota. The archive it would replace does not count, since it goes on commit.
    /// </summary>
    private async Task EnsureFitsAsync(Guid userId, Guid id, long sizeBytes, CancellationToken ct)
    {
        if (sizeBytes > _options.MaxArchiveBytes)
        {
            throw new PayloadTooLargeException(
                $"An archive can be at most {_options.MaxArchiveBytes / (1024 * 1024)} MB.",
                "BackupTooLarge");
        }

        var used = await db.BackupSessions
            .Where(b => b.OwnerId == userId && b.Id != id)
            .SumAsync(b => (long?)b.SizeBytes, ct) ?? 0;

        if (used + sizeBytes > _options.QuotaBytesPerUser)
        {
            throw new ConflictException(
                $"This would take your backup past its {_options.QuotaBytesPerUser / (1024 * 1024)} MB quota.",
                "BackupQuotaExceeded");
        }
    }

    /// <summary>
    /// 503 before anything else when there is no bucket, and 401 for an account deleted after
    /// its token was issued: tokens stay valid until they expire.
    /// </summary>
    private async Task EnsureReadyAsync(Guid userId, CancellationToken ct)
    {
        if (!storage.IsConfigured)
        {
            throw new ServiceUnavailableException("Backup storage is not configured on this server.");
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId && u.DeletedAt == null, ct))
        {
            throw new UnauthorizedException("This account no longer exists.");
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.json\\.gz$")]
    private static partial Regex ArchiveName();
}
