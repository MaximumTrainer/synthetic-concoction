using System.Security.Claims;

namespace Fabricate.Api.Routes;

internal static class HttpContextExtensions
{
    internal static Guid GetUserId(this HttpContext context)
    {
        var value = context.User.FindFirst("sub")?.Value
                    ?? context.User.FindFirst("uid")?.Value
                    ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}
