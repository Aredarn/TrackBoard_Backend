using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrackBoard.Auth;
using TrackBoard.Common;
using TrackBoard.Dtos;
using TrackBoard.Services;

namespace TrackBoard.Controllers;

/// <summary>
/// The signed-in driver's personal backup: an archive per recorded session and the app's
/// preferences. Private to the caller; nothing here can address another driver. Every
/// endpoint answers 503 on a deployment without backup storage.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/me/backup")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
public class BackupController(IBackupService backups) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<BackupSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackupSummaryResponse>> Summary(CancellationToken ct)
        => Ok(await backups.GetSummaryAsync(User.GetUserId(), ct));

    /// <summary>Newest session first.</summary>
    [HttpGet("sessions")]
    [ProducesResponseType<PagedResult<BackupSessionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<BackupSessionResponse>>> List(
        [FromQuery] BackupPageQuery page,
        CancellationToken ct)
        => Ok(await backups.ListAsync(User.GetUserId(), page, ct));

    /// <summary>
    /// Step one: a signed, single-use URL the app PUTs the archive to directly. Step two is
    /// <c>PUT /me/backup/sessions/{id}</c> with the returned path.
    /// </summary>
    [HttpPost("sessions/{id:guid}/upload")]
    [ProducesResponseType<BackupUploadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<BackupUploadResponse>> CreateUpload(
        Guid id,
        [FromBody] CreateBackupUploadRequest request,
        CancellationToken ct)
        => Ok(await backups.CreateUploadAsync(User.GetUserId(), id, request.SizeBytes!.Value, ct));

    /// <summary>
    /// Records the uploaded archive for the session, replacing any earlier one, whose object
    /// is then deleted. Safe to retry with the same path.
    /// </summary>
    [HttpPut("sessions/{id:guid}")]
    [ProducesResponseType<BackupSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<BackupSessionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<BackupSessionResponse>> Commit(
        Guid id,
        [FromBody] CommitBackupRequest request,
        CancellationToken ct)
    {
        var (backup, created) = await backups.CommitAsync(User.GetUserId(), id, request, ct);

        return created
            ? StatusCode(StatusCodes.Status201Created, backup)
            : Ok(backup);
    }

    /// <summary>A short-lived signed URL for the archive.</summary>
    [HttpGet("sessions/{id:guid}/download")]
    [ProducesResponseType<BackupDownloadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BackupDownloadResponse>> Download(Guid id, CancellationToken ct)
        => Ok(await backups.CreateDownloadAsync(User.GetUserId(), id, ct));

    /// <summary>Removes the archive and its record. 204 whether or not there was one.</summary>
    [HttpDelete("sessions/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await backups.DeleteAsync(User.GetUserId(), id, ct);
        return NoContent();
    }

    [HttpGet("settings")]
    [ProducesResponseType<BackupSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BackupSettingsResponse>> GetSettings(CancellationToken ct)
    {
        var settings = await backups.GetSettingsAsync(User.GetUserId(), ct);
        return settings is null ? NotFound() : Ok(settings);
    }

    /// <summary>Replaces the preferences document. At most 64 KB.</summary>
    [HttpPut("settings")]
    [ProducesResponseType<BackupSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<BackupSettingsResponse>> PutSettings(
        [FromBody] PutBackupSettingsRequest request,
        CancellationToken ct)
        => Ok(await backups.PutSettingsAsync(User.GetUserId(), request, ct));
}
