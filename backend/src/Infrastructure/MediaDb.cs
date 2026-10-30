using MediaServer.Core;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Infrastructure;

public class MediaDb(DbContextOptions<MediaDb> options, IUserContext? context = null)
    : DbContext(options)
{
    private int? CurrentUserId => context?.UserId;
    public DbSet<MovieRequest> MovieRequests => Set<MovieRequest>();
    public DbSet<RequestSupport> RequestSupport => Set<RequestSupport>();
    public DbSet<FeaturePolicy> FeaturePolicies => Set<FeaturePolicy>();
    public DbSet<FeatureVisibility> FeatureVisibility => Set<FeatureVisibility>();
    public DbSet<LibraryGrant> Grants => Set<LibraryGrant>();
    public DbSet<WatchHistory> History => Set<WatchHistory>();
    public DbSet<SmartCollection> Collections => Set<SmartCollection>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<MediaItem> Items => Set<MediaItem>();
    public DbSet<PlaybackProgress> Progress => Set<PlaybackProgress>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<ScanJob> Jobs => Set<ScanJob>();

    public DbSet<ScanIssue> ScanIssues => Set<ScanIssue>();

    public DbSet<PlaybackReport> Reports => Set<PlaybackReport>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MovieRequest>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<MovieRequest>().HasOne<Library>().WithMany().HasForeignKey(x => x.LibraryId);
        b.Entity<MovieRequest>().HasIndex(x => new { x.LibraryId, x.SearchTitle });
        b.Entity<MovieRequest>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<RequestSupport>().HasKey(x => new { x.RequestId, x.UserId });
        b.Entity<RequestSupport>()
            .HasOne<MovieRequest>()
            .WithMany()
            .HasForeignKey(x => x.RequestId);
        b.Entity<RequestSupport>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<FeaturePolicy>().HasKey(x => x.Key);
        b.Entity<FeatureVisibility>().HasKey(x => new { x.UserId, x.Key });
        b.Entity<FeatureVisibility>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<Library>()
            .HasQueryFilter(library =>
                CurrentUserId == null
                || Users.Any(user =>
                    user.Id == CurrentUserId
                    && !user.Disabled
                    && (
                        user.Role == "Admin"
                        || user.AllLibraries
                        || Grants.Any(g => g.UserId == user.Id && g.LibraryId == library.Id)
                    )
                )
            );
        b.Entity<User>().Property(x => x.Name).UseCollation("NOCASE");
        b.Entity<User>().HasIndex(x => x.Name).IsUnique();
        b.Entity<MediaItem>()
            .HasQueryFilter(item =>
                CurrentUserId == null
                || (
                    !item.IsMissing
                    && Users.Any(user =>
                        user.Id == CurrentUserId
                        && !user.Disabled
                        && (
                            user.Role == "Admin"
                            || (
                                (
                                    user.AllLibraries
                                    || Grants.Any(grant =>
                                        grant.UserId == user.Id && grant.LibraryId == item.LibraryId
                                    )
                                )
                                && (
                                    user.MaximumRating == null
                                    || (
                                        item.ContentRating > 0
                                        && item.ContentRating <= user.MaximumRating
                                    )
                                )
                            )
                        )
                    )
                )
            );
        b.Entity<MediaItem>().HasIndex(x => new { x.SearchTitle, x.Id });
        b.Entity<MediaItem>().HasIndex(x => x.Fingerprint);
        b.Entity<LibraryGrant>().HasKey(x => new { x.UserId, x.LibraryId });
        b.Entity<LibraryGrant>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<LibraryGrant>().HasOne<Library>().WithMany().HasForeignKey(x => x.LibraryId);
        b.Entity<WatchHistory>().HasKey(x => new { x.UserId, x.MediaItemId });
        b.Entity<WatchHistory>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<WatchHistory>().HasOne<MediaItem>().WithMany().HasForeignKey(x => x.MediaItemId);
        b.Entity<SmartCollection>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<ScanIssue>().HasOne<ScanJob>().WithMany().HasForeignKey(x => x.ScanJobId);
        b.Entity<ScanIssue>().HasIndex(x => x.ScanJobId);
        b.Entity<PlaybackReport>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<PlaybackReport>().HasOne<MediaItem>().WithMany().HasForeignKey(x => x.MediaItemId);
        b.Entity<User>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<MediaItem>().HasIndex(x => new { x.LibraryId, x.RelativePath }).IsUnique();
        b.Entity<PlaybackProgress>().HasKey(x => new { x.UserId, x.MediaItemId });
        b.Entity<MediaItem>().HasOne<Library>().WithMany().HasForeignKey(x => x.LibraryId);
        b.Entity<PlaybackProgress>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<PlaybackProgress>()
            .HasOne<MediaItem>()
            .WithMany()
            .HasForeignKey(x => x.MediaItemId);
        b.Entity<Session>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<ScanJob>().HasOne<Library>().WithMany().HasForeignKey(x => x.LibraryId);
    }
}
