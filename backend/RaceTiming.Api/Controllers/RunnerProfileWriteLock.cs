using Microsoft.EntityFrameworkCore;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

internal static class RunnerProfileWriteLock
{
    internal static async Task Acquire(RaceTimingDbContext db, Guid organizerId, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;

        // Registration writers already hold the event lock. Profile-only writers lock only the organizer.
        await db.Organizers.FromSqlInterpolated($"SELECT * FROM Organizers WHERE Id = {organizerId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
    }
}
