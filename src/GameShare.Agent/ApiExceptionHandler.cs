using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace GameShare.Agent;

/// <summary>
/// One place that turns failures into HTTP answers. The message is always the specific reason, never "something went wrong".
/// The mapping follows what each exception means in this codebase.
/// </summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _log;

    public ApiExceptionHandler(ILogger<ApiExceptionHandler> log) => _log = log;

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception ex, CancellationToken ct)
    {
        var (status, title) = ex switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ArgumentException or BadHttpRequestException or JsonException => (StatusCodes.Status400BadRequest, "Invalid request"),
            InvalidDataException => (StatusCodes.Status422UnprocessableEntity, "The data is invalid"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Not possible in the current state"),
            HttpRequestException => (StatusCodes.Status502BadGateway, "Another PC could not be reached"),
            IOException io when io.Message.StartsWith("Not enough free space", StringComparison.Ordinal) => (StatusCodes.Status507InsufficientStorage, "Not enough disk space"),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Cancelled"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
        };

        if (status >= 500) _log.LogError(ex, "{Method} {Path} failed", context.Request.Method, context.Request.Path);
        else _log.LogInformation("{Method} {Path} answered {Status}: {Reason}", context.Request.Method, context.Request.Path, status, ex.Message);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status >= 500 ? $"{ex.GetType().Name}: {ex.Message}" : ex.Message,
            Instance = context.Request.Path,
        }, ct).ConfigureAwait(false);
        return true;
    }
}
