using System.Runtime.InteropServices;
using MediaServer.Downloads;
using MediaServer.Media;
using Xunit;

namespace Api.Tests;

public sealed class DownloadStorageTests : IDisposable
{
    private readonly string root = Path.Combine(
        TestPaths.TempRoot,
        "download-files-" + Guid.NewGuid()
    );

    public DownloadStorageTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    private sealed class Space : IDownloadSpace
    {
        public long Available(string directory) => DownloadStorage.TotalLimit * 2;
    }

    [Fact]
    public void CreatesAndReopensWithoutTruncationOrOverwrite()
    {
        var policy = new PathPolicy([root]);
        using (var f = policy.OpenWrite(root, "test.part", true))
            f.Write([1, 2, 3]);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(root, "test.part"))
            );
        using (var f = policy.OpenWrite(root, "test.part", false))
        {
            Assert.Equal(3, f.Length);
            f.Position = f.Length;
            f.Write([4]);
        }
        Assert.ThrowsAny<IOException>(() => policy.OpenWrite(root, "test.part", true));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(root, "test.part")));
        var storage = new DownloadStorage(root, new Space());
        var id = Guid.NewGuid().ToString("N");
        using (var f = storage.OpenWrite(id))
            f.Write([5, 6]);
        storage.Complete(id);
        using (var f = storage.OpenRead(id))
            Assert.Equal(2, f.Length);
        storage.Discard(id);
        Assert.False(storage.Exists(id, true));
    }

    [Fact]
    public void HardLinkedWritesRejectedBeforeOriginalChanges()
    {
        var original = Path.Combine(root, "original");
        var link = Path.Combine(root, "linked");
        File.WriteAllBytes(original, [1, 2, 3]);
        if (OperatingSystem.IsWindows())
            Assert.True(CreateHardLink(link, original, IntPtr.Zero));
        else
            Assert.Equal(0, Link(original, link));
        var policy = new PathPolicy([root]);
        Assert.ThrowsAny<IOException>(() => policy.OpenWrite(root, "linked", false));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(original));
    }

    [Fact]
    public void RejectsEscapesAndLinkedParents()
    {
        var policy = new PathPolicy([root]);
        Assert.Throws<ArgumentException>(() => policy.OpenWrite(root, "../escape", true));
        if (OperatingSystem.IsWindows())
            return; // Link creation privileges are not guaranteed; hard-link test runs on Windows.
        var actual = Path.Combine(root, "actual");
        Directory.CreateDirectory(actual);
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), actual);
        Assert.Throws<ArgumentException>(() => policy.OpenWrite(root, "alias/file", true));
        Assert.Empty(Directory.GetFiles(actual));
    }

    [Fact]
    public void ReservationsIncludeOtherJobsAndCompletedBytes()
    {
        var storage = new DownloadStorage(root, new Space());
        Assert.Throws<DownloadFault>(() =>
            storage.Admit([
                new MediaServer.Core.DownloadJob
                {
                    State = "completed",
                    ReceivedBytes = 80L * 1024 * 1024 * 1024,
                },
                new MediaServer.Core.DownloadJob
                {
                    State = "queued",
                    MaxBytes = 30L * 1024 * 1024 * 1024,
                },
            ])
        );
        Assert.Throws<DownloadFault>(() =>
            storage.Admit([
                new MediaServer.Core.DownloadJob
                {
                    State = "queued",
                    MaxBytes = 60L * 1024 * 1024 * 1024,
                },
                new MediaServer.Core.DownloadJob
                {
                    State = "paused",
                    MaxBytes = 60L * 1024 * 1024 * 1024,
                },
            ])
        );
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string oldPath, string newPath);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr security);
}
