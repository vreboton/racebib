# Platform administration and security

## Role model

`SystemAdministrator` is a global ASP.NET Core Identity role. It grants platform administration through `/api/v1/platform/*`; it is separate from `OrganizerMembership.Role`. Organizer event management remains scoped to the organizer membership and is enforced by backend authorization. A suspended or inactive organizer cannot be accessed through membership authorization. Reactivation restores access through existing active memberships.

## Platform administration API

Every platform administration endpoint except the organizer audit endpoint requires the global `SystemAdministrator` role:

- `GET /api/v1/platform/dashboard`
- `GET /api/v1/platform/users?search=&page=&pageSize=` and `GET /users/{userId}`
- `PUT /api/v1/platform/users/{userId}/platform-admin` with `{ "isAdministrator": true|false }`
- `PUT /api/v1/platform/users/{userId}/status` with `{ "isActive": true|false }`
- `GET /api/v1/platform/organizers?search=&page=&pageSize=`
- `GET /api/v1/platform/organizers/{organizerId}` plus `/members` and `/events`
- `PUT /api/v1/platform/organizers/{organizerId}/status` with `{ "status": "Active|Suspended|Inactive" }`
- `GET /api/v1/platform/audit` with actor, action, entity, search, date and pagination filters

Organizer administrators may read only their own organizer audit at `GET /api/v1/platform/organizers/{organizerId}/audit`. The endpoint checks active EventAdministrator membership server-side; race directors and other organizers are denied. Platform roles are never assigned through organizer membership APIs. The last active platform administrator cannot be revoked/deactivated, and an administrator cannot deactivate their own account.

## Organizer lifecycle

Organizer state is represented by `IsActive` and `IsSuspended`: active means `true/false`, suspended means `true/true`, and inactive means `false/false`. Lifecycle changes preserve memberships, events, runners and registrations. Suspended and inactive organizers are excluded from ordinary organizer/event access. Platform administrators retain global administration access.

## Audit history

`AuditRecords` stores actor identity, action, target, optional organizer, UTC time and sanitized JSON metadata. Business changes and their audit records are saved through the same EF Core unit of work (and explicit transaction for platform role/account operations). The API exposes read-only, paginated audit queries; there are no audit creation, update or delete endpoints. Metadata is allowlisted to known role/status values under `role`, `from` and `to`; unknown keys, values and secret-like fields are dropped before serialization. Actor identity is stored separately. Avoid putting sensitive values into audit metadata.

Audit events currently cover organizer creation and lifecycle, platform role/account changes, membership changes, invitation create/accept/revoke/expiry, logout revocation and account lockout. Audit storage is not a tamper-proof external ledger; database administrators can modify database rows. Production deployments that require stronger evidentiary guarantees should export records to restricted append-only storage.

## Security and operations

JWT authentication remains bearer-token based; revoked JWT IDs are rejected by the validation event. Identity login uses the configured five-attempt lockout. Invitation tokens are hashed at rest and single-use. Frontend navigation is a convenience only; APIs remain the authorization boundary. Apply `AddPlatformAdministrationAudit` after reviewing its SQL and confirming the target database. Back up the local database before applying migrations and never reset the MySQL volume to install this schema.

The supplied Compose topology binds API and web ports to loopback and serves HTTP locally. HTTPS redirect middleware is enabled only when `ASPNETCORE_HTTPS_PORT` or `HttpsRedirection:HttpsPort` is configured, avoiding a misleading redirect warning when the API has no HTTPS listener. Public deployments must terminate TLS at a trusted reverse proxy and configure forwarded headers and HTTPS redirect behavior for that proxy.

## Live Docker integration test

`RaceTiming.Api.Tests.LiveDockerIntegrationTests` sends HTTP requests to a running API connected to relational data. It is skipped in ordinary offline test runs. To enable it, set `RACEBIB_INTEGRATION_BASE_URL`, `RACEBIB_INTEGRATION_ADMIN_EMAIL`, `RACEBIB_INTEGRATION_ADMIN_PASSWORD`, `RACEBIB_INTEGRATION_ORGANIZER_ID`, `RACEBIB_INTEGRATION_MEMBER_ID`, `RACEBIB_INTEGRATION_MEMBER_TOKEN`, and `RACEBIB_INTEGRATION_EVENT_ID` in the test process. Optionally set `RACEBIB_INTEGRATION_OTHER_ORGANIZER_EVENT_ID` to test cross-organizer denial. Run `dotnet test backend/RaceTiming.Api.Tests --filter Category=LiveDocker`. Use a clearly labeled test organizer and member; the test restores organizer status and membership/platform roles and revokes only its own login token.
