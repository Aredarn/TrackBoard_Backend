using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TrackBoard.Auth;
using TrackBoard.Common;
using TrackBoard.Data;
using TrackBoard.Dtos;
using TrackBoard.Entities;
using TrackBoard.Mapping;
using TrackBoard.Storage;

namespace TrackBoard.Services;

public interface IVehicleService
{
    Task<PagedResult<VehicleResponse>> GetPagedAsync(PageQuery page, Guid? ownerId, CancellationToken ct);

    Task<VehicleResponse?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<VehicleResponse> CreateAsync(Guid ownerId, CreateVehicleRequest request, CancellationToken ct);

    /// <summary>
    /// Idempotent create-or-replace under a client-generated id. Replaces the old
    /// update-only PUT, which answered 404 for an id the app had not uploaded yet.
    /// </summary>
    Task<UpsertResult<VehicleResponse>> UpsertAsync(
        Guid id,
        Guid callerId,
        UpdateVehicleRequest request,
        CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

public class VehicleService(
    TrackBoardDbContext db,
    IResourceAuthorizer authorizer,
    ITrackLeaderboardService leaderboards,
    IDragLeaderboardService dragBoards,
    IMediaStorage media) : IVehicleService
{
    /// <summary>
    /// The response projection. Built per call because the photo URL depends on where the
    /// media bucket lives; EF sends <paramref name="mediaBase"/> as a query parameter.
    /// </summary>
    public static Expression<Func<Vehicle, VehicleResponse>> Projection(string? mediaBase) =>
        v => new VehicleResponse(
            v.Id,
            v.OwnerId,
            v.Owner.DisplayName,
            v.Manufacturer,
            v.Model,
            v.Year,
            v.EngineType,
            v.Horsepower,
            v.Torque,
            v.Weight,
            v.TopSpeed,
            v.Acceleration,
            v.Drivetrain,
            v.FuelType,
            v.TireType,
            v.FuelCapacity,
            v.Transmission,
            v.SuspensionType,
            v.CreatedAt,
            v.UpdatedAt,
            mediaBase == null || v.PhotoPath == null ? null : mediaBase + v.PhotoPath);

    // The base is fixed for the life of the process, so each compiled form is built once.
    private static readonly ConcurrentDictionary<string, Func<Vehicle, VehicleResponse>> Compiled = new();

    private Expression<Func<Vehicle, VehicleResponse>> ToResponse => Projection(media.PublicBaseUrl);

    /// <summary>Same shape as <see cref="ToResponse"/>, for entities already in memory.</summary>
    private VehicleResponse ToResponseInMemory(Vehicle vehicle) =>
        Compiled.GetOrAdd(media.PublicBaseUrl ?? string.Empty, b => Projection(b.Length == 0 ? null : b).Compile())(vehicle);

    public Task<PagedResult<VehicleResponse>> GetPagedAsync(
        PageQuery page,
        Guid? ownerId,
        CancellationToken ct)
    {
        var query = db.Vehicles.AsNoTracking();

        if (ownerId is not null)
        {
            query = query.Where(v => v.OwnerId == ownerId);
        }

        return query
            .OrderBy(v => v.Manufacturer)
            .ThenBy(v => v.Model)
            .ThenBy(v => v.Id)
            .Select(ToResponse)
            .ToPagedResultAsync(page, ct);
    }

    public async Task<VehicleResponse?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        // The entity is loaded rather than projected so the ownership handler has something
        // to authorize against; Owner is included because the response carries its name.
        var vehicle = await db.Vehicles
            .AsNoTracking()
            .Include(v => v.Owner)
            .FirstOrDefaultAsync(v => v.Id == id, ct);

        if (vehicle is null)
        {
            return null;
        }

        await authorizer.EnsureAsync(vehicle, ResourceOperations.Read);

        return ToResponseInMemory(vehicle);
    }

    public async Task<VehicleResponse> CreateAsync(
        Guid ownerId,
        CreateVehicleRequest request,
        CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == ownerId, ct))
        {
            throw new NotFoundException("User", ownerId);
        }

        var vehicle = VehicleMapper.ToEntity(request);
        vehicle.OwnerId = ownerId;

        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync(ct);

        return await GetByIdAsync(vehicle.Id, ct)
            ?? throw new InvalidOperationException("Vehicle vanished immediately after insert.");
    }

    public async Task<UpsertResult<VehicleResponse>> UpsertAsync(
        Guid id,
        Guid callerId,
        UpdateVehicleRequest request,
        CancellationToken ct)
    {
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct);
        var created = vehicle is null;

        if (vehicle is null)
        {
            vehicle = new Vehicle { Id = id, OwnerId = callerId };
            db.Vehicles.Add(vehicle);
        }
        else
        {
            // A PUT to someone else's id is refused, never treated as an overwrite.
            await authorizer.EnsureAsync(vehicle, ResourceOperations.Update);
        }

        VehicleMapper.ApplyTo(request, vehicle);
        await db.SaveChangesAsync(ct);

        if (!created)
        {
            // Leaderboard entries show the vehicle's make, model and year.
            var trackIds = await db.Sessions
                .Where(s => s.VehicleId == id && s.TrackId != null)
                .Select(s => s.TrackId)
                .Distinct()
                .ToListAsync(ct);

            await leaderboards.InvalidateAsync(trackIds, ct);

            // Drag boards show the same, and power and weight decide the car's class.
            if (await db.DragResults.AnyAsync(r => r.Session.VehicleId == id, ct))
            {
                await dragBoards.InvalidateAsync(ct);
            }
        }

        return new UpsertResult<VehicleResponse>(
            await GetByIdAsync(id, ct)
                ?? throw new InvalidOperationException("Vehicle vanished immediately after upsert."),
            created);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new NotFoundException("Vehicle", id);

        await authorizer.EnsureAsync(vehicle, ResourceOperations.Delete);

        if (await db.Results.AnyAsync(r => r.VehicleId == id, ct))
        {
            throw new ConflictException(
                "This vehicle has recorded results and cannot be deleted.");
        }

        if (await db.Sessions.AnyAsync(s => s.VehicleId == id, ct))
        {
            throw new ConflictException(
                "This vehicle is used by uploaded sessions and cannot be deleted.",
                "VehicleInUse");
        }

        db.Vehicles.Remove(vehicle);
        await db.SaveChangesAsync(ct);
    }
}
