using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using TrackBoard.Caching;
using TrackBoard.Common;
using TrackBoard.Data;
using TrackBoard.Dtos;
using TrackBoard.Entities;

namespace TrackBoard.Services;

/// <summary>
/// One car's standing on a track, as cached, so a driver with several cars has one for each.
/// Laps with no car on them share a single standing per driver. Carries the lap id so a session
/// can look up its own lap's rank from the same cached entry.
/// </summary>
public sealed record TrackStanding(
    Guid LapId,
    int Rank,
    Guid UserId,
    string DisplayName,
    int LapTimeMs,
    int GapToLeaderMs,
    SectorSplitDto[] Sectors,
    LeaderboardVehicleResponse? Vehicle,
    DateTimeOffset SetAt,
    GpsSource GpsSource,
    double? TopSpeedKmh = null);

public interface ITrackLeaderboardService
{
    /// <summary>
    /// The public leaderboard: a line per car, or with <paramref name="perCar"/> false a line per
    /// driver. Throws <see cref="NotFoundException"/> unless the track is published.
    /// </summary>
    Task<TrackLeaderboardResponse> GetAsync(
        Guid trackId,
        int limit,
        bool perCar,
        Guid? viewerId,
        CancellationToken ct);

    /// <summary>Every car's standing on a track, fastest first. Served from cache.</summary>
    Task<IReadOnlyList<TrackStanding>> GetStandingsAsync(Guid trackId, CancellationToken ct);

    /// <summary>Drops the cached standings of each track. Call after the write commits.</summary>
    Task InvalidateAsync(IEnumerable<Guid?> trackIds, CancellationToken ct);
}

