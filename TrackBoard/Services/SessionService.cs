using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TrackBoard.Auth;
using TrackBoard.Common;
using TrackBoard.Data;
using TrackBoard.Dtos;
using TrackBoard.Entities;

namespace TrackBoard.Services;

public interface ISessionService
{
    Task<PagedResult<SessionSummaryResponse>> ListAsync(
        Guid ownerId,
        Guid? trackId,
        PageQuery page,
        CancellationToken ct);

    /// <summary>Null when it does not exist; throws 403 when it belongs to someone else.</summary>
    Task<SessionResponse?> GetAsync(Guid id, CancellationToken ct);

    Task<UpsertResult<SessionResponse>> UpsertAsync(
        Guid id,
        Guid callerId,
        UpsertSessionRequest request,
        CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

public class SessionService(
    TrackBoardDbContext db,
    IResourceAuthorizer authorizer,
    ITrackLeaderboardService leaderboards,
    IDragLeaderboardService dragBoards) : ISessionService
{
    private static readonly Expression<Func<Session, SessionSummaryResponse>> ToSummary =
        s => new SessionSummaryResponse
        {
            Id = s.Id,
            Name = s.Name,
            StartedAt = s.StartedAt,
            EndedAt = s.EndedAt,
            TrackId = s.TrackId,
            TrackName = s.Track != null ? s.Track.Name : null,
            VehicleId = s.VehicleId,
            GpsSource = s.GpsSource,
            Visibility = s.Visibility,
            Voided = s.Voided,
            LapCount = s.Laps.Count,
            BestLapMs = s.Laps.Min(l => (int?)l.TimeMs),
            DragResultCount = s.DragResults.Count,
        };

    public Task<PagedResult<SessionSummaryResponse>> ListAsync(
        Guid ownerId,
        Guid? trackId,
        PageQuery page,
        CancellationToken ct)
    {
        var sessions = db.Sessions.AsNoTracking().Where(s => s.OwnerId == ownerId);

        if (trackId is not null)
        {
            sessions = sessions.Where(s => s.TrackId == trackId);
        }

        return sessions
            .OrderByDescending(s => s.StartedAt)
            .ThenBy(s => s.Id)
            .Select(ToSummary)
            .ToPagedResultAsync(page, ct);
    }

    public async Task<SessionResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var session = await db.Sessions
            .AsNoTracking()
            .Include(s => s.Track)
            .Include(s => s.Laps)
            .ThenInclude(l => l.Sectors)
            .Include(s => s.DragResults)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (session is null)
        {
            return null;
        }

        await authorizer.EnsureAsync<IOwnedResource>(session, ResourceOperations.Read);

        var trackVisibility = session.Track?.Visibility;
        var laps = session.Laps.OrderBy(l => l.LapNumber).ToList();
        var counting = laps.Where(l => LeaderboardRules.Counts(l, session, trackVisibility)).ToList();

        // A lap's rank comes from the cached track standings, so it always agrees with what
        // the public leaderboard shows.
        var ranksByLap = new Dictionary<Guid, int>();

        if (counting.Count > 0 && session.TrackId is { } trackId)
        {
            var standings = await leaderboards.GetStandingsAsync(trackId, ct);
            ranksByLap = standings.ToDictionary(s => s.LapId, s => s.Rank);
        }

        var countsForDragBoard = DragRules.Counts(session);
        var dragResults = new List<DragResultResponse>();

        foreach (var result in session.DragResults.OrderBy(r => r.Metric))
        {
            int? rank = null;

            if (countsForDragBoard
                && (await dragBoards.GetRanksAsync(result.Metric, ct)).TryGetValue(result.Id, out var standing))
            {
                rank = standing;
            }

            dragResults.Add(new DragResultResponse
            {
                Metric = result.Metric,
                TimeMs = result.TimeMs,
                TrapSpeedKmh = result.TrapSpeedKmh,
                CountsForLeaderboard = countsForDragBoard,
                LeaderboardRank = rank,
            });
        }

        return new SessionResponse
        {
            Id = session.Id,
            Name = session.Name,
            StartedAt = session.StartedAt,
            EndedAt = session.EndedAt,
            TrackId = session.TrackId,
            TrackName = session.Track?.Name,
            VehicleId = session.VehicleId,
            GpsSource = session.GpsSource,
            Visibility = session.Visibility,
            Voided = session.Voided,
            LapCount = laps.Count,
            BestLapMs = laps.Count > 0 ? laps.Min(l => l.TimeMs) : null,
            DragResultCount = dragResults.Count,
            DragResults = dragResults,
            Weather = ToWeather(session),
            AppVersion = session.AppVersion,
            Laps = laps
                .Select(l => new LapResponse
                {
                    LapNumber = l.LapNumber,
                    TimeMs = l.TimeMs,
                    SignalGap = l.SignalGap,
                    Sectors = l.Sectors
                        .OrderBy(s => s.SectorIndex)
                        .Select(s => new SectorSplitDto { SectorIndex = s.SectorIndex, SplitMs = s.SplitMs })
                        .ToList(),
                    CountsForLeaderboard = counting.Contains(l),
                    LeaderboardRank = ranksByLap.TryGetValue(l.Id, out var rank) ? rank : null,
                })
                .ToList(),
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
        };
    }

    public async Task<UpsertResult<SessionResponse>> UpsertAsync(
        Guid id,
        Guid callerId,
        UpsertSessionRequest request,
        CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        var created = session is null;

        if (session is not null)
        {
            // A PUT to someone else's id is refused, never treated as an overwrite.
            await authorizer.EnsureAsync<IOwnedResource>(session, ResourceOperations.Update);
        }

        // An admin editing a session must not move it into the admin's own garage.
        var ownerId = session?.OwnerId ?? callerId;

        // 404 rather than 403 for someone else's vehicle or private track: whether it
        // exists is not the caller's business.
        if (request.VehicleId is { } vehicleId
            && !await db.Vehicles.AnyAsync(v => v.Id == vehicleId && v.OwnerId == ownerId, ct))
        {
            throw new NotFoundException("Vehicle", vehicleId);
        }

        if (request.TrackId is { } trackId
            && !await db.Tracks.AnyAsync(
                t => t.Id == trackId && (t.OwnerId == ownerId || t.Visibility == TrackVisibility.Published),
                ct))
        {
            throw new NotFoundException("Track", trackId);
        }

        var previousTrackId = session?.TrackId;
        var hadDragResults = !created && await db.DragResults.AnyAsync(r => r.SessionId == id, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (session is null)
        {
            session = new Session { Id = id, OwnerId = ownerId };
            db.Sessions.Add(session);
        }
        else
        {
            // The upload is the whole session, so its laps replace the stored ones outright.
            // Deleted in SQL first: re-inserting the same lap numbers in one SaveChanges would
            // collide with the unique (session, lap number) index.
            await db.Laps.Where(l => l.SessionId == id).ExecuteDeleteAsync(ct);
            await db.DragResults.Where(r => r.SessionId == id).ExecuteDeleteAsync(ct);
        }

        session.Name = request.Name;
        session.StartedAt = request.StartedAt!.Value;
        session.EndedAt = request.EndedAt;
        session.VehicleId = request.VehicleId;
        session.TrackId = request.TrackId;
        session.GpsSource = request.GpsSource!.Value;
        session.Visibility = request.Visibility;
        session.Voided = request.Voided;
        session.AppVersion = request.AppVersion;
        ApplyWeather(session, request.Weather);

        if (!created)
        {
            // A re-upload that only adds laps leaves every column unchanged, so EF would skip
            // the row and UpdatedAt would stand still. The event board reads UpdatedAt to tell
            // who is still on track, so every upload counts as activity.
            db.Entry(session).State = EntityState.Modified;
        }

        db.Laps.AddRange(request.Laps.Select(l => new Lap
        {
            SessionId = id,
            LapNumber = l.LapNumber,
            TimeMs = l.TimeMs,
            SignalGap = l.SignalGap,
            Sectors = l.Sectors
                .Select(s => new LapSector { SectorIndex = s.SectorIndex, SplitMs = s.SplitMs })
                .ToList(),
        }));

        db.DragResults.AddRange(request.DragResults.Select(r => new DragResult
        {
            SessionId = id,
            Metric = r.Metric!.Value,
            TimeMs = r.TimeMs,
            TrapSpeedKmh = r.TrapSpeedKmh,
        }));

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Moving a session between tracks changes two standings, not one.
        await leaderboards.InvalidateAsync([previousTrackId, request.TrackId], ct);

        if (hadDragResults || request.DragResults.Count > 0)
        {
            await dragBoards.InvalidateAsync(ct);
        }

        return new UpsertResult<SessionResponse>(
            await GetAsync(id, ct) ?? throw new InvalidOperationException("Session vanished after upsert."),
            created);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Session", id);

        await authorizer.EnsureAsync<IOwnedResource>(session, ResourceOperations.Delete);

        var trackId = session.TrackId;
        var hadDragResults = await db.DragResults.AnyAsync(r => r.SessionId == id, ct);

        // Laps, their sectors and drag results go with it (database cascade).
        db.Sessions.Remove(session);
        await db.SaveChangesAsync(ct);

        await leaderboards.InvalidateAsync([trackId], ct);

        if (hadDragResults)
        {
            await dragBoards.InvalidateAsync(ct);
        }
    }

    private static void ApplyWeather(Session session, WeatherDto? weather)
    {
        session.WeatherTempC = weather?.TempC;
        session.WeatherHumidityPct = weather?.HumidityPct;
        session.WeatherPrecipitationMm = weather?.PrecipitationMm;
        session.WeatherCode = weather?.WeatherCode;
        session.WeatherWindKph = weather?.WindKph;
        session.WeatherWindDirDeg = weather?.WindDirDeg;
        session.WeatherPressureHpa = weather?.PressureHpa;
    }

    /// <summary>Null when nothing was recorded, rather than an object full of nulls.</summary>
    private static WeatherDto? ToWeather(Session s) =>
        s.WeatherTempC is null
        && s.WeatherHumidityPct is null
        && s.WeatherPrecipitationMm is null
        && s.WeatherCode is null
        && s.WeatherWindKph is null
        && s.WeatherWindDirDeg is null
        && s.WeatherPressureHpa is null
            ? null
            : new WeatherDto
            {
                TempC = s.WeatherTempC,
                HumidityPct = s.WeatherHumidityPct,
                PrecipitationMm = s.WeatherPrecipitationMm,
                WeatherCode = s.WeatherCode,
                WindKph = s.WeatherWindKph,
                WindDirDeg = s.WeatherWindDirDeg,
                PressureHpa = s.WeatherPressureHpa,
            };
}
