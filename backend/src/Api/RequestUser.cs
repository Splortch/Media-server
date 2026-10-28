using System.Security.Claims;
using MediaServer.Core;

public sealed class RequestUser(IHttpContextAccessor accessor) : IUserContext
{
    // No HTTP context is a trusted system operation (scanner/migrations/offline tools).
    // An anonymous HTTP context is user 0, not a system operation.
    public int? UserId =>
        accessor.HttpContext is null ? null
        : int.TryParse(
            accessor.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier),
            out var id
        )
            ? id
        : 0;
}
