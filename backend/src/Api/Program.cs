using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var recovery = args.Contains("--reset-admin-password", StringComparer.Ordinal);
var maintenance = args.Select((value, index) => (value, index))
    .Where(x => x.value is "--backup" or "--restore-check" or "--restore")
    .ToArray();
if (
    maintenance.Length > 1
    || (recovery && maintenance.Length > 0)
    || maintenance.Any(x => x.index + 1 >= args.Length)
)
{
    Console.Error.WriteLine("Choose one maintenance command and archive path.");
    Environment.ExitCode = 1;
    return;
}
var filtered = args.Where(
        (value, index) =>
            value != "--reset-admin-password"
            && !maintenance.Any(x => index == x.index || index == x.index + 1)
    )
    .ToArray();
var builder = WebApplication.CreateBuilder(filtered);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5050");
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
var data = Path.GetFullPath(builder.Configuration["MediaServer:DataDirectory"] ?? "data");
Directory.CreateDirectory(data);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(
        data,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
    );
FileStream instanceLock;
try
{
    var lockOptions = new FileStreamOptions
    {
        Mode = FileMode.OpenOrCreate,
        Access = FileAccess.ReadWrite,
        Share = FileShare.None,
    };
    if (!OperatingSystem.IsWindows())
        lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    instanceLock = new FileStream(Path.Combine(data, ".server.lock"), lockOptions);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(
        "Cannot exclusively lock the data directory. Ensure it is writable and stop other server instances before starting or recovering this server."
    );
    Environment.ExitCode = 1;
    return;
}
using var instanceLease = instanceLock;
if (File.Exists(Path.Combine(data, ".restore-in-progress")))
{
    Console.Error.WriteLine(
        "An interrupted restore was detected. Preserve this directory and restore into an empty directory."
    );
    Environment.ExitCode = 1;
    return;
}
if (maintenance.Length > 0)
{
    try
    {
        var service = new BackupService(
            data,
            builder.Configuration.GetValue<long?>("MediaServer:BackupMaxBytes")
                ?? 4L * 1024 * 1024 * 1024
        );
        var (action, index) = maintenance[0];
        var archive = args[index + 1];
        if (action == "--backup")
            await service.CreateAsync(
                archive,
                ClientV1.ServerVersion,
                new
                {
                    AllowedRoots = builder
                        .Configuration.GetSection("MediaServer:AllowedRoots")
                        .Get<string[]>()
                        ?? [],
                    FfprobePath = builder.Configuration["MediaServer:FfprobePath"] ?? "ffprobe",
                    SecureCookies = builder.Configuration.GetValue<bool>(
                        "MediaServer:SecureCookies"
                    ),
                    AllowedHosts = builder.Configuration["AllowedHosts"],
                    Urls = builder.Configuration["Urls"],
                }
            );
        else if (action == "--restore")
            await service.RestoreAsync(archive);
        else
            await service.InspectAsync(archive);
        Console.WriteLine(
            "Maintenance completed. Configuration in the archive is reference-only; review it before starting."
        );
    }
    catch (Exception ex)
        when (ex
                is IOException
                    or ArgumentException
                    or SqliteException
                    or JsonException
                    or UnauthorizedAccessException
        )
    {
        Console.Error.WriteLine(
            "Maintenance failed: "
                + ex.GetType().Name
                + ". Check the archive, permissions, size limit, schema compatibility and empty restore destination."
        );
        Environment.ExitCode = 1;
    }
    return;
}
var setupPath = Path.Combine(data, "setup-token");
builder
    .Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserContext, RequestUser>();
builder.Services.AddDbContext<MediaDb>(o =>
    o.UseSqlite(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(data, "media.db"),
            ForeignKeys = true,
        }.ToString()
    )
);
builder.Services.AddSingleton(
    new PathPolicy(
        builder.Configuration.GetSection("MediaServer:AllowedRoots").Get<string[]>() ?? []
    )
);
builder.Services.AddSingleton<IMediaProbe>(
    new Ffprobe(builder.Configuration["MediaServer:FfprobePath"] ?? "ffprobe")
);
builder.Services.AddSingleton<IPlaybackPlanner, DirectPlaybackPlanner>();
builder.Services.AddSingleton(
    new FfmpegRunner(builder.Configuration["MediaServer:FfmpegPath"] ?? "ffmpeg")
);
builder.Services.AddSingleton<EncoderDiscovery>();
var permittedEncoders =
    builder.Configuration.GetSection("MediaServer:HardwareEncoders").Get<string[]>() ?? [];
