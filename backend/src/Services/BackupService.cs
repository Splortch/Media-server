using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Services;

public record BackupEntry(string Name, long Length, string Sha256);

public record BackupManifest(
    int Format,
    string Version,
    DateTime CreatedAt,
    string[] Migrations,
    BackupEntry[] Files
);

// Callers hold the exclusive data-directory lease; no live HTTP backup/restore endpoint exists.
public sealed class BackupService(string dataDirectory, long maximumBytes = 4L * 1024 * 1024 * 1024)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static bool Allowed(string name) =>
        name is "media.db" or "launcher.json" or "server-configuration.json"
        || (
            name.StartsWith("keys/", StringComparison.Ordinal)
            && name.EndsWith(".xml", StringComparison.Ordinal)
            && name[5..].IndexOfAny(['/', '\\', ':']) < 0
        );

    private static void PrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
    }

    private static FileStream PrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task<string> Digest(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }

    private static async Task<string[]> Migrations(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        await connection.OpenAsync();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check";
            if ((string?)await command.ExecuteScalarAsync() != "ok")
                throw new InvalidDataException("Backup database integrity check failed.");
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
            using var reader = await command.ExecuteReaderAsync();
            var versions = new List<string>();
            while (await reader.ReadAsync())
                versions.Add(reader.GetString(0));
            return versions.ToArray();
        }
    }

    public async Task CreateAsync(string destination, string version, object configuration)
    {
        destination = Path.GetFullPath(destination);
        if (PathPolicy.Within(dataDirectory, destination))
            throw new ArgumentException("Choose a backup destination outside the data directory.");
        if (!File.Exists(Path.Combine(dataDirectory, "media.db")))
            throw new InvalidDataException("No server database exists to back up.");
        var temporary = Path.Combine(
            Path.GetTempPath(),
            "media-backup-" + Guid.NewGuid().ToString("N")
        );
        PrivateDirectory(temporary);
        try
        {
            var database = Path.Combine(dataDirectory, "media.db");
            PathPolicy.RejectLinks(database);
            using (
                var source = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = database,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString()
                )
            )
            using (
                var target = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(temporary, "media.db"),
                        Pooling = false,
                    }.ToString()
                )
            )
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }
            var files = new List<string> { "media.db" };
            var keys = Path.Combine(dataDirectory, "keys");
            if (Directory.Exists(keys))
                foreach (
                    var file in Directory.EnumerateFiles(
                        keys,
                        "*.xml",
                        SearchOption.TopDirectoryOnly
                    )
                )
                {
                    PathPolicy.RejectLinks(file);
                    var name = "keys/" + Path.GetFileName(file);
                    if (!Allowed(name))
                        throw new InvalidDataException("Unsafe key file.");
                    PrivateDirectory(Path.Combine(temporary, "keys"));
                    File.Copy(file, Path.Combine(temporary, name));
                    files.Add(name);
                }
            var launcher = Path.Combine(dataDirectory, "launcher.json");
            if (File.Exists(launcher))
            {
                PathPolicy.RejectLinks(launcher);
                File.Copy(launcher, Path.Combine(temporary, "launcher.json"));
                files.Add("launcher.json");
            }
            await File.WriteAllTextAsync(
                Path.Combine(temporary, "server-configuration.json"),
                JsonSerializer.Serialize(configuration, Json)
            );
            files.Add("server-configuration.json");
            var entries = new List<BackupEntry>();
            long total = 0;
            foreach (var name in files)
            {
                var path = Path.Combine(temporary, name);
                var length = new FileInfo(path).Length;
                total = checked(total + length);
                if (total > maximumBytes)
                    throw new InvalidDataException("Backup exceeds the configured size limit.");
                entries.Add(new(name, length, await Digest(path)));
            }
            var manifest = new BackupManifest(
                1,
                version,
                DateTime.UtcNow,
                await Migrations(Path.Combine(temporary, "media.db")),
                entries.ToArray()
            );
            var outputCreated = false;
            try
            {
                using var output = PrivateFile(destination);
                outputCreated = true;
                using var zip = new ZipArchive(output, ZipArchiveMode.Create);
                foreach (var name in files)
                    zip.CreateEntryFromFile(
                        Path.Combine(temporary, name),
                        name,
                        CompressionLevel.Optimal
                    );
                using var metadata = new StreamWriter(zip.CreateEntry("backup.json").Open());
                await metadata.WriteAsync(JsonSerializer.Serialize(manifest, Json));
            }
            catch
            {
                if (outputCreated)
                    File.Delete(destination);
                throw;
            }
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    public async Task<BackupManifest> InspectAsync(string archive)
    {
        var stage = Path.Combine(
            Path.GetTempPath(),
            "media-restore-check-" + Guid.NewGuid().ToString("N")
        );
        PrivateDirectory(stage);
        try
        {
            return await ExtractAsync(archive, stage);
        }
        finally
        {
            Directory.Delete(stage, true);
        }
    }

    private async Task<BackupManifest> ExtractAsync(string archive, string stage)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (
            zip.Entries.Count is < 2 or > 1003
            || zip.Entries.Sum(x => x.Length) > maximumBytes + 1024 * 1024
        )
            throw new InvalidDataException("Backup exceeds limits.");
        if (
            zip.Entries.Select(x => x.FullName).Distinct(StringComparer.Ordinal).Count()
            != zip.Entries.Count
        )
            throw new InvalidDataException("Duplicate backup entries.");
        var metadata =
            zip.GetEntry("backup.json")
            ?? throw new InvalidDataException("Missing backup manifest.");
        if (metadata.Length > 1024 * 1024)
            throw new InvalidDataException("Manifest exceeds limits.");
        BackupManifest manifest;
        using (var input = metadata.Open())
            manifest =
                await JsonSerializer.DeserializeAsync<BackupManifest>(input)
                ?? throw new InvalidDataException("Invalid manifest.");
        if (
            manifest.Format != 1
            || manifest.Files is null
            || manifest.Migrations is null
            || manifest.Files.Length != zip.Entries.Count - 1
            || manifest.Files.Select(x => x.Name).Distinct().Count() != manifest.Files.Length
            || !manifest.Files.Any(x => x.Name == "media.db")
        )
            throw new InvalidDataException("Invalid manifest.");
        using var context = new MediaDb(
            new DbContextOptionsBuilder<MediaDb>().UseSqlite("Data Source=:memory:").Options
        );
        var known = context.Database.GetMigrations().ToArray();
        if (
            manifest.Migrations.Length == 0
            || manifest.Migrations.Length > known.Length
            || !manifest.Migrations.SequenceEqual(known.Take(manifest.Migrations.Length))
        )
            throw new InvalidDataException(
                "This backup requires an incompatible or newer database migration. Use the matching server version."
            );
        foreach (var file in manifest.Files)
        {
            if (
                file.Name is null
                || !Allowed(file.Name)
                || file.Length < 0
                || file.Length > maximumBytes
            )
                throw new InvalidDataException("Unsafe backup entry.");
            var entry =
                zip.GetEntry(file.Name) ?? throw new InvalidDataException("Incomplete backup.");
            if (
                entry.Length != file.Length
                || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000
            )
                throw new InvalidDataException("Invalid backup entry.");
            var path = Path.Combine(stage, file.Name);
            PrivateDirectory(Path.GetDirectoryName(path)!);
            using (var input = entry.Open())
            using (var output = PrivateFile(path))
            {
                var buffer = new byte[65536];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    count += read;
                    if (count > file.Length)
                        throw new InvalidDataException("Expanded entry exceeds declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
                if (count != file.Length)
                    throw new InvalidDataException("Truncated backup.");
            }
            if (await Digest(path) != file.Sha256)
                throw new InvalidDataException("Backup checksum verification failed.");
        }
        var actualMigrations = await Migrations(Path.Combine(stage, "media.db"));
        if (!manifest.Migrations.SequenceEqual(actualMigrations))
            throw new InvalidDataException("Database migration metadata does not match manifest.");
        return manifest;
    }

    public async Task RestoreAsync(string archive)
    {
        if (
            Directory
                .EnumerateFileSystemEntries(dataDirectory)
                .Any(x => Path.GetFileName(x) != ".server.lock")
        )
            throw new InvalidDataException(
                "Restore requires an empty data directory. Preserve the old directory for rollback."
            );
        var stage = Path.Combine(
            Path.GetTempPath(),
            "media-restore-" + Guid.NewGuid().ToString("N")
        );
        PrivateDirectory(stage);
        var marker = Path.Combine(dataDirectory, ".restore-in-progress");
        var created = new List<string>();
        try
        {
            var manifest = await ExtractAsync(archive, stage);
            // A rollback must not resurrect cookies revoked since the checkpoint.
            await using (
                var restored = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(stage, "media.db"),
                        Pooling = false,
                    }.ToString()
                )
            )
            {
                await restored.OpenAsync();
                using var revoke = restored.CreateCommand();
                revoke.CommandText = "DELETE FROM Sessions";
                await revoke.ExecuteNonQueryAsync();
            }
            await File.WriteAllTextAsync(
                marker,
                "Restore incomplete. Do not start this data directory. Restore again into an empty directory.\n"
            );
            foreach (var file in manifest.Files)
            {
                var target = Path.Combine(dataDirectory, file.Name);
                PrivateDirectory(Path.GetDirectoryName(target)!);
                using var source = File.OpenRead(Path.Combine(stage, file.Name));
                using var destination = PrivateFile(target);
                created.Add(target);
                await source.CopyToAsync(destination);
            }
            File.Delete(marker);
        }
        catch
        {
            foreach (var file in created)
                File.Delete(file);
            throw;
        }
        finally
        {
            Directory.Delete(stage, true);
        }
    }
}
