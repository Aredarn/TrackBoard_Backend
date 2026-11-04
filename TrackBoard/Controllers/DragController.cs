using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrackBoard.Auth;
using TrackBoard.Dtos;
using TrackBoard.Entities;
using TrackBoard.Services;

namespace TrackBoard.Controllers;

/// <summary>The drag boards. Results arrive with their session; this only reads them.</summary>
[ApiController]
[Route("api/v1/drag")]
[Produces("application/json")]
public class DragController(IDragLeaderboardService leaderboards) : ControllerBase
{
    /// <summary>
    /// Each car's best time on a metric, fastest first, optionally within one power-to-weight
    /// class. Public. Signing in adds the caller's quickest entry as <c>me</c>, even when it falls
    /// outside <paramref name="limit"/>.
    /// </summary>
    [HttpGet("leaderboard")]
    [Authorize(Policy = AuthorizationPolicies.OptionalAuthentication)]
    [ProducesResponseType<DragLeaderboardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DragLeaderboardResponse>> GetLeaderboard(
        [FromQuery, EnumDataType(typeof(DragMetric))] DragMetric metric = DragMetric.ZeroToHundred,
        [FromQuery(Name = "class"), EnumDataType(typeof(DragClass))] DragClass? carClass = null,
        [FromQuery, Range(1, 100)] int limit = 50,
        CancellationToken ct = default)
        => Ok(await leaderboards.GetAsync(metric, carClass, limit, User.TryGetUserId(), ct));
}
