using Microsoft.EntityFrameworkCore;
using TrackBoard.Common;
using TrackBoard.Data;
using TrackBoard.Dtos;
using TrackBoard.Entities;
using TrackBoard.Storage;

namespace TrackBoard.Services;

public interface ICarService
{
    /// <summary>
    /// A car's public page. Throws <see cref="NotFoundException"/> for an unknown car, a deleted
    /// owner, or a car with no place on any public board.
    /// </summary>
    Task<PublicCarResponse> GetAsync(Guid carId, CancellationToken ct);
}

public class CarService(
    TrackBoardDbContext db,
    IMediaStorage media,
    ITrackLeaderboardService trackBoards,
    IDragLeaderboardService dragBoards) : ICarService
{
    public async Task<PublicCarResponse> GetAsync(Guid carId, CancellationToken ct)
    {
        var car = await db.Vehicles
            .AsNoTracking()
            .Where(v => v.Id == carId && v.Owner.DeletedAt == null)
            .Select(v => new { Vehicle = v, OwnerName = v.Owner.DisplayName })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Car", carId);

        var trackBests = await TrackBestsAsync(carId, ct);
        var dragBests = await DragBestsAsync(carId, ct);

        // Everything shown is already public on some board; a car on none has no page.
        if (trackBests.Count == 0 && dragBests.Count == 0)
        {
            throw new NotFoundException("Car", carId);
        }

        var v = car.Vehicle;

        return new PublicCarResponse(
            v.Id,
            v.OwnerId,
            car.OwnerName,
            v.Manufacturer,
            v.Model,
            v.Year,
            media.PublicUrl(v.PhotoPath),
            v.EngineType,
            v.Horsepower,
            v.Torque,
            v.Weight,
            v.TopSpeed,
            v.Acceleration,
            v.Drivetrain,
            v.FuelType,
            v.TireType,
            v.Transmission,
            v.SuspensionType,
            DragRules.ClassOf(v.Horsepower, v.Weight),
            trackBests,
            dragBests);
    }

    private async Task<List<CarTrackBestResponse>> TrackBestsAsync(Guid carId, CancellationToken ct)
    {
        var tracks = await db.Laps
            .AsNoTracking()
            .Where(LeaderboardRules.CountsForLeaderboard)
            .Where(l => l.Session.VehicleId == carId)
            .Select(l => new { l.Session.Track!.Id, l.Session.Track.Name, l.Session.Track.Country, l.Session.Track.Type })
            .Distinct()
            .ToListAsync(ct);

        var bests = new List<CarTrackBestResponse>();

        foreach (var track in tracks)
        {
            // The cached per-car standings the board is served from, so the two always agree.
            var field = await trackBoards.GetStandingsAsync(track.Id, ct);

            if (field.FirstOrDefault(s => s.Vehicle?.Id == carId) is not { } mine)
            {
                continue;
            }

            bests.Add(new CarTrackBestResponse(
                track.Id,
                track.Name,
                track.Country,
                track.Type,
                mine.Rank,
                field.Count,
                mine.LapTimeMs,
                mine.GapToLeaderMs,
                mine.TopSpeedKmh,
                mine.SetAt,
                mine.GpsSource));
        }

        return bests
            .OrderBy(b => b.Rank)
            .ThenBy(b => b.TrackName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<List<CarDragBestResponse>> DragBestsAsync(Guid carId, CancellationToken ct)
    {
        var metrics = await db.DragResults
            .AsNoTracking()
            .Where(DragRules.CountsForBoard)
            .Where(r => r.Session.VehicleId == carId)
            .Select(r => r.Metric)
            .Distinct()
            .ToListAsync(ct);

        var bests = new List<CarDragBestResponse>();

        foreach (var metric in metrics.Order())
        {
            var field = await dragBoards.GetStandingsAsync(metric, ct);
            var index = Array.FindIndex(field, s => s.Vehicle.Id == carId);

            if (index < 0)
            {
                continue;
            }

            var mine = field[index];

            bests.Add(new CarDragBestResponse(
                metric,
                index + 1,
                field.Length,
                mine.TimeMs,
                mine.TimeMs - field[0].TimeMs,
                mine.TrapSpeedKmh,
                mine.SetAt,
                mine.GpsSource));
        }

        return bests;
    }
}
