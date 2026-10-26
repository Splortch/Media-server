using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5050");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
var data = Path.GetFullPath(builder.Configuration["MediaServer:DataDirectory"] ?? "data");
Directory.CreateDirectory(data);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(
        data,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
    );
var setupPath = Path.Combine(data, "setup-token");
if (!File.Exists(setupPath))
{
    using var tokenFile = new FileStream(
        setupPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None
    );
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(setupPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    using var writer = new StreamWriter(tokenFile);
    writer.Write(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
}
builder
    .Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")));
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
builder.Services.AddSingleton<ScanQueue>();
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
                || !await db.Sessions.AnyAsync(x => x.Id == id && x.ExpiresAt > DateTime.UtcNow)
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
});
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IScanEvents, StatusEvents>();
var dummyHash = await Passwords.Hash("dummy-password-value");
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<MediaDb>().Database.MigrateAsync();
app.Use(
    async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; media-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
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
app.UseRateLimiter();
app.UseAuthentication();
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
app.MapGet(
    "/api/auth/csrf",
    (HttpContext ctx, IAntiforgery csrf) =>
        Results.Ok(new { token = csrf.GetAndStoreTokens(ctx).RequestToken })
);
app.MapGet(
    "/api/setup",
    async (MediaDb db) => Results.Ok(new { required = !await db.Users.AnyAsync() })
);
app.MapPost(
        "/api/setup",
        async (SetupRequest input, MediaDb db) =>
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
            return Results.Created(
                "/api/auth/me",
                new { message = "Administrator created. Sign in to continue." }
            );
        }
    )
    .RequireRateLimiting("auth");
app.MapPost(
        "/api/auth/login",
        async (LoginRequest input, MediaDb db, HttpContext ctx) =>
        {
            ValidateCredentials(input.Name, input.Password);
            var user = await db.Users.SingleOrDefaultAsync(x => x.Name == input.Name);
            // Derive even for unknown usernames to avoid a cheap account enumeration timing signal.
            var valid = await Passwords.Verify(input.Password, user?.PasswordHash ?? dummyHash);
            if (user is null || !valid)
                return Results.Unauthorized();
            await db.Sessions.Where(x => x.ExpiresAt <= DateTime.UtcNow).ExecuteDeleteAsync();
            var session = new Session
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                UserId = user.Id,
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
                    new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)
                )
            );
            return Results.Ok(new { user.Name, user.Role });
        }
    )
    .RequireRateLimiting("auth");
app.MapPost(
        "/api/auth/logout",
        async (HttpContext ctx, MediaDb db) =>
        {
            var id = ctx.User.FindFirstValue("sid");
            await db.Sessions.Where(x => x.Id == id).ExecuteDeleteAsync();
            await ctx.SignOutAsync();
            return Results.NoContent();
        }
    )
    .RequireAuthorization();
app.MapGet(
        "/api/auth/me",
        (ClaimsPrincipal u) =>
            new { name = u.Identity!.Name, role = u.FindFirstValue(ClaimTypes.Role) }
    )
    .RequireAuthorization();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet(
    "/status",
    async (MediaDb db) =>
        new
        {
            version = "0.1.0-alpha",
            mediaCount = await db.Items.CountAsync(),
            libraryCount = await db.Libraries.CountAsync(),
            streaming = "Direct Play",
            transcoding = false,
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
            var path = policy.ValidateDirectory(input.Path);
            if (await db.Libraries.AnyAsync(x => x.Path == path))
                return Results.Conflict(new { message = "Directory already configured." });
            var library = new Library { Name = input.Name.Trim(), Path = path };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            return Results.Created($"/api/libraries/{library.Id}", library);
        }
    )
    .RequireAuthorization("admin");
api.MapPost(
        "/libraries/{id:int}/scan",
        async (int id, MediaDb db, ScanQueue queue) =>
        {
            if (!await db.Libraries.AnyAsync(x => x.Id == id))
                return Results.NotFound();
            if (
                await db.Jobs.AnyAsync(x =>
                    x.LibraryId == id && (x.State == "queued" || x.State == "running")
                )
            )
                return Results.Conflict(new { message = "A scan is already pending." });
            var job = new ScanJob { LibraryId = id };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            if (!queue.Channel.Writer.TryWrite(job.Id))
            {
                job.State = "rejected";
                await db.SaveChangesAsync();
                return Results.StatusCode(503);
            }
            return Results.Accepted($"/api/scans/{job.Id}", new { job.Id });
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
    "/media",
    async (MediaDb db, int? offset, int? limit) =>
        Results.Ok(
            await db
                .Items.AsNoTracking()
                .OrderBy(x => x.Title)
                .Skip(Math.Max(0, offset ?? 0))
                .Take(Math.Clamp(limit ?? 100, 1, 200))
                .Select(x => new
                {
                    x.Id,
                    x.Title,
                    x.Duration,
                    x.VideoCodec,
                    x.AudioCodec,
                    x.Container,
                    x.Size,
                    x.LibraryId,
                })
                .ToListAsync()
        )
);
api.MapGet(
    "/media/{id:int}",
    async (int id, MediaDb db, IPlaybackPlanner planner) =>
        await db.Items.FindAsync(id) is { } item
            ? Results.Ok(
                new
                {
                    item.Id,
                    item.Title,
                    item.Duration,
                    item.VideoCodec,
                    item.AudioCodec,
                    item.Container,
                    item.Size,
                    playback = planner.Plan(item),
                }
            )
            : Results.NotFound()
);
api.MapMethods(
    "/media/{id:int}/stream",
    ["GET", "HEAD"],
    async (int id, MediaDb db, PathPolicy policy, IPlaybackPlanner planner) =>
    {
        var item = await db.Items.FindAsync(id);
        if (item is null)
            return Results.NotFound();
        var library = await db.Libraries.FindAsync(item.LibraryId);
        if (library is null)
            return Results.NotFound();
        try
        {
            var path = policy.Resolve(library.Path, item.RelativePath);
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.Asynchronous | FileOptions.RandomAccess
            );
            return Results.File(
                stream,
                planner.Plan(item).MimeType,
                enableRangeProcessing: true,
                lastModified: File.GetLastWriteTimeUtc(path)
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
    async (MediaDb db, ClaimsPrincipal u) =>
    {
        var uid = UserId(u);
        return Results.Ok(
            await db
                .Progress.Where(x => x.UserId == uid)
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
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Progress (UserId, MediaItemId, Seconds, UpdatedAt) VALUES ({uid}, {id}, {input.Seconds}, {updated}) ON CONFLICT(UserId, MediaItemId) DO UPDATE SET Seconds=excluded.Seconds, UpdatedAt=excluded.UpdatedAt"
        );
        return Results.NoContent();
    }
);
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

public record SetupRequest(string Name, string Password, string Token);

public record LoginRequest(string Name, string Password);

public record LibraryRequest(string Name, string Path);

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
