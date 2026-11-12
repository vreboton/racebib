# Organizer member management

Organizer membership roles are distinct from platform Identity roles. `SystemAdministrator` is a global platform role. `EventAdministrator` and `RaceDirector` are organizer membership roles. Event Administrators and System Administrators can administer memberships; Race Directors can manage events and race operations but cannot change organizer membership.

## API

All paths use `/api/v1`.

| Method and path | Access | Purpose |
| --- | --- | --- |
| `GET /organizers/{organizerId}/members?search=&page=1&pageSize=20` | Event Administrator or System Administrator | Paginated member listing |
| `GET /organizers/{organizerId}/members/user-search?query=` | Same | Search active RaceBib users not already members (minimum 3 characters) |
| `POST /organizers/{organizerId}/members` | Same | Add existing user; body `{ "email": "...", "role": "RaceDirector" }` |
| `PUT /organizers/{organizerId}/members/{membershipId}/role` | Same | Change role; body `{ "role": "EventAdministrator" }` |
| `PUT /organizers/{organizerId}/members/{membershipId}/status?active=false` | Same | Deactivate or reactivate membership |
| `GET /organizers/{organizerId}/invitations` | Same | List invitation lifecycle status |
| `POST /organizers/{organizerId}/invitations` | Same | Create and email invitation; body `{ "email": "...", "role": "RaceDirector" }` |
| `POST /organizers/{organizerId}/invitations/{id}/resend` | Same | Rotate token and resend a pending invitation |
| `POST /organizers/{organizerId}/invitations/{id}/revoke` | Same | Revoke pending invitation |
| `POST /invitations/accept` | Authenticated account with matching confirmed email | Accept invitation; body `{ "token": "..." }` |
| `POST /invitations/reject` | Authenticated account with matching email | Reject invitation |
| `POST /invitations/register` | Anonymous, rate limited | Create account using invitation email/token and accept; body includes `token`, `email`, `displayName`, `password` |

Member list response shape is `{ items, page, pageSize, total }`. Search results are capped at 20. Role assignment is limited to `EventAdministrator` and `RaceDirector`. A deactivated membership immediately stops satisfying backend organizer authorization checks.

## Invitation lifecycle and security

Tokens contain 256 bits of cryptographic randomness. Only a SHA-256 token hash is stored. Acceptance checks invitation state and expiration, matches the normalized email, then atomically consumes the invitation and creates or reactivates its membership in a database transaction. Registration confirms ownership of the invitation email by requiring its token and creates no platform role. Expiration defaults to seven days and is configurable from 1 to 30 days. Repeated or revoked tokens are rejected.

Public acceptance and registration routes have an IP-based limit of 10 requests per minute. User search and membership management require organizer administrator access. API responses and application logs do not expose tokens in production. Development without SMTP returns an invitation link only in the Development response; do not expose that development endpoint outside a trusted local environment.

## SMTP and local delivery

Set `APPLICATION_BASE_URL`, `SMTP_HOST`, `SMTP_PORT`, `SMTP_ENABLE_SSL`, `SMTP_FROM`, and optionally `SMTP_USERNAME` and `SMTP_PASSWORD` in the runtime environment. Keep SMTP credentials in an untracked `.env` or secret store. In Development only, if `SMTP_HOST` is empty, the create/resend response includes `developmentAcceptanceUrl` for local manual testing. Production requires SMTP; delivery failures return HTTP 502 without writing the token to logs.

## Database migration

The invitation table adds organizer and creator foreign keys, token hash, status, expiry, UTC timestamps and indexes. A nullable unique active-invitation key enforces one pending invitation per organizer and normalized email while retaining history; application status transitions clear the key when an invitation is accepted, rejected, revoked, or expired. Generate and review the EF migration before applying it. Back up the development database first; do not reset or remove the existing MySQL volume. Apply with:

```powershell
dotnet ef database update --project backend/RaceTiming.Infrastructure --startup-project backend/RaceTiming.Api
```