if (permittedEncoders.Any(x => !EncoderDiscovery.Encoders.Contains(x)))
    throw new InvalidOperationException("Unknown configured hardware encoder.");
var transcodeSettings = new TranscodeSettings(
    new[] { "libx264" }.Concat(permittedEncoders).Distinct().ToArray(),
    builder.Configuration["MediaServer:VaapiDevice"] ?? "/dev/dri/renderD128",
    builder.Configuration.GetValue<int?>("MediaServer:NvencGpu") ?? -1,
    builder.Configuration["MediaServer:QsvDevice"]
);
transcodeSettings.Validate();
builder.Services.AddSingleton(transcodeSettings);

builder.Services.AddSingleton(sp => new PlaybackSessions(
    data,
    sp.GetRequiredService<FfmpegRunner>(),
    sp.GetRequiredService<EncoderDiscovery>(),
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<TranscodeSettings>(),
    sp.GetRequiredService<IMediaProbe>()
));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlaybackSessions>());
builder.Services.AddSingleton<ScanQueue>();
builder.Services.AddSingleton<StreamRegistry>();
builder.Services.AddSingleton(new ResourceMonitor(data));
builder.Services.AddSingleton(
    new GpuMetrics(
        builder.Configuration.GetValue<bool>("MediaServer:EnableGpuMetrics"),
        builder.Configuration["MediaServer:NvidiaSmiPath"] ?? "nvidia-smi"
    )
);
builder.Services.AddSingleton<ScanScheduler>();
builder.Services.AddHostedService<LibraryMonitor>();
builder.Services.AddHostedService<ScanWorker>();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("MediaServer:SecureCookies")
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
});
builder
    .Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "MediaServer.Session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("MediaServer:SecureCookies")
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = false;
        o.Events.OnRedirectToLogin = c =>
        {
            c.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = c =>
        {
            c.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
        o.Events.OnValidatePrincipal = async c =>
        {
            var id = c.Principal?.FindFirstValue("sid");
            var db = c.HttpContext.RequestServices.GetRequiredService<MediaDb>();
            if (
                id is null
                || !await db.Sessions.AnyAsync(x =>
                    x.Id == id
                    && x.ExpiresAt > DateTime.UtcNow
                    && db.Users.Any(u =>
                        u.Id == x.UserId
                        && !u.Disabled
                        && u.Role == c.Principal!.FindFirstValue(ClaimTypes.Role)
                    )
                )
            )
                c.RejectPrincipal();
        };
    });
