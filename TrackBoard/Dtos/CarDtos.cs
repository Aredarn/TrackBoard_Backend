using TrackBoard.Entities;

namespace TrackBoard.Dtos;

/// <summary>A car's best lap on one published track, as it stands on that track's board.</summary>
public record CarTrackBestResponse(
    Guid TrackId,
    string TrackName,
    string Country,
    TrackType TrackType,
    int Rank,
    int FieldSize,
    int LapTimeMs,
    int GapToLeaderMs,
    double? TopSpeedKmh,
    DateTimeOffset SetAt,
    GpsSource GpsSource);

/// <summary>A car's best on one drag metric, as it stands on the overall board.</summary>
public record CarDragBestResponse(
    DragMetric Metric,
    int Rank,
    int FieldSize,
    int TimeMs,
    int GapToLeaderMs,
    double? TrapSpeedKmh,
    DateTimeOffset SetAt,
    GpsSource GpsSource);

/// <summary>
/// A car's public page: its garage entry and where it stands on the public boards. Only cars
/// with a place on a public board have one, so a car kept to private sessions stays private.
/// </summary>
/// <param name="TrackBests">Best position first, then by track name.</param>
/// <param name="DragBests">In metric order.</param>
public record PublicCarResponse(
    Guid Id,
    Guid OwnerId,
    string OwnerDisplayName,
    string Manufacturer,
    string Model,
    int Year,
    string? PhotoUrl,
    string EngineType,
    int Horsepower,
    int? Torque,
    double Weight,
    double? TopSpeed,
    double? Acceleration,
    string Drivetrain,
    string FuelType,
    string TireType,
    string Transmission,
    string? SuspensionType,
    DragClass? CarClass,
    IReadOnlyList<CarTrackBestResponse> TrackBests,
    IReadOnlyList<CarDragBestResponse> DragBests);
