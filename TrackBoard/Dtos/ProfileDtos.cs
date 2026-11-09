using System.ComponentModel.DataAnnotations;
using TrackBoard.Entities;

namespace TrackBoard.Dtos;

/// <summary>The signed-in driver's own profile. A superset of <see cref="AuthenticatedUserResponse"/>.</summary>
public record ProfileResponse(
    Guid Id,
    string Email,
    string DisplayName,
    UserRole Role,
    string? Bio,
    string? Country,
    string? AvatarUrl,
    DateTimeOffset MemberSince);

/// <summary>
/// Partial update: a field left out (null) keeps its value. Send an empty string to clear
/// <see cref="Bio"/> or <see cref="Country"/>; <see cref="DisplayName"/> cannot be cleared.
/// </summary>
public record UpdateProfileRequest
{
    [MinLength(2)]
    [MaxLength(100)]
    public string? DisplayName { get; init; }

    [MaxLength(500)]
    public string? Bio { get; init; }

    [MaxLength(60)]
    public string? Country { get; init; }
}

public enum MediaKind
{
    Avatar,
    VehiclePhoto,
}

public record CreateUploadRequest : IValidatableObject
{
    [Required]
    [EnumDataType(typeof(MediaKind))]
    public MediaKind? Kind { get; init; }

    /// <summary>Required for <see cref="MediaKind.VehiclePhoto"/>; the vehicle must be the caller's.</summary>
    public Guid? VehicleId { get; init; }

    /// <summary>Only JPEG and WebP: the app re-encodes before uploading, so nothing else is expected.</summary>
    [Required]
    [RegularExpression("^image/(jpeg|webp)$", ErrorMessage = "contentType must be image/jpeg or image/webp.")]
    public string ContentType { get; init; } = "image/jpeg";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Kind == MediaKind.VehiclePhoto && VehicleId is null)
        {
            yield return new ValidationResult("A vehicle photo upload needs a vehicleId.", [nameof(VehicleId)]);
        }
    }
}

/// <param name="UploadUrl">PUT the bytes here, with the same Content-Type, within a couple of hours.</param>
/// <param name="Path">Pass back to the matching "set photo" endpoint once the upload succeeded.</param>
public record UploadTargetResponse(string UploadUrl, string Path, string PublicUrl);

public record SetMediaRequest
{
    [Required]
    [MaxLength(300)]
    public string Path { get; init; } = string.Empty;
}

public record ProfileVehicleRef(Guid Id, string Manufacturer, string Model, int Year, string? PhotoUrl);

/// <summary>A driver's best lap on one track, and where it places them.</summary>
/// <param name="BestLapMs">
/// Fastest valid lap from any of the driver's sessions, ranked or private.
/// </param>
/// <param name="Rank">
/// Position on the public leaderboard, which only counts ranked sessions on published
/// tracks. Null when the driver has no standing there, even if they have laps.
/// </param>
/// <param name="RankedLapMs">
/// The time the rank is for. Differs from <see cref="BestLapMs"/> when the fastest lap came
/// from a private session.
/// </param>
public record PersonalBestResponse(
    Guid TrackId,
    string TrackName,
    string Country,
    TrackType TrackType,
    int BestLapMs,
    DateTimeOffset SetAt,
    ProfileVehicleRef? Vehicle,
    int LapCount,
    int? Rank,
    int? FieldSize,
    int? RankedLapMs);

/// <param name="DistanceKm">Sum of valid laps times their track's length. Null when no lap has a known length.</param>
/// <param name="MainVehicle">The vehicle with the most sessions.</param>
/// <param name="PersonalBests">Ranked tracks first, best position first; then the rest by track name.</param>
public record ProfileStatsResponse(
    int SessionCount,
    int LapCount,
    int TrackCount,
    int VehicleCount,
    double? DistanceKm,
    DateTimeOffset? FirstSessionAt,
    DateTimeOffset? LastSessionAt,
    ProfileVehicleRef? MainVehicle,
    IReadOnlyList<PersonalBestResponse> PersonalBests);

// ── Export ────────────────────────────────────────────────────────────────────

public record ExportLap(int LapNumber, int TimeMs, bool SignalGap, IReadOnlyList<SectorSplitDto> Sectors, double? TopSpeedKmh);

public record ExportDragResult(DragMetric Metric, int TimeMs, double? TrapSpeedKmh);

public record ExportSession(
    Guid Id,
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    Guid? VehicleId,
    Guid? TrackId,
    GpsSource GpsSource,
    SessionVisibility Visibility,
    bool Voided,
    string? AppVersion,
    IReadOnlyList<ExportLap> Laps,
    IReadOnlyList<ExportDragResult> DragResults);

public record ExportTrackPoint(
    int Seq,
    double Latitude,
    double Longitude,
    double? Altitude,
    bool IsStartPoint,
    bool IsSectorPoint,
    int? SectorIndex);

public record ExportTrack(
    Guid Id,
    string Name,
    string Country,
    TrackType Type,
    double? LengthMeters,
    TrackVisibility Visibility,
    IReadOnlyList<ExportTrackPoint> Points);

/// <param name="Hosted">True for events the caller hosts; false for ones they joined.</param>
/// <param name="Group">The caller's run group, when they joined one.</param>
public record ExportEvent(
    Guid Id,
    string Name,
    Guid TrackId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool Hosted,
    string? Group);

/// <summary>Everything the server holds about the caller, in one document.</summary>
public record AccountExportResponse(
    DateTimeOffset ExportedAt,
    ProfileResponse Profile,
    IReadOnlyList<VehicleResponse> Vehicles,
    IReadOnlyList<ExportTrack> Tracks,
    IReadOnlyList<ExportSession> Sessions,
    IReadOnlyList<ExportEvent> Events);
