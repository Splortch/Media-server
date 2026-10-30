using System.Security.Claims;
using System.Text.RegularExpressions;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

public record MovieRequestInput(int LibraryId, string Title, int? Year, string? Note);

public record MovieRequestReview(string State, string? Reply, int? MediaItemId, long Revision);

public record MovieRequestView(
    int Id,
    int LibraryId,
    string Library,
    string Title,
    int? Year,
    string Note,
    string State,
    string Reply,
    int? MediaItemId,
    bool Mine,
    int SupportCount,
    bool Supported,
    long Revision,
    DateTime UpdatedAt
);

public static class MovieRequests
{
    public static void MapMovieRequests(this WebApplication app)
    {
        // Serialize duplicate check + insert across both API aliases in this single-process server.
        var gate = new SemaphoreSlim(1, 1);
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var api = app.MapGroup(prefix + "/requests").RequireAuthorization();
            if (prefix.EndsWith("v1", StringComparison.Ordinal))
                api.WithGroupName("client-v1");
            api.AddEndpointFilter(
                async (context, next) =>
                    await Features.Enabled(
                        context.HttpContext.RequestServices.GetRequiredService<MediaDb>(),
                        "movie-requests"
                    )
                        ? await next(context)
                        : Results.Forbid()
            );
            api.MapGet(
                    "",
                    async (MediaDb db, ClaimsPrincipal user, string? q, int? before) =>
                    {
                        var query = Visible(db, user);
                        if (before is not null)
                            query = query.Where(x => x.Id < before);
                        if (q is not null)
                        {
                            if (q.Length > 160)
                                throw new ArgumentException(
                                    "Search must be at most 160 characters."
                                );
                            var text = Normalize(q);
                            query = query.Where(x => x.SearchTitle.Contains(text));
                        }
                        return Results.Ok(
                            await Views(db, user, query.OrderByDescending(x => x.Id).Take(200))
                                .ToArrayAsync()
                        );
                    }
                )
                .Produces<MovieRequestView[]>();
            api.MapGet(
                    "/{id:int}",
                    async (int id, MediaDb db, ClaimsPrincipal user) =>
                    {
                        var row = await Visible(db, user).SingleOrDefaultAsync(x => x.Id == id);
                        return row is null
                            ? Results.NotFound()
                            : Results.Ok(await View(db, user, row));
                    }
                )
                .Produces<MovieRequestView>();
            api.MapPost(
                    "",
                    async (MovieRequestInput input, MediaDb db, ClaimsPrincipal user) =>
                    {
                        if (
                            string.IsNullOrWhiteSpace(input.Title)
                            || input.Title.Length > 160
                            || input.Title.Any(char.IsControl)
                            || input.Year is < 1888 or > 2100
                            || (input.Note?.Length ?? 0) > 500
                            || input.Note?.Any(c => char.IsControl(c) && c != '\n') == true
                        )
                            throw new ArgumentException(
                                "Enter a title (1–160 characters), optional year (1888–2100), and note up to 500 characters."
                            );
                        if (!await db.Libraries.AnyAsync(x => x.Id == input.LibraryId))
                            return Results.NotFound();
                        var uid = Operations.Uid(user);
                        var normalized = Normalize(input.Title);
                        await gate.WaitAsync();
                        try
                        {
                            var duplicate = await Visible(db, user)
                                .FirstOrDefaultAsync(x =>
                                    x.LibraryId == input.LibraryId
                                    && x.SearchTitle == normalized
                                    && x.Year == input.Year
                                );
                            if (duplicate is not null)
                                return Results.Conflict(
                                    new
                                    {
                                        duplicateRequestId = duplicate.Id,
                                        message = "A visible request already exists. Support it instead.",
                                    }
                                );
                            // Return only authorized existing media; never infer fulfillment from a title match.
                            var matches = await db
                                .Items.Where(x => x.Title.ToLower() == input.Title.Trim().ToLower())
                                .Select(x => new { x.Id, x.Title })
                                .Take(10)
                                .ToArrayAsync();
                            if (matches.Length > 0)
                                return Results.Conflict(
                                    new
                                    {
                                        message = "An accessible title with this name is already in the library.",
                                        matches,
                                    }
                                );
                            if (
                                await db.MovieRequests.CountAsync(x => x.UserId == uid) >= 50
                                || await db.MovieRequests.CountAsync() >= 5000
                            )
                                return Results.Problem(
                                    "Request limit reached. Withdraw an old request or ask an administrator.",
                                    statusCode: 409
                                );
                            var row = new MovieRequest
                            {
                                UserId = uid,
                                LibraryId = input.LibraryId,
                                Title = input.Title.Trim(),
                                SearchTitle = normalized,
                                Year = input.Year,
                                Note = input.Note?.Trim() ?? "",
                            };
                            db.MovieRequests.Add(row);
                            await db.SaveChangesAsync();
                            return Results.Created(
                                prefix + "/requests/" + row.Id,
                                await View(db, user, row)
                            );
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }
                )
                .Produces<MovieRequestView>(201)
                .RequireRateLimiting("requests");
            api.MapPut(
                    "/{id:int}/support",
                    async (int id, MediaDb db, ClaimsPrincipal user) =>
                    {
                        var row = await Visible(db, user).SingleOrDefaultAsync(x => x.Id == id);
                        if (row is null)
                            return Results.NotFound();
                        var uid = Operations.Uid(user);
                        if (row.UserId == uid || row.State is "Added" or "Declined")
                            return Results.BadRequest(
                                new { message = "Only support another member's open request." }
                            );
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"INSERT INTO RequestSupport (RequestId, UserId) SELECT {id}, {uid} WHERE EXISTS (SELECT 1 FROM MovieRequests WHERE Id = {id}) ON CONFLICT(RequestId, UserId) DO NOTHING"
                        );
                        return Results.NoContent();
                    }
                )
                .RequireRateLimiting("requests");
            api.MapDelete(
                "/{id:int}/support",
                async (int id, MediaDb db, ClaimsPrincipal user) =>
                {
                    if (!await Visible(db, user).AnyAsync(x => x.Id == id))
                        return Results.NotFound();
                    var uid = Operations.Uid(user);
                    await db
                        .RequestSupport.Where(x => x.RequestId == id && x.UserId == uid)
                        .ExecuteDeleteAsync();
                    return Results.NoContent();
                }
            );
            api.MapDelete(
                "/{id:int}",
                async (int id, MediaDb db, ClaimsPrincipal user) =>
                {
                    var uid = Operations.Uid(user);
                    var admin = user.IsInRole("Admin");
                    var count = await Visible(db, user)
                        .Where(x => x.Id == id && (admin || x.UserId == uid))
                        .ExecuteDeleteAsync();
                    return count == 0 ? Results.NotFound() : Results.NoContent();
                }
            );
            api.MapPut(
                    "/{id:int}",
                    async (int id, MovieRequestReview input, MediaDb db, ClaimsPrincipal user) =>
                    {
                        if (
                            input.State
                                is not ("Requested" or "Considering" or "Added" or "Declined")
                            || (input.Reply?.Length ?? 0) > 500
                            || input.Revision < 0
                            || input.Reply?.Any(c => char.IsControl(c) && c != '\n') == true
                            || (input.State == "Added") != input.MediaItemId.HasValue
                        )
                            throw new ArgumentException(
                                "Choose a valid status. Added requires an explicitly selected library title; replies are limited to 500 characters."
                            );
                        var row = await db.MovieRequests.SingleOrDefaultAsync(x => x.Id == id);
                        if (row is null)
                            return Results.NotFound();
                        if (
                            input.MediaItemId is int media
                            && !await db.Items.AnyAsync(x =>
                                x.Id == media && x.LibraryId == row.LibraryId
                            )
                        )
                            throw new ArgumentException(
                                "Select an available title from the request's library."
                            );
                        if (row.Revision != input.Revision)
                            return Results.Conflict(
                                new { message = "This request changed. Refresh before saving." }
                            );
                        row.State = input.State;
                        row.Reply = input.Reply?.Trim() ?? "";
                        row.MediaItemId = input.MediaItemId;
                        row.UpdatedAt = DateTime.UtcNow;
                        row.Revision++;
                        try
                        {
                            await db.SaveChangesAsync();
                        }
                        catch (DbUpdateConcurrencyException)
                        {
                            return Results.Conflict(
                                new { message = "This request changed. Refresh before saving." }
                            );
                        }
                        return Results.Ok(await View(db, user, row));
                    }
                )
                .RequireAuthorization("admin")
                .Produces<MovieRequestView>();
        }
    }

    private static string Normalize(string title) =>
        Regex.Replace(title.Trim(), @"\s+", " ").ToLowerInvariant();

    private static IQueryable<MovieRequest> Visible(MediaDb db, ClaimsPrincipal user)
    {
        var uid = Operations.Uid(user);
        var admin = user.IsInRole("Admin");
        // Rating-restricted users see only their own requests, not unclassified member titles.
        return db.MovieRequests.Where(x =>
            db.Libraries.Any(l => l.Id == x.LibraryId)
            && (
                admin
                || x.UserId == uid
                || db.Users.Any(u => u.Id == uid && u.MaximumRating == null)
            )
        );
    }

    private static Task<MovieRequestView> View(
        MediaDb db,
        ClaimsPrincipal user,
        MovieRequest row
    ) => Views(db, user, db.MovieRequests.Where(x => x.Id == row.Id)).SingleAsync();

    private static IQueryable<MovieRequestView> Views(
        MediaDb db,
        ClaimsPrincipal user,
        IQueryable<MovieRequest> rows
    )
    {
        var uid = Operations.Uid(user);
        return rows.Select(row => new MovieRequestView(
            row.Id,
            row.LibraryId,
            db.Libraries.Where(x => x.Id == row.LibraryId).Select(x => x.Name).FirstOrDefault()
                ?? "Unavailable library",
            row.Title,
            row.Year,
            row.Note,
            row.State,
            row.Reply,
            row.MediaItemId.HasValue && db.Items.Any(x => x.Id == row.MediaItemId)
                ? row.MediaItemId
                : null,
            row.UserId == uid,
            db.RequestSupport.Count(x => x.RequestId == row.Id),
            db.RequestSupport.Any(x => x.RequestId == row.Id && x.UserId == uid),
            row.Revision,
            row.UpdatedAt
        ));
    }
}
