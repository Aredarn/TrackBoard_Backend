using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrackBoard.Auth;
using TrackBoard.Common;
using TrackBoard.Dtos;
using TrackBoard.Services;

namespace TrackBoard.Controllers;

/// <summary>
/// Tracks and their leaderboards. Reading published tracks is public; writing needs sign-in.
/// No class-level [Authorize] here on purpose: it would combine with the optional policy on
/// the read actions and make them require a token after all.
/// </summary>
[ApiController]
[Route("api/v1/tracks")]
[Produces("application/json")]
public class TracksController(ITrackService tracks, ITrackLeaderboardService leaderboards)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.OptionalAuthentication)]
    [ProducesResponseType<PagedResult<TrackSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<TrackSummaryResponse>>> Search(
        [FromQuery] TrackSearchQuery query,
        [FromQuery] PageQuery page,
        CancellationToken ct)
    {
        var viewerId = User.TryGetUserId();

        // "My tracks" means nothing without knowing who "my" is.
        if (query.Mine && viewerId is null)
        {
            return Challenge();
        }

        return Ok(await tracks.SearchAsync(query, page, viewerId, ct));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.OptionalAuthentication)]
    [ProducesResponseType<TrackResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrackResponse>> GetById(Guid id, CancellationToken ct)
    {
        var track = await tracks.GetAsync(id, User.TryGetUserId(), ct);
        return track is null ? NotFound() : Ok(track);
    }

    /// <summary>Create or replace one of your tracks under an id the app generated.</summary>
    [HttpPut("{id:guid}")]
    [Authorize]
    [ProducesResponseType<TrackResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TrackResponse>> Upsert(
        Guid id,
        [FromBody] UpsertTrackRequest request,
        CancellationToken ct)
    {
        var (track, created) = await tracks.UpsertAsync(id, User.GetUserId(), request, ct);

        return created
            ? CreatedAtAction(nameof(GetById), new { id }, track)
            : Ok(track);
    }

    [HttpDelete("{id:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await tracks.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Each driver's best lap on a published track, fastest first, or with
    /// <paramref name="perCar"/> each car's. Public. Signing in adds the caller's quickest entry
    /// as <c>me</c>, even when it falls outside <paramref name="limit"/>.
    /// </summary>
    [HttpGet("{id:guid}/leaderboard")]
    [Authorize(Policy = AuthorizationPolicies.OptionalAuthentication)]
    [ProducesResponseType<TrackLeaderboardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrackLeaderboardResponse>> GetLeaderboard(
        Guid id,
        [FromQuery, Range(1, 100)] int limit = 50,
        [FromQuery] bool perCar = false,
        CancellationToken ct = default)
        => Ok(await leaderboards.GetAsync(id, limit, perCar, User.TryGetUserId(), ct));
}
