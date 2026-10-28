using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Tests;

public sealed class BackupTests
{
    private static async Task Fixture(string directory)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "keys"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "keys", "fixture.xml"),
            "private fixture key ring"
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "launcher.json"),
            "{\"mediaRoot\":\"fixture\"}"
        );
        await using var db = new MediaDb(
            new DbContextOptionsBuilder<MediaDb>()
                .UseSqlite(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(directory, "media.db"),
                        Pooling = false,
                    }.ToString()
                )
                .Options
        );
        await db.Database.MigrateAsync();
        db.Users.Add(
            new User
            {
                Id = 1,
                Name = "fixture",
                PasswordHash = "explicit fixture, not a real password",
            }
        );
        db.Sessions.Add(
            new Session
            {
                Id = "fixture-session",
                UserId = 1,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            }
        );
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task BackupIsConsistentPrivateAndRestoreDryRunDoesNotTouchExistingData()
    {
        var root = Path.Combine(Path.GetTempPath(), "backup-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            await Fixture(data);
            var service = new BackupService(data);
            var archive = Path.Combine(root, "backup.zip");
            await service.CreateAsync(
                archive,
                "test-version",
                new { AllowedRoots = new[] { "fixture" } }
            );
            var before = SHA256.HashData(
                await File.ReadAllBytesAsync(Path.Combine(data, "media.db"))
            );
            var manifest = await service.InspectAsync(archive);
            Assert.Contains(manifest.Files, x => x.Name == "keys/fixture.xml");
            Assert.Equal(
                before,
                SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(data, "media.db")))
            );
            if (!OperatingSystem.IsWindows())
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(archive)
                );
            await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(archive));
            var restored = Path.Combine(root, "restored");
            Directory.CreateDirectory(restored);
            await new BackupService(restored).RestoreAsync(archive);
            Assert.Equal(
                "private fixture key ring",
                await File.ReadAllTextAsync(Path.Combine(restored, "keys", "fixture.xml"))
            );
            await using var db = new MediaDb(
                new DbContextOptionsBuilder<MediaDb>()
                    .UseSqlite(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = Path.Combine(restored, "media.db"),
                            Pooling = false,
                        }.ToString()
                    )
                    .Options
            );
            Assert.Equal("fixture", (await db.Users.SingleAsync()).Name);
            Assert.Empty(await db.Sessions.ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TamperedUnsafeAndFutureSchemaArchivesAreRejectedBeforeRestoring()
    {
        var root = Path.Combine(Path.GetTempPath(), "backup-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            await Fixture(data);
            var archive = Path.Combine(root, "backup.zip");
            var service = new BackupService(data);
            await service.CreateAsync(archive, "test", new { });
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            {
                zip.GetEntry("launcher.json")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("launcher.json").Open());
                writer.Write("tampered");
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(archive));
            File.Delete(archive);
            await service.CreateAsync(archive, "test", new { });
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("backup.json")!;
                BackupManifest manifest;
                using (var reader = entry.Open())
                    manifest = JsonSerializer.Deserialize<BackupManifest>(reader)!;
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("backup.json").Open());
                writer.Write(
                    JsonSerializer.Serialize(
                        manifest with
                        {
                            Migrations = [.. manifest.Migrations, "99999999999999_Future"],
                        }
                    )
                );
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(archive));
            File.Delete(archive);
            await service.CreateAsync(archive, "test", new { });
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            {
                var metadata = zip.GetEntry("backup.json")!;
                BackupManifest manifest;
                using (var input = metadata.Open())
                    manifest = JsonSerializer.Deserialize<BackupManifest>(input)!;
                metadata.Delete();
                var payload = System.Text.Encoding.UTF8.GetBytes("not allowed");
                using (var escaped = zip.CreateEntry("../escape").Open())
                    escaped.Write(payload);
                using var writer = new StreamWriter(zip.CreateEntry("backup.json").Open());
                writer.Write(
                    JsonSerializer.Serialize(
                        manifest with
                        {
                            Files =
                            [
                                .. manifest.Files,
                                new BackupEntry(
                                    "../escape",
                                    payload.Length,
                                    Convert.ToHexString(SHA256.HashData(payload))
                                ),
                            ],
                        }
                    )
                );
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(archive));
            Assert.False(File.Exists(Path.Combine(root, "escape")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
