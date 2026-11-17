namespace TrackBoard.Common;

/// <summary>Thrown by services when a referenced entity does not exist. Maps to 404.</summary>
public class NotFoundException(string resource, object key)
    : Exception($"{resource} '{key}' was not found.")
{
    public string Resource { get; } = resource;
}

/// <summary>Thrown when a request is well-formed but violates a domain rule. Maps to 409.</summary>
/// <param name="message">Human-readable explanation.</param>
/// <param name="code">
/// Optional stable identifier, e.g. <c>TrackGeometryLocked</c>, returned as the problem's
/// <c>code</c> so a client can branch on it without parsing the message.
/// </param>
public class ConflictException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// Thrown when credentials or a refresh token are missing, wrong, or expired. Maps to 401.
/// The message is deliberately vague at every throw site so it cannot be used to enumerate
/// which accounts exist.
/// </summary>
public class UnauthorizedException(string message) : Exception(message);

/// <summary>
/// Thrown when an authenticated caller is not permitted to touch this particular resource.
/// Maps to 403 — distinct from 401, which means "we do not know who you are".
/// </summary>
public class ForbiddenException(string message) : Exception(message);

/// <summary>
/// Thrown when an optional dependency the request needs is not configured on this
/// deployment, e.g. photo storage. Maps to 503 so a client can say "not available here"
/// rather than "something broke".
/// </summary>
public class ServiceUnavailableException(string message) : Exception(message);

/// <summary>
/// Thrown when a request is well-formed but too big for this server's limits. Maps to 413.
/// </summary>
/// <param name="message">Human-readable explanation.</param>
/// <param name="code">Optional stable identifier, returned as the problem's <c>code</c>.</param>
public class PayloadTooLargeException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}
