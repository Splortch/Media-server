using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Downloads;

public static class DownloadAccess
{
    public static async Task<bool> Allowed(MediaDb db, DownloadJob job, CancellationToken ct)
    {
        if (
            !await db.Users.AnyAsync(
                x => x.Id == job.UserId && !x.Disabled && x.Role == "Admin",
                ct
            )
        )
            return false;
        if (
            job.TargetLibraryId is int library
            && (job.ImportState != "discarded" || job.RequestId != null)
            && !await db
                .Libraries.IgnoreQueryFilters()
                .AnyAsync(
                    x =>
                        x.Id == library && x.Path == job.TargetRoot && x.Kind == job.Classification,
                    ct
                )
        )
            return false;
        if (job.RequestId is not int request)
            return true;
        if (!await Features.Enabled(db, "movie-requests"))
            return false;
        return await db.MovieRequests.AnyAsync(
                x =>
                    x.Id == request
                    && x.UserId == job.RequestOwnerId
                    && x.LibraryId == job.TargetLibraryId
                    && x.Revision == job.RequestRevision
                    && x.State == "Considering",
                ct
            )
            && await db.Users.AnyAsync(
                u =>
                    u.Id == job.RequestOwnerId
                    && !u.Disabled
                    && u.Role != "Guest"
                    && (
                        u.Role == "Admin"
                        || u.AllLibraries
                        || db.Grants.Any(g =>
                            g.UserId == u.Id && g.LibraryId == job.TargetLibraryId
                        )
                    )
                    && (
                        u.MaximumRating == null
                        || (job.ImportRating > 0 && job.ImportRating <= u.MaximumRating)
                    ),
                ct
            );
    }

    public static long ImportRemaining(DownloadJob job) =>
        job.TargetLibraryId != null
        && job.State != "cancelled"
        && job.ImportState is not ("none" or "imported" or "discarded")
            ? Math.Max(0, (job.TotalBytes ?? job.MaxBytes) - job.ImportCopiedBytes)
            : 0;
}
