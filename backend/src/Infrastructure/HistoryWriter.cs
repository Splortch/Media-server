using Microsoft.EntityFrameworkCore;

namespace MediaServer.Infrastructure;

public static class HistoryWriter
{
    public static Task RecordAsync(
        MediaDb db,
        int user,
        int media,
        double seconds,
        bool completed,
        DateTime at
    ) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO History (UserId,MediaItemId,Seconds,LastPlayedAt,CompletedAt) VALUES ({user},{media},{seconds},{at},{(completed ? at : (DateTime?)null)}) ON CONFLICT(UserId,MediaItemId) DO UPDATE SET Seconds=excluded.Seconds,LastPlayedAt=excluded.LastPlayedAt,CompletedAt=COALESCE(excluded.CompletedAt,History.CompletedAt) WHERE History.LastPlayedAt <= excluded.LastPlayedAt"
        );
}
