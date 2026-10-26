using MediaServer.Core;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Infrastructure;

public class MediaDb(DbContextOptions<MediaDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<MediaItem> Items => Set<MediaItem>();
    public DbSet<PlaybackProgress> Progress => Set<PlaybackProgress>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<ScanJob> Jobs => Set<ScanJob>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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
