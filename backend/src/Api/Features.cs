using System.Security.Claims;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

public record FeatureCapability(
    string Key,
    string Name,
    bool Enabled,
    bool Visible,
    bool Hidden,
    bool ServerConfigurable
);

public record FeatureState(FeatureCapability[] Features);

public record FeaturePolicyInput(bool? Enabled);

public record FeatureVisibilityInput(string[]? Hidden);

public static class Features
{
    private sealed record Definition(string Key, string Name, bool ServerConfigurable);

    private static readonly Definition[] Registry =
    [
        new("smart-collections", "Smart collections", true),
        new("watch-history", "Watch history navigation", false),
    ];
    private static string[] Keys => Registry.Select(x => x.Key).ToArray();

    public static async Task<bool> Enabled(MediaDb db, string key) =>
        !await db.FeaturePolicies.AnyAsync(x => x.Key == key && !x.Enabled);

    private static async Task<FeatureState> State(MediaDb db, ClaimsPrincipal user)
    {
        var uid = Operations.Uid(user);
        var disabled = await db
            .FeaturePolicies.Where(x => !x.Enabled)
            .Select(x => x.Key)
            .ToArrayAsync();
        var hidden = await db
            .FeatureVisibility.Where(x => x.UserId == uid)
            .Select(x => x.Key)
            .ToArrayAsync();
        return new FeatureState(
            Registry
                .Select(feature => new FeatureCapability(
                    feature.Key,
                    feature.Name,
                    !disabled.Contains(feature.Key),
                    !disabled.Contains(feature.Key) && !hidden.Contains(feature.Key),
                    hidden.Contains(feature.Key),
                    feature.ServerConfigurable
                ))
                .ToArray()
        );
    }

    public static void MapFeatures(this WebApplication app)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var api = app.MapGroup(prefix).RequireAuthorization();
            if (prefix.EndsWith("v1", StringComparison.Ordinal))
                api.WithGroupName("client-v1");
            api.MapGet("/features", State);
            api.MapPut(
                    "/features/{key}",
                    async (
                        string key,
                        FeaturePolicyInput input,
                        MediaDb db,
                        ClaimsPrincipal user
                    ) =>
                    {
                        if (
                            input.Enabled is null
                            || !Registry.Any(x => x.Key == key && x.ServerConfigurable)
                        )
                            throw new ArgumentException("This feature has no server-wide switch.");
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"INSERT INTO FeaturePolicies (Key, Enabled) VALUES ({key}, {input.Enabled.Value}) ON CONFLICT(Key) DO UPDATE SET Enabled = excluded.Enabled"
                        );
                        return await State(db, user);
                    }
                )
                .RequireAuthorization("admin");
            api.MapPut(
                "/preferences/features",
                async (FeatureVisibilityInput input, MediaDb db, ClaimsPrincipal user) =>
                {
                    if (
                        input.Hidden is null
                        || input.Hidden.Length > Keys.Length
                        || input.Hidden.Distinct().Count() != input.Hidden.Length
                        || input.Hidden.Any(x => !Keys.Contains(x))
                    )
                        throw new ArgumentException("Choose only supported optional features.");
                    var uid = Operations.Uid(user);
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    await db.FeatureVisibility.Where(x => x.UserId == uid).ExecuteDeleteAsync();
                    db.FeatureVisibility.AddRange(
                        input.Hidden.Select(key => new FeatureVisibility
                        {
                            UserId = uid,
                            Key = key,
                        })
                    );
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return await State(db, user);
                }
            );
            api.MapDelete(
                "/preferences/features",
                async (MediaDb db, ClaimsPrincipal user) =>
                {
                    var uid = Operations.Uid(user);
                    await db.FeatureVisibility.Where(x => x.UserId == uid).ExecuteDeleteAsync();
                    return await State(db, user);
                }
            );
        }
    }
}
