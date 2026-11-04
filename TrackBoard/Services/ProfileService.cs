using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TrackBoard.Auth;
using TrackBoard.Common;
using TrackBoard.Data;
using TrackBoard.Dtos;
using TrackBoard.Entities;
using TrackBoard.Storage;

namespace TrackBoard.Services;

public interface IProfileService
{
    Task<ProfileResponse> GetAsync(Guid userId, CancellationToken ct);

    Task<ProfileResponse> UpdateAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct);

    Task<ProfileStatsResponse> GetStatsAsync(Guid userId, CancellationToken ct);

    Task<AccountExportResponse> ExportAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Removes the account and everything it owns, once <paramref name="password"/> is
    /// confirmed. See the implementation for the one case where an anonymised row is kept
    /// instead.
    /// </summary>
    Task DeleteAccountAsync(Guid userId, string password, CancellationToken ct);

    Task<UploadTargetResponse> CreateUploadAsync(Guid userId, CreateUploadRequest request, CancellationToken ct);

    Task<ProfileResponse> SetAvatarAsync(Guid userId, string? path, CancellationToken ct);

    Task<VehicleResponse> SetVehiclePhotoAsync(Guid userId, Guid vehicleId, string? path, CancellationToken ct);
}

public class ProfileService(
    TrackBoardDbContext db,
    IMediaStorage media,
    IResourceAuthorizer authorizer,
    IVehicleService vehicles,
    ITrackLeaderboardService leaderboards,
    IDragLeaderboardService dragBoards,
    IPasswordHasher<User> passwordHasher,
    ILoginAttemptTracker lockout,
    TimeProvider timeProvider) : IProfileService
{
    public async Task<ProfileResponse> GetAsync(Guid userId, CancellationToken ct) =>
        ToResponse(await LoadLiveUserAsync(userId, tracking: false, ct));

    public async Task<ProfileResponse> UpdateAsync(
        Guid userId,
        UpdateProfileRequest request,
        CancellationToken ct)
    {
        var user = await LoadLiveUserAsync(userId, tracking: true, ct);
        var renamed = false;

        if (request.DisplayName is { } name)
        {
            var trimmed = name.Trim();

            if (trimmed.Length < 2)
            {
                throw new ConflictException("A display name needs at least two visible characters.");
            }

            renamed = trimmed != user.DisplayName;
            user.DisplayName = trimmed;
        }

        if (request.Bio is not null)
        {
            user.Bio = Blank(request.Bio);
        }

        if (request.Country is not null)
        {
            user.Country = Blank(request.Country);
        }

        await db.SaveChangesAsync(ct);

        if (renamed)
        {
            // Every leaderboard the driver stands on shows their name.
            await leaderboards.InvalidateAsync(await TrackIdsDrivenAsync(userId, ct), ct);
            await dragBoards.InvalidateAsync(ct);
        }

        return ToResponse(user);
    }

    public async Task<ProfileStatsResponse> GetStatsAsync(Guid userId, CancellationToken ct)
    {
        await LoadLiveUserAsync(userId, tracking: false, ct);

        // Aggregated in memory from a narrow projection rather than in SQL. A driver has
        // hundreds of sessions and low thousands of laps at most, and this keeps the maths
        // identical on PostgreSQL and on the SQLite the tests run against.
        var sessions = await db.Sessions
            .AsNoTracking()
            .Where(s => s.OwnerId == userId && !s.Voided)
            .Select(s => new { s.Id, s.StartedAt, s.TrackId, s.VehicleId })
            .ToListAsync(ct);

        var laps = await db.Laps
            .AsNoTracking()
            .Where(l => l.Session.OwnerId == userId && !l.Session.Voided && !l.SignalGap)
            .Select(l => new { l.TimeMs, l.Session.TrackId, l.Session.VehicleId, l.Session.StartedAt })
            .ToListAsync(ct);

        var trackIds = sessions.Select(s => s.TrackId).OfType<Guid>().Distinct().ToList();

        var tracks = await db.Tracks
            .AsNoTracking()
            .Where(t => trackIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.Country, t.Type, t.LengthMeters, t.Visibility })
            .ToDictionaryAsync(t => t.Id, ct);

        var baseUrl = media.PublicBaseUrl;
        var garage = await db.Vehicles
            .AsNoTracking()
            .Where(v => v.OwnerId == userId)
            .Select(v => new ProfileVehicleRef(
                v.Id,
                v.Manufacturer,
                v.Model,
                v.Year,
                baseUrl == null || v.PhotoPath == null ? null : baseUrl + v.PhotoPath))
            .ToDictionaryAsync(v => v.Id, ct);

        var mainVehicle = sessions
            .Where(s => s.VehicleId is { } id && garage.ContainsKey(id))
            .GroupBy(s => s.VehicleId!.Value)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(s => s.StartedAt))
            .Select(g => garage[g.Key])
            .FirstOrDefault();

        var lengths = laps
            .Select(l => l.TrackId is { } id && tracks.TryGetValue(id, out var t) ? t.LengthMeters : null)
            .OfType<double>()
            .ToList();

        var personalBests = new List<PersonalBestResponse>();

        foreach (var group in laps.Where(l => l.TrackId is not null).GroupBy(l => l.TrackId!.Value))
        {
            if (!tracks.TryGetValue(group.Key, out var track))
            {
                continue;
            }

            // A tie with yourself goes to the lap set first, as it does on the leaderboard.
            var best = group.OrderBy(l => l.TimeMs).ThenBy(l => l.StartedAt).First();

            TrackStanding? standing = null;
            int? fieldSize = null;

            if (track.Visibility == TrackVisibility.Published)
            {
                var standings = await leaderboards.GetStandingsAsync(track.Id, ct);
                standing = standings.FirstOrDefault(s => s.UserId == userId);
                fieldSize = standing is null ? null : standings.Count;
            }

            personalBests.Add(new PersonalBestResponse(
                track.Id,
                track.Name,
                track.Country,
                track.Type,
                best.TimeMs,
                best.StartedAt,
                best.VehicleId is { } vid ? garage.GetValueOrDefault(vid) : null,
                group.Count(),
                standing?.Rank,
                fieldSize,
                standing?.LapTimeMs));
        }

        var ordered = personalBests
            .OrderBy(pb => pb.Rank is null)
            .ThenBy(pb => pb.Rank)
            .ThenBy(pb => pb.TrackName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new ProfileStatsResponse(
            sessions.Count,
            laps.Count,
            trackIds.Count,
            garage.Count,
            lengths.Count == 0 ? null : Math.Round(lengths.Sum() / 1000.0, 1),
            sessions.Count == 0 ? null : sessions.Min(s => s.StartedAt),
            sessions.Count == 0 ? null : sessions.Max(s => s.StartedAt),
            mainVehicle,
            ordered);
    }

    public async Task<AccountExportResponse> ExportAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadLiveUserAsync(userId, tracking: false, ct);

        var vehicleList = await db.Vehicles
            .AsNoTracking()
            .Where(v => v.OwnerId == userId)
            .OrderBy(v => v.Manufacturer)
            .ThenBy(v => v.Model)
            .Select(VehicleService.Projection(media.PublicBaseUrl))
            .ToListAsync(ct);

        var tracks = await db.Tracks
            .AsNoTracking()
            .Where(t => t.OwnerId == userId)
            .Include(t => t.Points)
            .AsSplitQuery()
            .ToListAsync(ct);

        var sessions = await db.Sessions
            .AsNoTracking()
            .Where(s => s.OwnerId == userId)
            .Include(s => s.Laps)
            .ThenInclude(l => l.Sectors)
            .Include(s => s.DragResults)
            .AsSplitQuery()
            .ToListAsync(ct);

        var events = await db.Events
            .AsNoTracking()
            .Where(e => e.HostId == userId || e.Entries.Any(x => x.UserId == userId))
            .OrderBy(e => e.StartsAt)
            .Select(e => new ExportEvent(
                e.Id,
                e.Name,
                e.TrackId,
                e.StartsAt,
                e.EndsAt,
                e.HostId == userId,
                e.Entries.Where(x => x.UserId == userId && x.Group != null).Select(x => x.Group!.Name).FirstOrDefault()))
            .ToListAsync(ct);

        return new AccountExportResponse(
            timeProvider.GetUtcNow(),
            ToResponse(user),
            vehicleList,
            tracks
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new ExportTrack(
                    t.Id,
                    t.Name,
                    t.Country,
                    t.Type,
                    t.LengthMeters,
                    t.Visibility,
                    t.Points
                        .OrderBy(p => p.Seq)
                        .Select(p => new ExportTrackPoint(
                            p.Seq, p.Latitude, p.Longitude, p.Altitude, p.IsStartPoint, p.IsSectorPoint, p.SectorIndex))
                        .ToList()))
                .ToList(),
            sessions
                .OrderBy(s => s.StartedAt)
                .Select(s => new ExportSession(
                    s.Id,
                    s.Name,
                    s.StartedAt,
                    s.EndedAt,
                    s.VehicleId,
                    s.TrackId,
                    s.GpsSource,
                    s.Visibility,
                    s.Voided,
                    s.AppVersion,
                    s.Laps
                        .OrderBy(l => l.LapNumber)
                        .Select(l => new ExportLap(
                            l.LapNumber,
                            l.TimeMs,
                            l.SignalGap,
                            l.Sectors
                                .OrderBy(x => x.SectorIndex)
                                .Select(x => new SectorSplitDto { SectorIndex = x.SectorIndex, SplitMs = x.SplitMs })
                                .ToList()))
                        .ToList(),
                    s.DragResults
                        .OrderBy(r => r.Metric)
                        .Select(r => new ExportDragResult(r.Metric, r.TimeMs, r.TrapSpeedKmh))
                        .ToList()))
                .ToList(),
            events);
    }

    /// <remarks>
    /// Sessions, laps, private tracks, vehicles, photos and refresh tokens are deleted
    /// outright, and the driver disappears from every leaderboard. The user row itself is
    /// deleted too, unless something other drivers depend on still points at it: a published
    /// track that others have timed on, or a result in a series. Deleting those would rewrite
    /// other people's history, so in that case the row is kept but anonymised — no email, no
    /// name, no password that can ever verify — and the address becomes free to register again.
    /// </remarks>
    public async Task DeleteAccountAsync(Guid userId, string password, CancellationToken ct)
    {
        var user = await LoadLiveUserAsync(userId, tracking: true, ct);

        ConfirmPassword(user, password);

        var mediaPaths = new List<string?> { user.AvatarPath };
        mediaPaths.AddRange(await db.Vehicles
            .Where(v => v.OwnerId == userId)
            .Select(v => v.PhotoPath)
            .ToListAsync(ct));

        var touchedTracks = await TrackIdsDrivenAsync(userId, ct);

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Children first: nothing here relies on the database cascading, so the order is
            // the same whatever the provider enforces.

            // The driver leaves every event they joined, and events they host go entirely:
            // an event is the host's, and nobody else can run it.
            var hosted = db.Events.Where(e => e.HostId == userId).Select(e => e.Id);
            await db.EventEntries
                .Where(x => x.UserId == userId || hosted.Contains(x.EventId))
                .ExecuteDeleteAsync(ct);
            await db.EventGroups.Where(g => hosted.Contains(g.EventId)).ExecuteDeleteAsync(ct);
            await db.Events.Where(e => e.HostId == userId).ExecuteDeleteAsync(ct);

            var sessionIds = db.Sessions.Where(s => s.OwnerId == userId).Select(s => s.Id);
            var lapIds = db.Laps.Where(l => sessionIds.Contains(l.SessionId)).Select(l => l.Id);

            await db.LapSectors.Where(x => lapIds.Contains(x.LapId)).ExecuteDeleteAsync(ct);
            await db.Laps.Where(l => sessionIds.Contains(l.SessionId)).ExecuteDeleteAsync(ct);
            await db.DragResults.Where(r => sessionIds.Contains(r.SessionId)).ExecuteDeleteAsync(ct);
            await db.Sessions.Where(s => s.OwnerId == userId).ExecuteDeleteAsync(ct);

            // With the driver's own sessions gone, any session left on one of their tracks
            // belongs to someone else — those tracks stay.
            var deletableTracks = db.Tracks
                .Where(t => t.OwnerId == userId
                    && !db.Sessions.Any(s => s.TrackId == t.Id)
                    && !db.Events.Any(e => e.TrackId == t.Id))
                .Select(t => t.Id);

            await db.TrackPoints.Where(p => deletableTracks.Contains(p.TrackId)).ExecuteDeleteAsync(ct);
            await db.Tracks.Where(t => deletableTracks.Contains(t.Id)).ExecuteDeleteAsync(ct);

            await db.Vehicles
                .Where(v => v.OwnerId == userId && !db.Results.Any(r => r.VehicleId == v.Id))
                .ExecuteDeleteAsync(ct);

            await db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);

            // A track another host's event runs on stays, like one other drivers have timed on.
            var stillReferenced =
                await db.Tracks.AnyAsync(t => t.OwnerId == userId, ct)
                || await db.Results.AnyAsync(r => r.UserId == userId, ct)
                || await db.Vehicles.AnyAsync(v => v.OwnerId == userId, ct);

            if (stillReferenced)
            {
                user.Email = $"deleted-{userId:N}@deleted.invalid";
                user.DisplayName = "Deleted driver";
                // Not a hash PasswordHasher can ever verify against.
                user.PasswordHash = "!";
                user.Bio = null;
                user.Country = null;
                user.AvatarPath = null;
                user.DeletedAt = timeProvider.GetUtcNow();
            }
            else
            {
                db.Users.Remove(user);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await leaderboards.InvalidateAsync(touchedTracks, ct);
        await dragBoards.InvalidateAsync(ct);
        await media.DeleteAsync(mediaPaths, ct);
    }

    public async Task<UploadTargetResponse> CreateUploadAsync(
        Guid userId,
        CreateUploadRequest request,
        CancellationToken ct)
    {
        await LoadLiveUserAsync(userId, tracking: false, ct);

        if (!media.IsConfigured)
        {
            throw new ServiceUnavailableException("Photo storage is not configured on this server.");
        }

        var extension = request.ContentType == "image/webp" ? "webp" : "jpg";

        // A fresh name per upload: public URLs are cached by phones and CDNs, so replacing a
        // photo under the same path would keep showing the old one for hours.
        var path = request.Kind switch
        {
            MediaKind.Avatar => $"{AvatarPrefix(userId)}{Guid.NewGuid():N}.{extension}",
            MediaKind.VehiclePhoto => $"{await VehiclePrefixAsync(userId, request.VehicleId, ct)}{Guid.NewGuid():N}.{extension}",
            _ => throw new ConflictException("Unknown upload kind."),
        };

        var signed = await media.CreateSignedUploadAsync(path, ct);
        return new UploadTargetResponse(signed.UploadUrl, signed.Path, signed.PublicUrl);
    }

    public async Task<ProfileResponse> SetAvatarAsync(Guid userId, string? path, CancellationToken ct)
    {
        var user = await LoadLiveUserAsync(userId, tracking: true, ct);

        if (path is not null)
        {
            EnsureUnder(path, AvatarPrefix(userId));
        }

        var previous = user.AvatarPath;
        user.AvatarPath = path;
        await db.SaveChangesAsync(ct);

        if (previous != path)
        {
            await media.DeleteAsync([previous], ct);
        }

        return ToResponse(user);
    }

    public async Task<VehicleResponse> SetVehiclePhotoAsync(
        Guid userId,
        Guid vehicleId,
        string? path,
        CancellationToken ct)
    {
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == vehicleId, ct)
            ?? throw new NotFoundException("Vehicle", vehicleId);

        await authorizer.EnsureAsync(vehicle, ResourceOperations.Update);

        if (path is not null)
        {
            EnsureUnder(path, VehiclePrefix(userId, vehicleId));
        }

        var previous = vehicle.PhotoPath;
        vehicle.PhotoPath = path;
        await db.SaveChangesAsync(ct);

        if (previous != path)
        {
            await media.DeleteAsync([previous], ct);
        }

        return await vehicles.GetByIdAsync(vehicleId, ct)
            ?? throw new NotFoundException("Vehicle", vehicleId);
    }

    private static string AvatarPrefix(Guid userId) => $"users/{userId:N}/avatar/";

    private static string VehiclePrefix(Guid userId, Guid vehicleId) => $"users/{userId:N}/vehicles/{vehicleId:N}/";

    private async Task<string> VehiclePrefixAsync(Guid userId, Guid? vehicleId, CancellationToken ct)
    {
        if (vehicleId is not { } id)
        {
            throw new ConflictException("A vehicle photo upload needs a vehicleId.");
        }

        var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new NotFoundException("Vehicle", id);

        await authorizer.EnsureAsync(vehicle, ResourceOperations.Update);
        return VehiclePrefix(userId, id);
    }

    /// <summary>
    /// A path can only be attached where this server would have signed it. Without this, a
    /// driver could point their avatar at someone else's object and have it deleted the next
    /// time they change photos.
    /// </summary>
    private static void EnsureUnder(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Length == prefix.Length)
        {
            throw new ForbiddenException("That photo was not uploaded for this profile.");
        }
    }

    private Task<List<Guid?>> TrackIdsDrivenAsync(Guid userId, CancellationToken ct) =>
        db.Sessions
            .Where(s => s.OwnerId == userId && s.TrackId != null)
            .Select(s => s.TrackId)
            .Distinct()
            .ToListAsync(ct);

    /// <summary>
    /// A deleted account's access token stays cryptographically valid until it expires, so
    /// every profile call re-checks the row rather than trusting the token alone.
    /// </summary>
    private async Task<User> LoadLiveUserAsync(Guid userId, bool tracking, CancellationToken ct)
    {
        var query = tracking ? db.Users : db.Users.AsNoTracking();
        var user = await query.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || user.DeletedAt is not null)
        {
            throw new UnauthorizedException("This account no longer exists.");
        }

        return user;
    }

    /// <summary>
    /// Deleting an account is irreversible, so a stolen access token (good for an hour) must
    /// not be enough to do it. Wrong guesses count against the same budget as sign-in:
    /// otherwise this endpoint would be a way round the lockout.
    /// </summary>
    /// <remarks>
    /// 403 rather than 401 for a wrong password: a client treats 401 as "your session has
    /// expired" and signs the driver out, which is not what a typo should do.
    /// </remarks>
    private void ConfirmPassword(User user, string password)
    {
        if (lockout.GetRetryAfter(user.Email) is { } retryAfter)
        {
            throw new TooManyRequestsException(retryAfter);
        }

        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password)
            == PasswordVerificationResult.Failed)
        {
            lockout.RecordFailure(user.Email);
            throw new ForbiddenException("The password is incorrect.");
        }

        lockout.Reset(user.Email);
    }

    private ProfileResponse ToResponse(User user) =>
        new(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Role,
            user.Bio,
            user.Country,
            media.PublicUrl(user.AvatarPath),
            user.CreatedAt);

    private static string? Blank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
