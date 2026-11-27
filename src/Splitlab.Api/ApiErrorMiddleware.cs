using Microsoft.AspNetCore.Mvc;
using Splitlab.Application;
using Splitlab.Domain;

namespace Splitlab.Api;

/// <summary>
/// Turns the application's expected refusals (not found, conflict, invalid input) into RFC 7807 problem responses.
/// <para>
/// It sits <em>inside</em> the framework's exception handler on purpose. That handler logs every exception it sees as an
/// unhandled error, so if these reached it, a routine 409 would look like a crash and real faults would drown among them.
/// Handled here, a refusal is a normal answer and is logged as information; anything unexpected passes through to the
/// framework handler, which logs it as an error and answers 500.
/// </para>
/// </summary>
internal sealed partial class ApiErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IProblemDetailsService _problems;
    private readonly ILogger<ApiErrorMiddleware> _logger;

    public ApiErrorMiddleware(RequestDelegate next, IProblemDetailsService problems, ILogger<ApiErrorMiddleware> logger)
    {
        _next = next;
        _problems = problems;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted && Map(exception) is var (status, title))
        {
            LogRefusal(context.Request.Method, context.Request.Path, status, exception.Message);

            context.Response.Clear();
            context.Response.StatusCode = status;
            await _problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = exception.Message },
            });
        }
    }

    /// <summary>The status and title for an expected refusal, or <c>null</c> for anything that is a genuine fault.</summary>
    private static (int Status, string Title)? Map(Exception exception) => exception switch
    {
        NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
        ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
        DomainException => (StatusCodes.Status400BadRequest, "Invalid request"),
        _ => null,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "{Method} {Path} refused with {Status}: {Reason}")]
    private partial void LogRefusal(string method, PathString path, int status, string reason);
}
