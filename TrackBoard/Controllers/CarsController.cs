using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrackBoard.Auth;
using TrackBoard.Dtos;
using TrackBoard.Services;

namespace TrackBoard.Controllers;

/// <summary>
/// Public car pages, linked from track and drag board entries. Anyone can read them; a token is
/// optional and changes nothing about the response. The owner's own garage stays at
/// <c>/api/v1/vehicles</c>.
/// </summary>
[ApiController]
[Route("api/v1/cars")]
[Produces("application/json")]
public class CarsController(ICarService cars) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.OptionalAuthentication)]
    [ProducesResponseType<PublicCarResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicCarResponse>> GetById(Guid id, CancellationToken ct)
        => Ok(await cars.GetAsync(id, ct));
}
