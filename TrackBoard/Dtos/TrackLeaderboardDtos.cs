using TrackBoard.Entities;

namespace TrackBoard.Dtos;

/// <param name="Id">
/// The car, for its public page at <c>/api/v1/cars/{id}</c>. Null on event boards, which can
/// carry laps from private sessions.
/// </param>
public record LeaderboardVehicleResponse(string Manufacturer, string Model, int Year, Guid? Id = null);

public record TrackLeaderboardEntryResponse(
    Guid LapId,
    int Rank,
    Guid UserId,
    string DisplayName,
    int LapTimeMs,
    int GapToLeaderMs,
    IReadOnlyList<SectorSplitDto> Sectors,
    LeaderboardVehicleResponse? Vehicle,
    DateTimeOffset SetAt,
    GpsSource GpsSource,
    double? TopSpeedKmh);

public record TrackLeaderboardResponse(
    Guid TrackId,
    string TrackName,
    IReadOnlyList<TrackLeaderboardEntryResponse> Entries,
    TrackLeaderboardEntryResponse? Me);
