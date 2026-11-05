using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

public static class MetadataEndpoints
{
    public static void MapMetadata(this WebApplication app)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var group = app.MapGroup(prefix).RequireAuthorization();
            if (prefix.EndsWith("v1"))
                group.WithGroupName("client-v1");
            group
                .MapGet(
                    "/media/{id:int}/metadata",
                    async (int id, MediaDb db, CancellationToken ct) =>
                    {
                        if (!await db.Items.AnyAsync(x => x.Id == id, ct))
                            return Results.NotFound();
                        var row = await db.Metadata.SingleOrDefaultAsync(
                            x => x.MediaItemId == id,
                            ct
                        );
                        return row is null
                            ? Results.Text("null", "application/json")
                            : Results.Json(MetadataService.View(row, id, prefix));
                    }
                )
                .Produces<MetadataView>();
            group.MapGet(
                "/media/{id:int}/artwork/{kind}",
                async (
                    int id,
                    string kind,
                    MediaDb db,
                    ArtworkCache cache,
                    HttpContext context,
                    CancellationToken ct
                ) =>
                {
                    if (!await db.Items.AnyAsync(x => x.Id == id, ct))
                        return Results.NotFound();
                    var row = await db.Metadata.SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
                    var key =
                        kind == "poster" ? row?.PosterKey
                        : kind == "backdrop" ? row?.BackdropKey
                        : null;
                    if (key is null)
                        return Results.NotFound();
                    try
                    {
                        var file = cache.Open(key);
                        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                        context.Response.Headers.CacheControl = "private, max-age=300";
                        return Results.File(
                            file,
                            key.EndsWith(".png") ? "image/png"
                                : key.EndsWith(".jpg") ? "image/jpeg"
                                : "image/webp"
                        );
                    }
                    catch (Exception e)
                        when (e
                                is IOException
                                    or UnauthorizedAccessException
                                    or MetadataFault
                                    or ArgumentException
                        )
                    {
                        return Results.NotFound();
                    }
                }
            );
            var admin = group.MapGroup("").RequireAuthorization("admin");
            admin.MapGet(
                "/metadata/providers",
                (IMetadataProvider provider) => Results.Ok(new { provider.Id, provider.Enabled })
            );
            admin
                .MapPost(
                    "/metadata/search",
                    async (
                        MetadataSearch input,
                        IMetadataProvider provider,
                        CancellationToken ct
                    ) =>
                    {
                        try
                        {
                            return Results.Ok(await provider.Search(input, ct));
                        }
                        catch (Exception e) when (Handled(e))
                        {
                            return Failure(e);
                        }
                    }
                )
                .Produces<MetadataCandidate[]>();
            admin
                .MapPost(
                    "/media/{id:int}/metadata/local",
                    async (
                        int id,
                        MetadataRevision input,
                        MediaDb db,
                        LocalMetadata local,
                        PathPolicy paths,
                        MetadataService service,
                        CancellationToken ct
                    ) =>
                        await Change(
                            id,
                            input.ExpectedRevision,
                            db,
                            service,
                            prefix,
                            async item =>
                            {
                                var library = await db.Libraries.SingleAsync(
                                    x => x.Id == item.LibraryId,
                                    ct
                                );
                                return await local.Read(item, library, paths, ct);
                            },
                            ct
                        )
                )
                .Produces<MetadataView>();
            admin
                .MapPost(
                    "/media/{id:int}/metadata/tmdb",
                    async (
                        int id,
                        MetadataMatch input,
                        MediaDb db,
                        IMetadataProvider provider,
                        MetadataService service,
                        CancellationToken ct
                    ) =>
                        await Change(
                            id,
                            input.ExpectedRevision,
                            db,
                            service,
                            prefix,
                            async item =>
                            {
                                var library = await db.Libraries.SingleAsync(
                                    x => x.Id == item.LibraryId,
                                    ct
                                );
                                if (library.Kind != input.Kind)
                                    throw new MetadataFault("metadata-kind-does-not-match-library");
                                var result = await provider.Match(input, ct);
                                var poster = await service.Image(result.Poster, false, ct);
                                var backdrop = await service.Image(result.Backdrop, true, ct);
                                return result with { Poster = poster, Backdrop = backdrop };
                            },
                            ct
                        )
                )
                .Produces<MetadataView>();
            admin
                .MapPut(
                    "/media/{id:int}/metadata",
                    async (
                        int id,
                        MetadataCorrection input,
                        MediaDb db,
                        MetadataService service,
                        CancellationToken ct
                    ) =>
                        await Change(
                            id,
                            input.ExpectedRevision,
                            db,
                            service,
                            prefix,
                            async item =>
                            {
                                var old = await db.Metadata.SingleOrDefaultAsync(
                                    x => x.MediaItemId == id,
                                    ct
                                );
                                // Editing text does not erase provider artwork or its attribution.
                                var attribution = old is null
                                    ? "Manual library metadata"
                                    : System
                                        .Text.Json.JsonSerializer.Deserialize<MetadataValue>(
                                            old.DocumentJson
                                        )
                                        ?.Attribution
                                        ?? "Manual library metadata";
                                return new MetadataResult(
                                    input.Details with
                                    {
                                        Provider = "manual",
                                        ProviderId = null,
                                        Attribution = attribution,
                                    },
                                    old?.PosterKey,
                                    old?.BackdropKey
                                );
                            },
                            ct
                        )
                )
                .Produces<MetadataView>();
        }
    }

    private static bool Handled(Exception e) =>
        e
            is MetadataFault
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException
                or System.Text.Json.JsonException
                or DbUpdateConcurrencyException
                or OperationCanceledException
                or ArgumentException
                or InvalidOperationException
                or KeyNotFoundException;

    private static IResult Failure(Exception e) =>
        e is MetadataFault { Code: "metadata-revision-conflict" } or DbUpdateConcurrencyException
            ? Results.Conflict(
                new
                {
                    error = "metadata-revision-conflict",
                    message = "Title details changed. Reload this title before saving again.",
                }
            )
            : Results.BadRequest(
                new
                {
                    error = e is MetadataFault fault ? fault.Code : "metadata-unavailable",
                    message = e is MetadataFault problem
                        ? problem.Code.Replace('-', ' ')
                        : "Metadata is unavailable. Existing details have been preserved.",
                }
            );

    private static async Task<IResult> Change(
        int id,
        long revision,
        MediaDb db,
        MetadataService service,
        string prefix,
        Func<MediaServer.Core.MediaItem, Task<MetadataResult>> read,
        CancellationToken ct
    )
    {
        await service.Gate.WaitAsync(ct);
        try
        {
            var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null)
                return Results.NotFound();
            var current = await db.Metadata.SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
            if ((current?.Revision ?? 0) != revision)
                throw new MetadataFault("metadata-revision-conflict");
            var value = await read(item);
            var row = await MetadataService.Save(
                db,
                item,
                value.Details,
                value.Poster,
                value.Backdrop,
                revision,
                ct
            );
            return Results.Ok(MetadataService.View(row, id, prefix));
        }
        catch (Exception e) when (Handled(e))
        {
            return Failure(e);
        }
        finally
        {
            service.Gate.Release();
        }
    }
}
