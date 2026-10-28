using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

public record AccountInput(
    string Name,
    string Password,
    string Role = "Viewer",
    bool AllLibraries = false,
    int? MaximumRating = null,
    int[]? Libraries = null
);

public record AccountPermissions(
    string Role,
    bool Disabled,
    bool AllLibraries,
    int? MaximumRating,
    int[] Libraries
);

public record PasswordReset(string Password);

public record AccountInfo(
    int Id,
    string Name,
    string Role,
    bool Disabled,
    bool AllLibraries,
    int? MaximumRating,
    int[] Libraries
);

public record RatingInput(int Rating);

public record LibraryOptions(string Kind, bool WatchEnabled, bool ComputeHashes);

public record LibraryBrowse(int Id, string Name, string Kind);

public static partial class Operations
{
    public static int Uid(ClaimsPrincipal user) =>
        int.Parse(
            user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            System.Globalization.CultureInfo.InvariantCulture
        );

    public static void MapOperations(this WebApplication app, SemaphoreSlim credentials)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var api = app.MapGroup(prefix).RequireAuthorization();
            if (prefix.EndsWith("v1", StringComparison.Ordinal))
                api.WithGroupName("client-v1");
            MapAccounts(api, credentials);
            MapSearch(api);
            MapLibraries(api);
            MapMonitoring(api);
        }
    }

    private static void ValidatePolicy(string role, int? maximum, int[]? libraries)
    {
        if (
            role is not ("Admin" or "Viewer")
            || maximum is not (null or 10 or 13 or 16 or 18)
            || libraries is null
            || libraries.Length > 100
            || libraries.Any(x => x < 1)
            || libraries.Distinct().Count() != libraries.Length
        )
            throw new ArgumentException("Invalid role, rating or library permissions.");
    }

    private static async Task<bool> LibrariesExist(MediaDb db, int[] ids) =>
        await db.Libraries.CountAsync(x => ids.Contains(x.Id)) == ids.Length;

    private static async Task<AccountInfo> Account(MediaDb db, User user) =>
        new(
            user.Id,
            user.Name,
            user.Role,
            user.Disabled,
            user.AllLibraries,
            user.MaximumRating,
            await db.Grants.Where(x => x.UserId == user.Id).Select(x => x.LibraryId).ToArrayAsync()
        );

    private static void MapAccounts(RouteGroupBuilder api, SemaphoreSlim credentials)
    {
        var admin = api.MapGroup("").RequireAuthorization("admin");
        admin.MapGet(
            "/users",
            async (MediaDb db) =>
            {
                var users = await db.Users.OrderBy(x => x.Id).Take(1000).ToListAsync();
                var result = new List<AccountInfo>();
                foreach (var user in users)
                    result.Add(await Account(db, user));
                return result;
            }
        );
        admin
            .MapPost(
                "/users",
                async (AccountInput input, MediaDb db) =>
                {
                    var libraries = input.Libraries ?? [];
                    ValidatePolicy(input.Role, input.MaximumRating, libraries);
                    if (
                        string.IsNullOrWhiteSpace(input.Name)
                        || input.Name.Length > 64
                        || input.Name.Any(c =>
                            !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')
                        )
                        || string.IsNullOrEmpty(input.Password)
                        || input.Password.Length is < 12 or > 256
                    )
                        throw new ArgumentException(
                            "Use a 1–64 character ASCII username and a 12–256 character password."
                        );
                    await credentials.WaitAsync();
                    try
                    {
                        if (await db.Users.CountAsync() >= 1000)
                            return Results.Conflict();
                        if (await db.Users.AnyAsync(x => x.Name.ToLower() == input.Name.ToLower()))
                            return Results.Conflict();
                        if (!await LibrariesExist(db, libraries))
                            throw new ArgumentException("Unknown library.");
                        var user = new User
                        {
                            Id = await db.Users.MaxAsync(x => x.Id) + 1,
                            Name = input.Name,
                            Role = input.Role,
                            AllLibraries = input.AllLibraries,
                            MaximumRating = input.MaximumRating,
                            PasswordHash = await Passwords.Hash(input.Password),
                        };
                        db.Users.Add(user);
                        foreach (var library in libraries)
                            db.Grants.Add(
                                new LibraryGrant { UserId = user.Id, LibraryId = library }
                            );
                        await db.SaveChangesAsync();
                        return Results.Created($"/api/users/{user.Id}", await Account(db, user));
                    }
                    finally
                    {
                        credentials.Release();
                    }
                }
            )
            .RequireRateLimiting("auth");
        admin.MapPut(
            "/users/{id:int}",
            async (int id, AccountPermissions input, MediaDb db, ClaimsPrincipal principal) =>
            {
                ValidatePolicy(input.Role, input.MaximumRating, input.Libraries);
                if (id == 1 && (input.Disabled || input.Role != "Admin"))
                    throw new ArgumentException(
                        "The recovery administrator must stay enabled and retain its role."
                    );
                if (id == Uid(principal) && input.Disabled)
                    throw new ArgumentException("You cannot disable your current account.");
                await credentials.WaitAsync();
                try
                {
                    var user = await db.Users.FindAsync(id);
                    if (user is null)
                        return Results.NotFound();
                    if (!await LibrariesExist(db, input.Libraries))
                        throw new ArgumentException("Unknown library.");
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    user.Role = input.Role;
                    user.Disabled = input.Disabled;
                    user.AllLibraries = input.AllLibraries;
                    user.MaximumRating = input.MaximumRating;
                    await db.Grants.Where(x => x.UserId == id).ExecuteDeleteAsync();
                    foreach (var library in input.Libraries)
                        db.Grants.Add(new LibraryGrant { UserId = id, LibraryId = library });
                    await db.Sessions.Where(x => x.UserId == id).ExecuteDeleteAsync();
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return Results.Ok(await Account(db, user));
                }
                finally
                {
                    credentials.Release();
                }
            }
        );
        admin
            .MapPost(
                "/users/{id:int}/password",
                async (int id, PasswordReset input, MediaDb db) =>
                {
                    if (
                        string.IsNullOrEmpty(input.Password)
                        || input.Password.Length is < 12 or > 256
                    )
                        throw new ArgumentException("Use a 12–256 character password.");
                    await credentials.WaitAsync();
                    try
                    {
                        var user = await db.Users.FindAsync(id);
                        if (user is null)
                            return Results.NotFound();
                        var hash = await Passwords.Hash(input.Password);
                        await using var transaction = await db.Database.BeginTransactionAsync();
                        user.PasswordHash = hash;
                        await db.SaveChangesAsync();
                        await db.Sessions.Where(x => x.UserId == id).ExecuteDeleteAsync();
                        await transaction.CommitAsync();
                        return Results.NoContent();
                    }
                    finally
                    {
                        credentials.Release();
                    }
                }
            )
            .RequireRateLimiting("auth");
        admin.MapGet(
            "/users/{id:int}/sessions",
            async (int id, MediaDb db) =>
            {
                var sessions = await db
                    .Sessions.Where(x => x.UserId == id && x.ExpiresAt > DateTime.UtcNow)
                    .ToListAsync();
                return sessions
                    .Select(x => new SessionInfo(
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(x.Id)))[..32],
                        x.DeviceName,
                        x.CreatedAt,
                        x.ExpiresAt,
                        false
                    ))
                    .ToArray();
            }
        );
        admin.MapDelete(
            "/users/{id:int}/sessions",
            async (int id, MediaDb db) =>
            {
                await db.Sessions.Where(x => x.UserId == id).ExecuteDeleteAsync();
                return Results.NoContent();
            }
        );
        admin.MapPut(
            "/media/{id:int}/rating",
            async (int id, RatingInput input, MediaDb db) =>
            {
                if (input.Rating is not (0 or 10 or 13 or 16 or 18))
                    throw new ArgumentException("Rating must be unknown, 10, 13, 16 or 18.");
                var item = await db.Items.FindAsync(id);
                if (item is null)
                    return Results.NotFound();
                item.ContentRating = input.Rating;
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
        );
        api.MapGet(
            "/browse-libraries",
            async (MediaDb db) =>
                await db
                    .Libraries.OrderBy(x => x.Name)
                    .Select(x => new LibraryBrowse(x.Id, x.Name, x.Kind))
                    .ToListAsync()
        );
        api.MapGet(
            "/history",
            async (MediaDb db, ClaimsPrincipal user) =>
                await (
                    from h in db.History
                    join item in db.Items on h.MediaItemId equals item.Id
                    where h.UserId == Uid(user)
                    orderby h.LastPlayedAt descending
                    select new
                    {
                        h.MediaItemId,
                        item.Title,
                        h.Seconds,
                        h.LastPlayedAt,
                        h.CompletedAt,
                    }
                )
                    .Take(200)
                    .ToListAsync()
        );
    }
}
