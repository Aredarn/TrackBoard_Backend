using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace TrackBoard.Common;

/// <summary>
/// Single exit point for unhandled exceptions, producing RFC 7807 <c>ProblemDetails</c>.
/// This is the .NET counterpart to Spring's <c>@ControllerAdvice</c>.
/// </summary>
/// <remarks>
/// Only exception types the domain defines get their message echoed back. Anything else
/// returns a fixed string, so an unexpected failure can never leak a stack trace, a SQL
/// fragment, or a connection string to the caller — the detail goes to the log instead.
/// </remarks>
public sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            NotFoundException notFound => (
                StatusCodes.Status404NotFound,
                "Resource not found",
                notFound.Message),

            ConflictException conflict => (
                StatusCodes.Status409Conflict,
                "Request conflicts with the current state",
                conflict.Message),

            // 401 and 403 are not interchangeable: 401 means we cannot identify the caller,
            // 403 means we can and they still may not do this.
            UnauthorizedException unauthorized => (
                StatusCodes.Status401Unauthorized,
                "Authentication failed",
                unauthorized.Message),

            ForbiddenException forbidden => (
                StatusCodes.Status403Forbidden,
                "Access denied",
                forbidden.Message),

            PayloadTooLargeException tooLarge => (
                StatusCodes.Status413PayloadTooLarge,
                "Too large",
                tooLarge.Message),

            ServiceUnavailableException unavailable => (
                StatusCodes.Status503ServiceUnavailable,
                "Not available on this server",
                unavailable.Message),

            // 499 is nginx's "client closed request"; ASP.NET Core has no constant for it.
            OperationCanceledException => (
                499,
                "Request cancelled",
                "The client closed the request before it completed."),

            _ => (
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred",
                "The request could not be completed. Contact support with the trace identifier."),
        };

        // A 503 here is a known, configured-off feature, not a crash worth a stack trace.
        if (status >= StatusCodes.Status500InternalServerError
            && status != StatusCodes.Status503ServiceUnavailable)
        {
            LogUnhandled(logger, httpContext.Request.Method, httpContext.Request.Path, exception);
        }
        else
        {
            LogHandled(
                logger,
                status,
                httpContext.Request.Method,
                httpContext.Request.Path,
                exception.Message);
        }

        httpContext.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}",
        };

        var code = exception switch
        {
            ConflictException conflict => conflict.Code,
            PayloadTooLargeException tooLarge => tooLarge.Code,
            _ => null,
        };

        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    // Path is taken as PathString rather than string: passing it as a string invokes an
    // implicit conversion at the call site, which allocates even when the level is disabled.
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on {Method} {Path}")]
    private static partial void LogUnhandled(
        ILogger logger,
        string method,
        PathString path,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Request failed with {Status} on {Method} {Path}: {Reason}")]
    private static partial void LogHandled(
        ILogger logger,
        int status,
        string method,
        PathString path,
        string reason);
}