public class TrackLeaderboardService(
    TrackBoardDbContext db,
    HybridCache cache,
    CacheMetrics metrics,
    IOptions<CacheSettings> cacheSettings) : ITrackLeaderboardService
{
    private readonly HybridCacheEntryOptions _entryOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(cacheSettings.Value.LeaderboardSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.Value.LeaderboardLocalSeconds),
    };

    public async Task<TrackLeaderboardResponse> GetAsync(
        Guid trackId,
        int limit,
        bool perCar,
        Guid? viewerId,
        CancellationToken ct)
    {
        // Read fresh rather than cached: a rename must show immediately, and this doubles as
        // the "is it published" check — an unpublished track has no public leaderboard.
        var trackName = await db.Tracks
            .Where(t => t.Id == trackId && t.Visibility == TrackVisibility.Published)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Track", trackId);

        var standings = await GetStandingsAsync(trackId, ct);

        // Fastest first, so a driver's first appearance is their best. The app before 1.5 keys
        // its list by driver and crashes on a repeat, so a line per car has to be asked for.
        var field = perCar ? standings : standings.DistinctBy(s => s.UserId).ToList();

        var entries = field.Select((s, i) => ToResponse(s, i + 1)).ToList();

        return new TrackLeaderboardResponse(
            trackId,
            trackName,
            entries.Take(limit).ToList(),
            viewerId is null ? null : entries.FirstOrDefault(e => e.UserId == viewerId));
    }

    public async Task<IReadOnlyList<TrackStanding>> GetStandingsAsync(Guid trackId, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var wasMiss = false;

        var standings = await cache.GetOrCreateAsync(
            CacheKeys.TrackStandings(trackId),
            async token =>
            {
                wasMiss = true;
                return await ComputeStandingsAsync(trackId, token);
            },
            _entryOptions,
            [CacheKeys.TrackTag(trackId)],
            ct);

        metrics.RecordLeaderboard(wasMiss, Stopwatch.GetElapsedTime(startedAt));

        return standings;
    }

    public async Task InvalidateAsync(IEnumerable<Guid?> trackIds, CancellationToken ct)
    {
        foreach (var trackId in trackIds.OfType<Guid>().Distinct())
        {
            await cache.RemoveByTagAsync(CacheKeys.TrackTag(trackId), ct);
        }
    }

    private async Task<TrackStanding[]> ComputeStandingsAsync(Guid trackId, CancellationToken ct)
    {
        var countingLaps = db.Laps
            .AsNoTracking()
            .Where(LeaderboardRules.CountsForLeaderboard)
            .Where(l => l.Session.TrackId == trackId);

        // Each car's fastest time, computed in SQL. A null would never match in the join below,
        // so a lap with no car goes in under the empty id...
        var bestTimes = countingLaps
            .GroupBy(l => new { l.Session.OwnerId, CarId = l.Session.VehicleId ?? Guid.Empty })
            .Select(g => new { g.Key.OwnerId, g.Key.CarId, TimeMs = g.Min(l => l.TimeMs) });

        // ...then only the laps that equal it come back — one per car, or a few if a car matched
        // its own best exactly — rather than every lap on the track.
        var candidates = await (
            from lap in countingLaps
            join best in bestTimes
                on new { lap.Session.OwnerId, CarId = lap.Session.VehicleId ?? Guid.Empty, lap.TimeMs }
                equals new { best.OwnerId, best.CarId, best.TimeMs }
            select new
            {
                LapId = lap.Id,
                lap.Session.VehicleId,
                UserId = lap.Session.OwnerId,
                lap.Session.Owner.DisplayName,
                lap.TimeMs,
                lap.TopSpeedKmh,
                SetAt = lap.Session.StartedAt,
                lap.Session.GpsSource,
                Manufacturer = lap.Session.Vehicle != null ? lap.Session.Vehicle.Manufacturer : null,
                Model = lap.Session.Vehicle != null ? lap.Session.Vehicle.Model : null,
                Year = lap.Session.Vehicle != null ? (int?)lap.Session.Vehicle.Year : null,
            })
            .ToListAsync(ct);

        // Ties are broken by whoever set the time first, both between cars and when a car
        // matched its own best. The lap id is a final, arbitrary but stable decider.
        var bestPerCar = candidates
            .GroupBy(c => new { c.UserId, c.VehicleId })
            .Select(g => g.OrderBy(c => c.SetAt).ThenBy(c => c.LapId).First())
            .OrderBy(c => c.TimeMs)
            .ThenBy(c => c.SetAt)
            .ThenBy(c => c.LapId)
            .ToList();

        var lapIds = bestPerCar.Select(c => c.LapId).ToList();

        var sectorsByLap = (await db.LapSectors
                .AsNoTracking()
                .Where(s => lapIds.Contains(s.LapId))
                .ToListAsync(ct))
            .GroupBy(s => s.LapId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(s => s.SectorIndex)
                    .Select(s => new SectorSplitDto { SectorIndex = s.SectorIndex, SplitMs = s.SplitMs })
                    .ToArray());

        var leaderMs = bestPerCar.Count > 0 ? bestPerCar[0].TimeMs : 0;

        return bestPerCar
            .Select((c, i) => new TrackStanding(
                c.LapId,
                i + 1,
                c.UserId,
                c.DisplayName,
                c.TimeMs,
                c.TimeMs - leaderMs,
                sectorsByLap.GetValueOrDefault(c.LapId, []),
                c.Manufacturer is null
                    ? null
                    : new LeaderboardVehicleResponse(c.Manufacturer, c.Model!, c.Year!.Value),
                c.SetAt,
                c.GpsSource,
                c.TopSpeedKmh))
            .ToArray();
    }

    private static TrackLeaderboardEntryResponse ToResponse(TrackStanding s, int rank) =>
        new(
            s.LapId,
            rank,
            s.UserId,
            s.DisplayName,
            s.LapTimeMs,
            s.GapToLeaderMs,
            s.Sectors,
            s.Vehicle,
            s.SetAt,
            s.GpsSource,
            s.TopSpeedKmh);
}
