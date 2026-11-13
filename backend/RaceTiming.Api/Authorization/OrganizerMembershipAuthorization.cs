using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Authorization;

public sealed class OrganizerMembershipAuthorization(RaceTimingDbContext db) : IOrganizerMembershipAuthorization
{
    public Task<bool> CanAccessAsync(ClaimsPrincipal principal, Guid organizerId, bool requireManagement, CancellationToken cancellationToken)
    {
        if (principal.IsInRole(AppRoles.SystemAdministrator))
            return Task.FromResult(true);

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Task.FromResult(false);

        var query = db.OrganizerMemberships.AsNoTracking()
            .Where(x => x.OrganizerId == organizerId && x.UserId == userId && x.IsActive && x.Organizer.IsActive && !x.Organizer.IsSuspended);
        if (requireManagement)
            query = query.Where(x => x.Role == OrganizerRole.EventAdministrator || x.Role == OrganizerRole.RaceDirector);

        return query.AnyAsync(cancellationToken);
    }
}
