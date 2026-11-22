using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class ProvenanceTests
{
    private static async Task Seed(Server server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        db.Libraries.AddRange(
            new Library
            {
                Id = 1,
                Name = "Family",
                Path = server.MediaPath,
            },
            new Library
            {
                Id = 2,
                Name = "Private",
                Path = server.MediaPath,
            }
        );
        for (var id = 1; id <= 6; id++)
        {
            db.Items.Add(
                new MediaItem
                {
                    Id = id,
                    LibraryId = id == 2 ? 2 : 1,
                    Title = id == 6 ? "Embedded title" : "Title " + id,
                    RelativePath = id + ".webm",
                    Duration = 120,
                    Size = 100,
                    ContentRating = id == 3 ? 18 : 10,
                    IsMissing = id == 4,
                    PendingImport = id == 5,
                    MetadataJson =
                        id == 6
                            ? JsonSerializer.Serialize(
                                new MovieMetadata(
                                    "Embedded title",
                                    "Description",
                                    null,
                                    null,
                                    null,
                                    []
                                )
                            )
                            : "null",
                }
            );
            await File.WriteAllBytesAsync(
                Path.Combine(server.MediaPath, id + ".webm"),
                new byte[100]
            );
        }
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> Viewer(
        Server server,
        HttpClient admin,
        string name = "viewer"
    )
    {
        await admin.PostAsJsonAsync(
            "/api/users",
            new
            {
                name,
                password = "provenance-fixture-password",
                libraries = new[] { 1 },
                maximumRating = 13,
            }
        );
        var viewer = server.CreateClient();
        await Server.Csrf(viewer);
        await viewer.PostAsJsonAsync(
            "/api/auth/login",
            new { name, password = "provenance-fixture-password" }
        );
        await Server.Csrf(viewer);
        return viewer;
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task PublicExplanationIsHonestAndNeverContainsPrivateDiagnostics(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var viewer = await Viewer(server, admin);
        var response = await viewer.GetStringAsync(prefix + "/media/1/provenance");
        Assert.DoesNotContain(server.MediaPath, response);
        Assert.DoesNotContain("relativePath", response);
        Assert.DoesNotContain("fingerprint", response);
        var explanation = JsonSerializer.Deserialize<LibraryProvenanceView>(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
        Assert.Equal("Filename-derived title", explanation.Identification);
        Assert.Equal("Family", explanation.Library);
        Assert.Null(explanation.AddedAt);
        Assert.Null(explanation.LastScannedAt);
        Assert.True(explanation.CanReport);
        Assert.Equal(
            "Metadata embedded in the media file",
            (
                await viewer.GetFromJsonAsync<LibraryProvenanceView>(prefix + "/media/6/provenance")
            )!.Identification
        );
        foreach (var id in new[] { 2, 3, 4, 5 })
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await viewer.GetAsync(prefix + $"/media/{id}/provenance")).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (
                    await viewer.PostAsJsonAsync(
                        prefix + $"/media/{id}/match-report",
                        new MatchReportInput("title", "Wrong")
                    )
                ).StatusCode
            );
        }
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync(prefix + "/admin/media/1/provenance")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync(prefix + "/admin/match-reports")).StatusCode
        );
        var diagnostics = (
            await admin.GetFromJsonAsync<ProvenanceDiagnostics>(
                prefix + "/admin/media/1/provenance"
            )
        )!;
        Assert.Equal(Path.Combine(server.MediaPath, "1.webm"), diagnostics.FilePath);
        Assert.Equal("1.webm", diagnostics.RelativePath);
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(prefix + "/media/1/provenance")).StatusCode
        );
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task ReportsDeduplicateStayPrivateAndRequireRevisionCheckedAdminReview(
        string prefix
    )
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var viewer = await Viewer(server, admin);
        using var other = await Viewer(server, admin, "other");
        var path = prefix + "/media/1/match-report";
        var request = new MatchReportInput("artwork", "This is a different film.");
        var reports = await Task.WhenAll(
            viewer.PostAsJsonAsync(path, request),
            viewer.PostAsJsonAsync(path, request)
        );
        Assert.All(reports, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var first = (await reports[0].Content.ReadFromJsonAsync<MatchReportView>())!;
        Assert.Equal(first.Id, (await reports[1].Content.ReadFromJsonAsync<MatchReportView>())!.Id);
        Assert.Null(
            (
                await other.GetFromJsonAsync<LibraryProvenanceView>(prefix + "/media/1/provenance")
            )!.Report
        );
        var queue = (
            await admin.GetFromJsonAsync<MatchReportPage>(prefix + "/admin/match-reports")
        )!;
        Assert.Equal("viewer", Assert.Single(queue.Items).Reporter);
        Assert.Equal(first.Id, queue.Items[0].Id);
        var review = prefix + $"/admin/match-reports/{first.Id}/review";
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await viewer.PostAsJsonAsync(
                    review,
                    new MatchReportReview("Resolved", first.Revision)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync(review, new MatchReportReview("Resolved", 99))).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PostAsJsonAsync(
                    review,
                    new MatchReportReview("Dismissed", first.Revision)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.PostAsJsonAsync(
                    review,
                    new MatchReportReview("Resolved", first.Revision)
                )
            ).StatusCode
        );
        Assert.Equal(
            "Dismissed",
            (await viewer.GetFromJsonAsync<LibraryProvenanceView>(prefix + "/media/1/provenance"))!
                .Report!
                .State
        );
        Assert.Empty(
            (await admin.GetFromJsonAsync<MatchReportPage>(prefix + "/admin/match-reports"))!.Items
        );
        Assert.Single(
            (
                await admin.GetFromJsonAsync<MatchReportPage>(
                    prefix + "/admin/match-reports?closed=true"
                )
            )!.Items
        );
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await viewer.PostAsJsonAsync(path, request)).StatusCode
        );
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            Assert.Single(db.MatchReports);
            db.MatchReports.Single().UpdatedAt = DateTime.UtcNow.AddMinutes(-6);
            await db.SaveChangesAsync();
        }
        var reopened = (
            await (
                await viewer.PostAsJsonAsync(
                    path,
                    new MatchReportInput("episode", "Wrong episode number")
                )
            ).Content.ReadFromJsonAsync<MatchReportView>()
        )!;
        Assert.Equal(first.Id, reopened.Id);
        Assert.Equal("Open", reopened.State);
        Assert.True(reopened.Revision > first.Revision);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await viewer.PostAsJsonAsync(path, new MatchReportInput("secret", null))).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await viewer.PostAsJsonAsync(
                    path,
                    new MatchReportInput("title", new string('a', 501))
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.GetAsync(prefix + "/admin/match-reports?offset=-1")).StatusCode
        );
    }

    [Fact]
    public async Task FeatureDisableAndGuestScopeRetainReportsWithoutAllowingWrites()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var viewer = await Viewer(server, admin);
        var report = (
            await (
                await viewer.PostAsJsonAsync(
                    "/api/media/1/match-report",
                    new MatchReportInput("title", null)
                )
            ).Content.ReadFromJsonAsync<MatchReportView>()
        )!;
        await admin.PutAsJsonAsync(
            "/api/features/library-provenance",
            new FeaturePolicyInput(false)
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await viewer.GetAsync("/api/media/1/provenance")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await viewer.PostAsJsonAsync(
                    "/api/media/1/match-report",
                    new MatchReportInput("title", null)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await admin.GetAsync("/api/admin/match-reports")).StatusCode
        );
        await admin.PutAsJsonAsync(
            "/api/features/library-provenance",
            new FeaturePolicyInput(true)
        );
        Assert.Equal(
            report.Id,
            (await viewer.GetFromJsonAsync<LibraryProvenanceView>("/api/media/1/provenance"))!
                .Report!
                .Id
        );
        await viewer.PutAsJsonAsync(
            "/api/preferences/features",
            new FeatureVisibilityInput(["library-provenance"])
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await viewer.GetAsync("/api/media/1/provenance")).StatusCode
        );
        await admin.PutAsJsonAsync(
            "/api/experiments/guests",
            new { enabled = true, libraries = new[] { 1 } }
        );
        using var guest = server.CreateClient();
        await Server.Csrf(guest);
        await guest.PostAsync("/api/auth/guest", null);
        await Server.Csrf(guest);
        Assert.False(
            (
                await guest.GetFromJsonAsync<LibraryProvenanceView>("/api/media/1/provenance")
            )!.CanReport
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync("/api/media/2/provenance")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await guest.PostAsJsonAsync(
                    "/api/media/1/match-report",
                    new MatchReportInput("title", null)
                )
            ).StatusCode
        );
    }
}
