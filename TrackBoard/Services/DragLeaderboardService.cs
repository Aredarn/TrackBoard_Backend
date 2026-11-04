using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using TrackBoard.Caching;
using TrackBoard.Data;
using TrackBoard.Dtos;
using TrackBoard.Entities;

namespace TrackBoard.Services;

/// <summary>
/// One car's best time on a metric, as cached. A board has a line per car, not per driver, so a
/// driver with several cars stands on it once for each.
/// </summary>
public sealed record DragStanding(
    Guid ResultId,
    Guid UserId,
    string DisplayName,
    int TimeMs,
    double? TrapSpeedKmh,
    LeaderboardVehicleResponse Vehicle,
    DragClass? CarClass,
    DateTimeOffset SetAt,
    GpsSource GpsSource);

public interface IDragLeaderboardService
{
    /// <summary>The public board: each car's best, overall or within one class.</summary>
    Task<DragLeaderboardResponse> GetAsync(
        DragMetric metric,
        DragClass? carClass,
        int limit,
        Guid? viewerId,
        CancellationToken ct);

    /// <summary>Overall rank by result id, for the results that stand on the board.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetRanksAsync(DragMetric metric, CancellationToken ct);

    /// <summary>Drops every cached board. Call after the write commits.</summary>
    Task InvalidateAsync(CancellationToken ct);
}

public class DragLeaderboardService(
    TrackBoardDbContext db,
    HybridCache cache,
    CacheMetrics metrics,
    IOptions<CacheSettings> cacheSettings) : IDragLeaderboardService
{
    private readonly HybridCacheEntryOptions _entryOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(cacheSettings.Value.LeaderboardSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.Value.LeaderboardLocalSeconds),
    };

    public async Task<DragLeaderboardResponse> GetAsync(
        DragMetric metric,
        DragClass? carClass,
        int limit,
        Guid? viewerId,
        CancellationToken ct)
    {
        var standings = await GetStandingsAsync(metric, ct);

        var field = standings.Where(s => carClass is null || s.CarClass == carClass).ToList();
        var leaderMs = field.Count > 0 ? field[0].TimeMs : 0;

        var entries = field.Select((s, i) => ToResponse(s, i + 1, leaderMs)).ToList();

        return new DragLeaderboardResponse(
            metric,
            carClass,
            entries.Take(limit).ToList(),
            // Fastest first, so with several cars this is the viewer's quickest.
            viewerId is null ? null : entries.FirstOrDefault(e => e.UserId == viewerId));
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetRanksAsync(DragMetric metric, CancellationToken ct)
    {
        var standings = await GetStandingsAsync(metric, ct);

        return standings
            .Select((s, i) => (s.ResultId, Rank: i + 1))
            .ToDictionary(x => x.ResultId, x => x.Rank);
    }

    public async Task InvalidateAsync(CancellationToken ct) =>
        await cache.RemoveByTagAsync(CacheKeys.DragTag, ct);

    private async Task<DragStanding[]> GetStandingsAsync(DragMetric metric, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var wasMiss = false;

        var standings = await cache.GetOrCreateAsync(
            CacheKeys.DragStandings(metric),
            async token =>
            {
                wasMiss = true;
                return await ComputeStandingsAsync(metric, token);
            },
            _entryOptions,
            [CacheKeys.DragTag],
            ct);

        metrics.RecordLeaderboard(wasMiss, Stopwatch.GetElapsedTime(startedAt));

        return standings;
    }

    private async Task<DragStanding[]> ComputeStandingsAsync(DragMetric metric, CancellationToken ct)
    {
        var counting = db.DragResults
            .AsNoTracking()
            .Where(DragRules.CountsForBoard)
            .Where(r => r.Metric == metric);

        // Each car's fastest time, computed in SQL...
        var bestTimes = counting
            .GroupBy(r => r.Session.VehicleId)
            .Select(g => new { VehicleId = g.Key, TimeMs = g.Min(r => r.TimeMs) });

        // ...then only the results that equal it come back, rather than every run ever posted.
        var candidates = await (
            from result in counting
            join best in bestTimes
                on new { result.Session.VehicleId, result.TimeMs } equals new { best.VehicleId, best.TimeMs }
            select new
            {
                ResultId = result.Id,
                result.Session.VehicleId,
                UserId = result.Session.OwnerId,
                result.Session.Owner.DisplayName,
                result.TimeMs,
                result.TrapSpeedKmh,
                SetAt = result.Session.StartedAt,
                result.Session.GpsSource,
                result.Session.Vehicle!.Manufacturer,
                result.Session.Vehicle.Model,
                result.Session.Vehicle.Year,
                result.Session.Vehicle.Horsepower,
                result.Session.Vehicle.Weight,
            })
            .ToListAsync(ct);

        // Ties go to whoever set the time first, as on a track. The id is a final, arbitrary
        // but stable decider.
        return candidates
            .GroupBy(c => c.VehicleId)
            .Select(g => g.OrderBy(c => c.SetAt).ThenBy(c => c.ResultId).First())
            .OrderBy(c => c.TimeMs)
            .ThenBy(c => c.SetAt)
            .ThenBy(c => c.ResultId)
            .Select(c => new DragStanding(
                c.ResultId,
                c.UserId,
                c.DisplayName,
                c.TimeMs,
                c.TrapSpeedKmh,
                new LeaderboardVehicleResponse(c.Manufacturer, c.Model, c.Year),
                DragRules.ClassOf(c.Horsepower, c.Weight),
                c.SetAt,
                c.GpsSource))
            .ToArray();
    }

    private static DragLeaderboardEntryResponse ToResponse(DragStanding s, int rank, int leaderMs) =>
        new(
            s.ResultId,
            rank,
            s.UserId,
            s.DisplayName,
            s.TimeMs,
            s.TimeMs - leaderMs,
            s.TrapSpeedKmh,
            s.Vehicle,
            s.CarClass,
            s.SetAt,
            s.GpsSource);
}
