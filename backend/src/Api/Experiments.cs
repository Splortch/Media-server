using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

public record GuestPolicy(bool Enabled, int[] Libraries);

public record GuestAvailability(bool Enabled);

public static class Experiments
{
    private const string GuestKey = "experimental-guest-access";

    public static Task<bool> GuestsEnabled(MediaDb db) =>
        db.FeaturePolicies.AnyAsync(x => x.Key == GuestKey && x.Enabled);

    public static void MapExperiments(this WebApplication app, SemaphoreSlim credentials)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var root = app.MapGroup(prefix);
            if (prefix.EndsWith("v1", StringComparison.Ordinal))
                root.WithGroupName("client-v1");
            root.MapGet(
                "/auth/guest",
                async (MediaDb db) => new GuestAvailability(await GuestsEnabled(db))
            );
            root.MapGet(
                    "/experiments/guests",
                    async (MediaDb db) =>
                    {
                        var guest = await db.Users.SingleOrDefaultAsync(x => x.Role == "Guest");
                        var libraries = guest is null
                            ? []
                            : await db
                                .Grants.Where(x => x.UserId == guest.Id)
                                .Select(x => x.LibraryId)
                                .ToArrayAsync();
                        return new GuestPolicy(await GuestsEnabled(db), libraries);
                    }
                )
                .RequireAuthorization("admin");
            root.MapPut(
                    "/experiments/guests",
                    async (GuestPolicy input, MediaDb db) =>
                    {
                        if (
                            input.Libraries is null
                            || input.Libraries.Length > 100
                            || input.Libraries.Any(x => x < 1)
                            || input.Libraries.Distinct().Count() != input.Libraries.Length
                            || (input.Enabled && input.Libraries.Length == 0)
                        )
                            throw new ArgumentException(
                                "Select at least one library before enabling guests."
                            );
                        await credentials.WaitAsync();
                        try
                        {
                            if (
                                await db.Libraries.CountAsync(x => input.Libraries.Contains(x.Id))
                                != input.Libraries.Length
                            )
                                throw new ArgumentException("Unknown library.");
                            await using var transaction = await db.Database.BeginTransactionAsync();
                            var guest = await db.Users.SingleOrDefaultAsync(x => x.Role == "Guest");
                            if (guest is null)
                            {
                                guest = new User
                                {
                                    Id = await db.Users.MaxAsync(x => x.Id) + 1,
                                    Name = "guest-" + Guid.NewGuid().ToString("N"),
                                    Role = "Guest",
                                    AllLibraries = false,
                                    Disabled = !input.Enabled,
                                };
                                db.Users.Add(guest);
                            }
                            guest.Disabled = !input.Enabled;
                            guest.AllLibraries = false;
                            await db.Sessions.Where(x => x.UserId == guest.Id).ExecuteDeleteAsync();
                            await db.Grants.Where(x => x.UserId == guest.Id).ExecuteDeleteAsync();
                            db.Grants.AddRange(
                                input.Libraries.Select(id => new LibraryGrant
                                {
                                    UserId = guest.Id,
                                    LibraryId = id,
                                })
                            );
                            await db.Database.ExecuteSqlInterpolatedAsync(
                                $"INSERT INTO FeaturePolicies (Key, Enabled) VALUES ({GuestKey}, {input.Enabled}) ON CONFLICT(Key) DO UPDATE SET Enabled = excluded.Enabled"
                            );
                            await db.SaveChangesAsync();
                            await transaction.CommitAsync();
                            return Results.Ok(input);
                        }
                        finally
                        {
                            credentials.Release();
                        }
                    }
                )
                .RequireAuthorization("admin");
            root.MapPost(
                    "/auth/guest",
                    async (MediaDb db, HttpContext ctx) =>
                    {
                        if (ctx.User.Identity?.IsAuthenticated == true)
                            return Results.Conflict();
                        await credentials.WaitAsync();
                        try
                        {
                            if (!await GuestsEnabled(db))
                                return Results.Forbid();
                            var guest = await db.Users.SingleOrDefaultAsync(x =>
                                x.Role == "Guest" && !x.Disabled
                            );
                            if (guest is null)
                                return Results.Forbid();
                            await db
                                .Sessions.Where(x =>
                                    x.UserId == guest.Id && x.ExpiresAt <= DateTime.UtcNow
                                )
                                .ExecuteDeleteAsync();
                            if (await db.Sessions.CountAsync(x => x.UserId == guest.Id) >= 100)
                                return Results.StatusCode(429);
                            var expires = DateTime.UtcNow.AddHours(4);
                            var session = new Session
                            {
                                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                                UserId = guest.Id,
                                DeviceName = "Guest browser",
                                ExpiresAt = expires,
                            };
                            db.Sessions.Add(session);
                            await db.SaveChangesAsync();
                            var identity = new ClaimsIdentity(
                                new[]
                                {
                                    new Claim(
                                        ClaimTypes.NameIdentifier,
                                        guest.Id.ToString(CultureInfo.InvariantCulture)
                                    ),
                                    new Claim(ClaimTypes.Name, "Guest"),
                                    new Claim(ClaimTypes.Role, "Guest"),
                                    new Claim("sid", session.Id),
                                },
                                CookieAuthenticationDefaults.AuthenticationScheme
                            );
                            await ctx.SignInAsync(
                                CookieAuthenticationDefaults.AuthenticationScheme,
                                new ClaimsPrincipal(identity),
                                new AuthenticationProperties
                                {
                                    ExpiresUtc = expires,
                                    IsPersistent = false,
                                }
                            );
                            return Results.Ok(new UserIdentity("Guest", "Guest"));
                        }
                        finally
                        {
                            credentials.Release();
                        }
                    }
                )
                .Produces<UserIdentity>()
                .RequireRateLimiting("auth");
        }
    }

    // Explicit allowlist: future endpoints are private until reviewed for guest access.
    public static async Task GuestBoundary(HttpContext context, RequestDelegate next)
    {
        if (!context.User.IsInRole("Guest") || !context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }
        var db = context.RequestServices.GetRequiredService<MediaDb>();
        if (!await GuestsEnabled(db))
        {
            context.Response.StatusCode = 401;
            return;
        }
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "";
        if (route.StartsWith("/api/v1/", StringComparison.Ordinal))
            route = route[7..];
        else if (route.StartsWith("/api/", StringComparison.Ordinal))
            route = route[4..];
        if (context.Request.Method == "POST" && route == "/auth/logout")
        {
            await next(context);
            return;
        }
        if (context.Request.Method is not ("GET" or "HEAD"))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (
            route
            is "/progress"
                or "/continue-watching"
                or "/continue-watching-media"
                or "/collections"
        )
        {
            await Results.Ok(Array.Empty<object>()).ExecuteAsync(context);
            return;
        }
        if (
            route
            is "/auth/me"
                or "/auth/csrf"
                or "/auth/guest"
                or "/server-info"
                or "/status"
                or "/features"
                or "/browse-libraries"
                or "/media"
                or "/media-page"
                or "/media/{id:int}"
                or "/media/{id:int}/stream"
                or "/media/{id:int}/playback-info"
                or "/media/{id:int}/tracks"
        )
        {
            await next(context);
            return;
        }
        context.Response.StatusCode = 403;
    }
}
