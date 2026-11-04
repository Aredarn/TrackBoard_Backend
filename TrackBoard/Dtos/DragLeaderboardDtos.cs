using TrackBoard.Entities;

namespace TrackBoard.Dtos;

public record DragLeaderboardEntryResponse(
    Guid ResultId,
    int Rank,
    Guid UserId,
    string DisplayName,
    int TimeMs,
    int GapToLeaderMs,
    double? TrapSpeedKmh,
    LeaderboardVehicleResponse Vehicle,
    DragClass? CarClass,
    DateTimeOffset SetAt,
    GpsSource GpsSource);

public record DragLeaderboardResponse(
    DragMetric Metric,
    DragClass? CarClass,
    IReadOnlyList<DragLeaderboardEntryResponse> Entries,
    DragLeaderboardEntryResponse? Me);