builder.Services.AddAuthorization(o => o.AddPolicy("admin", p => p.RequireRole("Admin")));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy(
        "auth",
        context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }
            )
    );
    o.AddPolicy(
        "playback",
        context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 6,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }
            )
    );
});
builder.Services.AddOpenApi("v1", o => o.ShouldInclude = _ => true);
builder.Services.AddOpenApi("client-v1", o => o.ShouldInclude = d => d.GroupName == "client-v1");
builder.Services.AddSignalR();
builder.Services.AddSingleton<IScanEvents, StatusEvents>();
var credentialGate = new SemaphoreSlim(1, 1);
var dummyHash = await Passwords.Hash("dummy-password-value");
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
    await db.Database.MigrateAsync();
    if (recovery)
    {
        Console.Write("New administrator password (12–256 characters): ");
        try
        {
            await CredentialRecovery.ResetAsync(db, CredentialRecovery.ReadPassword());
            Console.WriteLine(
                "Password changed. All administrator sessions revoked. Restart the server normally."
            );
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
        return;
    }
    if (!await db.Users.AnyAsync() && !File.Exists(setupPath))
    {
        var tokenOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            tokenOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var tokenFile = new FileStream(setupPath, tokenOptions);
        using var writer = new StreamWriter(tokenFile);
        writer.Write(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }
    if (await db.Users.AnyAsync() && File.Exists(setupPath))
        File.Delete(setupPath);
    // Migration SQL uses SQLite's ASCII lower(); normalize existing Unicode titles once with .NET.
    var searchIndex = Path.Combine(data, "search-index-v1");
    if (!File.Exists(searchIndex))
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        connection.CreateFunction("media_fold", (string title) => title.ToLowerInvariant());
        await db.Database.ExecuteSqlRawAsync("UPDATE Items SET SearchTitle=media_fold(Title)");
        await connection.CloseAsync();
        await File.WriteAllTextAsync(searchIndex, "1");
    }
}
app.Use(
    async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; media-src 'self' blob:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.Headers.CacheControl = "no-store";
        try
        {
            await next();
        }
        catch (AntiforgeryValidationException)
        {
            await Results
                .Problem("Refresh the page and try again.", statusCode: 400)
                .ExecuteAsync(context);
        }
        catch (ArgumentException ex)
        {
            await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            app.Logger.LogError(
                "Request {TraceId} failed ({ErrorType})",
                context.TraceIdentifier,
                ex.GetType().Name
            );
            await Results
                .Problem(
                    "An unexpected server error occurred.",
                    statusCode: 500,
                    extensions: new Dictionary<string, object?>
                    {
                        ["traceId"] = context.TraceIdentifier,
                    }
                )
                .ExecuteAsync(context);
        }
    }
);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.Use(
    async (context, next) =>
    {
        if (
            context.Request.Path.StartsWithSegments("/api")
            && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS")
        )
            await context
                .RequestServices.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(context);
        await next();
    }
);
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
MapApi(app.MapGroup("/api"));
MapApi(app.MapGroup("/api/v1").WithGroupName("client-v1"));
app.MapClientV1();
app.MapPlayback();
app.MapOperations(credentialGate);
app.MapFeatures();
void MapApi(RouteGroupBuilder root)
{
    root.MapGet(
        "/auth/csrf",
        (HttpContext ctx, IAntiforgery csrf) =>
            Results.Ok(new CsrfToken(csrf.GetAndStoreTokens(ctx).RequestToken!))
    );
    root.MapGet(
        "/setup",
        async (MediaDb db) => Results.Ok(new { required = !await db.Users.AnyAsync() })
    );
    root.MapPost(
            "/setup",
            async (SetupRequest input, MediaDb db) =>
            {
                await credentialGate.WaitAsync();
                try
                {
                    if (await db.Users.AnyAsync())
                        return Results.Conflict(new { message = "Server is already configured." });
                    var expectedToken = await File.ReadAllTextAsync(setupPath);
                    if (
                        input.Token is null
                        || !CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.UTF8.GetBytes(input.Token),
                            System.Text.Encoding.UTF8.GetBytes(expectedToken)
                        )
                    )
                        return Results.Unauthorized();
                    ValidateCredentials(input.Name, input.Password);
                    db.Users.Add(
                        new User
                        {
                            Name = input.Name.Trim(),
                            PasswordHash = await Passwords.Hash(input.Password),
                        }
                    );
                    try
                    {
                        await db.SaveChangesAsync();
                    }
                    catch (DbUpdateException)
                    {
                        return Results.Conflict();
                    }
                    File.Delete(setupPath);
                    return Results.Created(
                        "/auth/me",
                        new { message = "Administrator created. Sign in to continue." }
                    );
                }
                finally
                {
                    credentialGate.Release();
                }
            }
        )
        .RequireRateLimiting("auth");
    root.MapPost(
            "/auth/login",
            async (LoginRequest input, MediaDb db, HttpContext ctx) =>
            {
                await credentialGate.WaitAsync();
                try
                {
                    ValidateCredentials(input.Name, input.Password);
                    var user = await db.Users.SingleOrDefaultAsync(x => x.Name == input.Name);
                    // Derive even for unknown usernames to avoid a cheap account enumeration timing signal.
                    var valid = await Passwords.Verify(
                        input.Password,
                        user?.PasswordHash ?? dummyHash
                    );
                    if (user is null || user.Disabled || !valid)
                        return Results.Unauthorized();
                    await db
                        .Sessions.Where(x => x.ExpiresAt <= DateTime.UtcNow)
                        .ExecuteDeleteAsync();
                    if (input.DeviceName is { Length: > 64 })
                        throw new ArgumentException("Device name is too long.");
                    var session = new Session
                    {
                        Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                        UserId = user.Id,
                        DeviceName = input.DeviceName ?? "Web browser",
                        ExpiresAt = DateTime.UtcNow.AddDays(7),
                    };
                    db.Sessions.Add(session);
                    await db.SaveChangesAsync();
                    var claims = new[]
                    {
                        new Claim(
                            ClaimTypes.NameIdentifier,
                            user.Id.ToString(CultureInfo.InvariantCulture)
                        ),
                        new Claim(ClaimTypes.Name, user.Name),
                        new Claim(ClaimTypes.Role, user.Role),
                        new Claim("sid", session.Id),
                    };
                    await ctx.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(
                            new ClaimsIdentity(
                                claims,
                                CookieAuthenticationDefaults.AuthenticationScheme
                            )
                        )
                    );
                    return Results.Ok(new UserIdentity(user.Name, user.Role));
                }
                finally
                {
                    credentialGate.Release();
                }
            }
        )
        .Produces<UserIdentity>()
        .RequireRateLimiting("auth");
    root.MapPost(
            "/auth/logout",
            async (HttpContext ctx, MediaDb db) =>
            {
                var id = ctx.User.FindFirstValue("sid");
                await db.Sessions.Where(x => x.Id == id).ExecuteDeleteAsync();
                await ctx.SignOutAsync();
                return Results.NoContent();
            }
        )
        .RequireAuthorization();
    root.MapGet(
            "/auth/me",
            (ClaimsPrincipal u) =>
                new UserIdentity(u.Identity!.Name!, u.FindFirstValue(ClaimTypes.Role)!)
        )
        .RequireAuthorization();
    root.MapPost(
            "/auth/password",
            async (PasswordChange input, MediaDb db, HttpContext ctx) =>
            {
                ValidateCredentials(ctx.User.Identity!.Name!, input.NewPassword);
                if (
                    string.IsNullOrEmpty(input.CurrentPassword)
                    || input.CurrentPassword.Length > 256
                )
                    throw new ArgumentException("Enter your current password.");
                await credentialGate.WaitAsync();
                try
                {
                    var uid = UserId(ctx.User);
                    var user = await db.Users.FindAsync(uid);
                    if (
                        user is null
                        || !await Passwords.Verify(input.CurrentPassword, user.PasswordHash)
                    )
                        return Results.Problem("Current password is incorrect.", statusCode: 400);
                    var hash = await Passwords.Hash(input.NewPassword);
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    user.PasswordHash = hash;
                    await db.SaveChangesAsync();
                    await db.Sessions.Where(x => x.UserId == uid).ExecuteDeleteAsync();
                    await transaction.CommitAsync();
                    await ctx.SignOutAsync();
                    return Results.NoContent();
                }
                finally
                {
                    credentialGate.Release();
                }
            }
        )
        .RequireAuthorization()
        .RequireRateLimiting("auth");
    var api = root.MapGroup("").RequireAuthorization();
    api.MapGet(
        "/status",
        async (MediaDb db) =>
            new
            {
                version = ClientV1.ServerVersion,
                mediaCount = await db.Items.CountAsync(),
                libraryCount = await db.Libraries.CountAsync(),
                streaming = "Direct Play + bounded HLS (FFmpeg required)",
                transcoding = true,
            }
    );
    api.MapGet(
            "/libraries",
            async (MediaDb db) => await db.Libraries.OrderBy(x => x.Name).ToListAsync()
        )
        .RequireAuthorization("admin");
    api.MapPost(
            "/libraries",
            async (LibraryRequest input, MediaDb db, PathPolicy policy) =>
            {
                if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100)
                    throw new ArgumentException("Library name must be 1–100 characters.");
                if (input.Kind is not ("movies" or "tv"))
                    throw new ArgumentException("Choose movies or tv.");
                var path = policy.ValidateDirectory(input.Path);
                if (await db.Libraries.AnyAsync(x => x.Path == path))
                    return Results.Conflict(new { message = "Directory already configured." });
                var library = new Library
                {
                    Name = input.Name.Trim(),
                    Path = path,
                    Kind = input.Kind,
                    WatchEnabled = input.WatchEnabled,
                    ComputeHashes = input.ComputeHashes,
                };
                db.Libraries.Add(library);
                await db.SaveChangesAsync();
                return Results.Created($"/api/libraries/{library.Id}", library);
            }
        )
        .RequireAuthorization("admin");
    api.MapPost(
            "/libraries/{id:int}/scan",
            async (int id, MediaDb db, ScanScheduler scheduler, CancellationToken ct) =>
            {
                if (!await db.Libraries.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                var job = await scheduler.EnqueueAsync(id, ct);
                if (job is null)
                    return Results.Conflict(new { message = "A scan is already pending." });
                return job.State == "rejected"
                    ? Results.StatusCode(503)
                    : Results.Accepted($"/api/scans/{job.Id}", new { job.Id });
            }
        )
        .RequireAuthorization("admin");
    api.MapGet(
            "/scans/{id:int}",
            async (int id, MediaDb db) =>
                await db.Jobs.FindAsync(id) is { } job ? Results.Ok(job) : Results.NotFound()
        )
        .RequireAuthorization("admin");
    api.MapGet(
            "/scans/{id:int}/issues",
            async (int id, MediaDb db) =>
                await db
                    .ScanIssues.Where(x => x.ScanJobId == id)
                    .OrderBy(x => x.Id)
                    .Take(100)
                    .Select(x => new ScanIssueInfo(x.FileName, x.Code, x.CorrelationId))
                    .ToListAsync()
        )
        .RequireAuthorization("admin");
    api.MapGet(
            "/media/{id:int}/playback-info",
            async (
                int id,
                MediaDb db,
                PathPolicy policy,
                IPlaybackPlanner planner,
                HttpContext ctx
            ) =>
            {
                var item = await db.Items.FindAsync(id);
                if (item is null)
                    return Results.NotFound();
                var library = await db.Libraries.FindAsync(item.LibraryId);
                var available = false;
                try
                {
                    if (library is not null)
                    {
                        using var stream = policy.OpenRead(library.Path, item.RelativePath);
                        available = true;
                    }
                }
                catch (Exception ex)
                    when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                if (!available)
                    app.Logger.LogWarning(
                        "Media {MediaId} unavailable; reference {CorrelationId}",
                        id,
                        ctx.TraceIdentifier
                    );
                return Results.Ok(
                    new PlaybackInfo(
                        available,
                        available ? "ready" : "media_unavailable",
                        ctx.TraceIdentifier,
                        planner.Plan(item)
                    )
                );
            }
        )
        .Produces<PlaybackInfo>();
    api.MapGet(
            "/media",
            async (MediaDb db, int? offset, int? limit, string? search, int? libraryId) =>
                Results.Ok(
                    await db
                        .Items.AsNoTracking()
                        .Where(x =>
                            (
                                string.IsNullOrEmpty(search)
                                || x.Title.ToLower().Contains(search.ToLower())
                            ) && (!libraryId.HasValue || x.LibraryId == libraryId)
                        )
                        .OrderBy(x => x.Title)
                        .ThenBy(x => x.Id)
                        .Skip(Math.Max(0, offset ?? 0))
                        .Take(Math.Clamp(limit ?? 100, 1, 200))
                        .Select(x => new MediaSummary(
                            x.Id,
                            x.Title,
                            x.Duration,
                            x.VideoCodec,
                            x.AudioCodec,
                            x.Container,
                            x.Size,
                            x.LibraryId
                        ))
                        .ToListAsync()
                )
        )
        .Produces<MediaSummary[]>();
    api.MapGet(
            "/media/{id:int}",
            async (int id, MediaDb db, IPlaybackPlanner planner) =>
                await db.Items.FindAsync(id) is { } item
                    ? Results.Ok(
                        new MediaDetails(
                            item.Id,
                            item.Title,
                            item.Duration,
                            item.VideoCodec,
                            item.AudioCodec,
                            item.Container,
                            item.Size,
                            planner.Plan(item),
                            item.ContentRating
                        )
                    )
                    : Results.NotFound()
        )
        .Produces<MediaDetails>();
    api.MapMethods(
        "/media/{id:int}/stream",
        ["GET", "HEAD"],
        async (
            int id,
            MediaDb db,
            PathPolicy policy,
            IPlaybackPlanner planner,
            StreamRegistry registry,
            ClaimsPrincipal user
        ) =>
        {
            var item = await db.Items.FindAsync(id);
            if (item is null)
                return Results.NotFound();
            var library = await db.Libraries.FindAsync(item.LibraryId);
            if (library is null)
                return Results.NotFound();
            try
            {
                var stream = policy.OpenRead(library.Path, item.RelativePath);
                return Results.File(
                    registry.Track(stream, UserId(user), item.Id),
                    planner.Plan(item).MimeType,
                    enableRangeProcessing: true
                );
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.NotFound();
            }
        }
    );
    api.MapGet(
        "/progress",
        async (MediaDb db, ClaimsPrincipal u, string? mediaIds) =>
        {
            var uid = UserId(u);
            int[]? ids = null;
            if (mediaIds is not null)
            {
                var parts = mediaIds.Split(',');
                if (parts.Length > 200 || parts.Any(x => !int.TryParse(x, out var id) || id < 1))
                    throw new ArgumentException("Invalid progress item IDs.");
                ids = parts.Select(int.Parse).ToArray();
            }
            return Results.Ok(
                await db
                    .Progress.Where(x =>
                        x.UserId == uid
                        && (ids == null || ids.Contains(x.MediaItemId))
                        && db.Items.Any(item => item.Id == x.MediaItemId)
                    )
                    .OrderByDescending(x => x.UpdatedAt)
                    .ToListAsync()
            );
        }
    );
    api.MapPut(
        "/media/{id:int}/progress",
        async (int id, ProgressRequest input, MediaDb db, ClaimsPrincipal u) =>
        {
            var item = await db.Items.FindAsync(id);
            if (item is null)
                return Results.NotFound();
            if (
                !double.IsFinite(input.Seconds)
                || input.Seconds < 0
                || (item.Duration > 0 && input.Seconds > item.Duration + 2)
            )
                throw new ArgumentException("Invalid playback position.");
            var uid = UserId(u);
            var updated = DateTime.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Progress (UserId, MediaItemId, Seconds, UpdatedAt, Revision, Completed) VALUES ({uid}, {id}, {input.Seconds}, {updated}, 1, {item.Duration > 0 && input.Seconds >= item.Duration * .95}) ON CONFLICT(UserId, MediaItemId) DO UPDATE SET Seconds=excluded.Seconds, UpdatedAt=excluded.UpdatedAt, Revision=Progress.Revision+1, Completed=excluded.Completed"
            );
            await HistoryWriter.RecordAsync(
                db,
                uid,
                id,
                input.Seconds,
                item.Duration > 0 && input.Seconds >= item.Duration * .95,
                updated
            );
            await transaction.CommitAsync();
            return Results.NoContent();
        }
    );
}
app.MapOpenApi().RequireAuthorization("admin");
app.MapHub<StatusHub>("/hubs/status").RequireAuthorization("admin");
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallback("/api/{**path}", () => Results.NotFound()).RequireAuthorization();
app.MapFallbackToFile("index.html");
app.Run();
static int UserId(ClaimsPrincipal u) =>
    int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
