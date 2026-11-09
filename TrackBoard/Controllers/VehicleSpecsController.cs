using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrackBoard.Dtos;
using TrackBoard.VehicleSpecs;

namespace TrackBoard.Controllers;

/// <summary>
/// Make, model and year lookup for the app's "add a car" form, with spec sheets to pre-fill it.
/// Signed-in only: every call spends the server's catalogue quota. Answers 503 when the server
/// has no catalogue key, which the app takes as "type it in yourself".
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/vehicle-specs")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
public class VehicleSpecsController(IVehicleSpecsClient catalogue) : ControllerBase
{
    [HttpGet("cars/makes")]
    [ProducesResponseType<IReadOnlyList<VehicleCatalogOption>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<VehicleCatalogOption>>> GetCarMakes(
        CancellationToken ct)
        => Ok(await catalogue.GetCarMakesAsync(ct));

    [HttpGet("cars/models")]
    [ProducesResponseType<IReadOnlyList<VehicleCatalogOption>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<VehicleCatalogOption>>> GetCarModels(
        [FromQuery, Required, StringLength(60, MinimumLength = 1)] string make,
        CancellationToken ct)
        => Ok(await catalogue.GetCarModelsAsync(make, ct));

    [HttpGet("cars/years")]
    [ProducesResponseType<IReadOnlyList<int>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<int>>> GetCarYears(
        [FromQuery, Required, StringLength(60, MinimumLength = 1)] string make,
        [FromQuery, Required, StringLength(80, MinimumLength = 1)] string model,
        CancellationToken ct)
        => Ok(await catalogue.GetCarYearsAsync(make, model, ct));

    /// <summary>
    /// The spec sheets for one make, model and year. On a free-tier key each sheet is one fuel's
    /// worth of agreed facts and is marked <c>partial</c>.
    /// </summary>
    [HttpGet("cars/variants")]
    [ProducesResponseType<IReadOnlyList<VehicleSpecVariant>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<VehicleSpecVariant>>> GetCarVariants(
        [FromQuery, Required, StringLength(60, MinimumLength = 1)] string make,
        [FromQuery, Required, StringLength(80, MinimumLength = 1)] string model,
        [FromQuery, Range(1900, 2100)] int year,
        CancellationToken ct)
        => Ok(await catalogue.GetCarVariantsAsync(make, model, year, ct));

    /// <summary>Motorcycles by make and (partial) model name, up to 30 sheets, one per model year.</summary>
    [HttpGet("motorcycles")]
    [ProducesResponseType<IReadOnlyList<VehicleSpecVariant>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<VehicleSpecVariant>>> SearchMotorcycles(
        [FromQuery, Required, StringLength(60, MinimumLength = 1)] string make,
        [FromQuery, Required, StringLength(80, MinimumLength = 1)] string model,
        [FromQuery, Range(1900, 2100)] int? year,
        CancellationToken ct)
        => Ok(await catalogue.SearchMotorcyclesAsync(make, model, year, ct));
}
