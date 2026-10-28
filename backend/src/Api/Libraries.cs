using MediaServer.Infrastructure;
using MediaServer.Services;
using Microsoft.EntityFrameworkCore;

public static partial class Operations
{
    private static void MapLibraries(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("").RequireAuthorization("admin");
        admin.MapDelete(
            "/libraries/{id:int}",
            async (int id, ScanScheduler scheduler, CancellationToken ct) =>
            {
                var result = await scheduler.RemoveAsync(id, ct);
                return result switch
                {
                    "missing" => Results.NotFound(),
                    "busy" => Results.Conflict(
                        new
                        {
                            message = "Wait for the active library scan to finish before removing this library.",
                        }
                    ),
                    _ => Results.NoContent(),
                };
            }
        );
        admin.MapPut(
            "/libraries/{id:int}/options",
            async (int id, LibraryOptions input, MediaDb db) =>
            {
                if (input.Kind is not ("movies" or "tv"))
                    throw new ArgumentException("Choose movies or tv.");
                var library = await db.Libraries.FindAsync(id);
                if (library is null)
                    return Results.NotFound();
                library.Kind = input.Kind;
                library.WatchEnabled = input.WatchEnabled;
                library.ComputeHashes = input.ComputeHashes;
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
        );
        admin.MapGet(
            "/libraries/{id:int}/missing",
            async (int id, MediaDb db) =>
                await db
                    .Items.IgnoreQueryFilters()
                    .Where(x => x.LibraryId == id && x.IsMissing)
                    .OrderBy(x => x.Id)
                    .Take(200)
                    .Select(x => new { x.Id, x.Title })
                    .ToListAsync()
        );
        admin.MapGet(
            "/duplicates",
            async (MediaDb db) =>
            {
                var hashes = await db
                    .Items.Where(x => x.Fingerprint != "")
                    .GroupBy(x => x.Fingerprint)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .Take(100)
                    .ToListAsync();
                var values = await db
                    .Items.Where(x => hashes.Contains(x.Fingerprint))
                    .Select(x => new
                    {
                        x.Id,
                        x.Title,
                        x.LibraryId,
                        x.Fingerprint,
                    })
                    .Take(1000)
                    .ToListAsync();
                return values
                    .GroupBy(x => x.Fingerprint)
                    .Select(g => new
                    {
                        fingerprint = g.Key,
                        items = g.Select(x => new
                        {
                            x.Id,
                            x.Title,
                            x.LibraryId,
                        }),
                    });
            }
        );
    }
}