static void ValidateCredentials(string name, string password)
{
    if (
        string.IsNullOrWhiteSpace(name)
        || name.Trim().Length > 64
        || string.IsNullOrEmpty(password)
        || password.Length is < 12 or > 256
    )
        throw new ArgumentException("Username: 1–64 characters. Password: 12–256 characters.");
}

public record ScanIssueInfo(string FileName, string Code, string CorrelationId);

public record PlaybackInfo(
    bool Available,
    string Code,
    string CorrelationId,
    PlaybackPlan Playback
);

public record PasswordChange(string CurrentPassword, string NewPassword);

public record SetupRequest(string Name, string Password, string Token);

public record LoginRequest(string Name, string Password, string? DeviceName = null);

public record LibraryRequest(
    string Name,
    string Path,
    string Kind = "movies",
    bool WatchEnabled = false,
    bool ComputeHashes = false
);

public record ProgressRequest(double Seconds);

public class StatusHub : Microsoft.AspNetCore.SignalR.Hub { }

public partial class Program { }

public class StatusEvents(Microsoft.AspNetCore.SignalR.IHubContext<StatusHub> hub) : IScanEvents
{
    public Task PublishAsync(ScanJob job, CancellationToken ct) =>
        Microsoft.AspNetCore.SignalR.ClientProxyExtensions.SendAsync(
            hub.Clients.All,
            "scanUpdated",
            new
            {
                job.Id,
                job.LibraryId,
                job.State,
                job.Discovered,
                job.Failed,
            },
            ct
        );
}
