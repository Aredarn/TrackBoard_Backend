using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using TrackBoard.Entities;

namespace TrackBoard.Dtos;

/// <summary>Step one of a backup upload.</summary>
public record CreateBackupUploadRequest
{
    /// <summary>The archive's size, checked against the per-archive limit and the quota before signing.</summary>
    [Required]
    [Range(1, long.MaxValue)]
    public long? SizeBytes { get; init; }
}

/// <param name="UploadUrl">PUT the gzip'd bytes here with <c>Content-Type: application/gzip</c>. Single use.</param>
/// <param name="Path">Pass back to <c>PUT /me/backup/sessions/{id}</c> once the upload succeeded.</param>
public record BackupUploadResponse(string UploadUrl, string Path);

/// <summary>Step two: records an uploaded archive against the session id.</summary>
public record CommitBackupRequest
{
    [Required]
    [MaxLength(300)]
    public string Path { get; init; } = string.Empty;

    /// <summary>SHA-256 of the archive bytes, 64 hex characters. Stored lowercase.</summary>
    [Required]
    [RegularExpression("^[0-9a-fA-F]{64}$", ErrorMessage = "contentHash must be a SHA-256 in hex (64 characters).")]
    public string ContentHash { get; init; } = string.Empty;

    [Required]
    [Range(1, long.MaxValue)]
    public long? SizeBytes { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? SchemaVersion { get; init; }

    [Required]
    [EnumDataType(typeof(BackupKind))]
    public BackupKind? Kind { get; init; }

    [Required]
    public DateTimeOffset? StartedAt { get; init; }
}

/// <param name="SizeBytes">The size the bucket reports for the stored object.</param>
public record BackupSessionResponse(
    Guid Id,
    string ContentHash,
    long SizeBytes,
    int SchemaVersion,
    BackupKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Url">GET the archive here. Works without a token until <paramref name="ExpiresAt"/>.</param>
public record BackupDownloadResponse(string Url, DateTimeOffset ExpiresAt);

public record BackupSummaryResponse(
    int SessionCount,
    long TotalBytes,
    long QuotaBytes,
    DateTimeOffset? SettingsUpdatedAt);

/// <summary>The app's preferences document. The server stores it as sent and never reads inside it.</summary>
public record PutBackupSettingsRequest : IValidatableObject
{
    [Required]
    [Range(1, int.MaxValue)]
    public int? SchemaVersion { get; init; }

    /// <summary>Any JSON object, at most <see cref="Services.BackupService.MaxSettingsBytes"/> bytes.</summary>
    public JsonElement Data { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Data.ValueKind != JsonValueKind.Object)
        {
            yield return new ValidationResult("data must be a JSON object.", [nameof(Data)]);
        }
    }
}

public record BackupSettingsResponse(int SchemaVersion, JsonElement Data, DateTimeOffset UpdatedAt);

/// <summary>Query-string paging for the backup list, which allows bigger pages than elsewhere.</summary>
public class BackupPageQuery
{
    /// <summary>
    /// A restore lists every archive at once, and the rows are small, so a page holds up to
    /// 500 rather than the usual 100.
    /// </summary>
    public const int MaxPageSize = 500;

    private int _pageSize = MaxPageSize;

    [Range(1, int.MaxValue, ErrorMessage = "page must be 1 or greater.")]
    public int Page { get; set; } = 1;

    /// <summary>Capped at <see cref="MaxPageSize"/>, like <see cref="Common.PageQuery"/>.</summary>
    [Range(1, MaxPageSize)]
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value > MaxPageSize ? MaxPageSize : value;
    }

    public int Skip => (Page - 1) * PageSize;
}

/// <summary>The backup part of the account export: what is stored, not the archives themselves.</summary>
public record ExportBackup(
    IReadOnlyList<BackupSessionResponse> Sessions,
    BackupSettingsResponse? Settings);
