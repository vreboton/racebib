using System.Security.Claims;

namespace RaceTiming.Api.Authorization;

public interface IOrganizerMembershipAuthorization
{
    Task<bool> CanAccessAsync(ClaimsPrincipal principal, Guid organizerId, bool requireManagement, CancellationToken cancellationToken);
}
