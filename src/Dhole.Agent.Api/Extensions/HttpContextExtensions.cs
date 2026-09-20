using System.Security.Claims;

namespace Dhole.Agent.Api.Extensions;

internal static class HttpContextExtensions
{
    public static Guid? GetCurrentUserId(this HttpContext context)
    {
        var raw=context.User.FindFirstValue(ClaimTypes.NameIdentifier)??context.User.FindFirstValue("sub");
        return Guid.TryParse(raw,out var id)?id:null;
    }
}
