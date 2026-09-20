using CustomCodeFramework.Api.Responses;
using CustomCodeFramework.Core.Results;

namespace Dhole.Agent.Api.Endpoints;

internal static class EndpointResults
{
    public static IResult FromResult<T>(Result<T> result, HttpContext httpContext)
        => result.IsSuccess
            ? Results.Ok(ApiResponse<T>.Ok(result.Value))
            : Results.BadRequest(ApiErrorResponse.Create(result.Error.Code,result.Error.Message,httpContext.TraceIdentifier));

    public static IResult FromResult(Result result, HttpContext httpContext)
        => result.IsSuccess
            ? Results.NoContent()
            : Results.BadRequest(ApiErrorResponse.Create(result.Error.Code,result.Error.Message,httpContext.TraceIdentifier));
}
