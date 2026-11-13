# Database migrations

RaceBib uses EF Core migrations owned by `RaceTiming.Infrastructure`. The API project is the startup project because it supplies the MySQL provider configuration and application services.

## Create the initial migration

Run this only after the domain model for the initial release is stable:

```powershell
$env:ConnectionStrings__DefaultConnection = "Server=localhost;Port=3307;Database=racebib;User=app_user;Password=YOUR_PASSWORD;"
$env:Jwt__SigningKey = "YOUR_LOCAL_SECRET_AT_LEAST_32_BYTES"

dotnet ef migrations add InitialCreate `
  --project backend/RaceTiming.Infrastructure `
  --startup-project backend/RaceTiming.Api `
  --output-dir Persistence/Migrations
```

Review the generated migration and model snapshot. Commit both generated files with the model changes. Do not hand-write or manually edit the generated snapshot.

## Apply migrations locally

```powershell
dotnet ef database update `
  --project backend/RaceTiming.Infrastructure `
  --startup-project backend/RaceTiming.Api
```

## Add later migrations

Use a descriptive name and the same project/startup-project/output-dir arguments:

```powershell
dotnet ef migrations add AddDescriptiveChange `
  --project backend/RaceTiming.Infrastructure `
  --startup-project backend/RaceTiming.Api `
  --output-dir Persistence/Migrations
```

Inspect the generated Up/Down operations before applying. Never use `EnsureCreated` alongside migrations. Do not automatically apply migrations at API startup in production; deploy schema changes as an explicit release step.

## Current status

Committed migrations include `InitialCreate` and `AddOrganizerInvitations`. The current feature branch adds `AddPlatformAdministrationAudit`: it adds a non-null `Organizers.IsSuspended` field defaulting to false, and creates the append-only `AuditRecords` table with optional organizer FK (`SET NULL`), UTC timestamp, JSON metadata and query indexes. It preserves existing organizer, membership, event and registration rows; no existing table or column is dropped or rewritten. Review the generated migration and snapshot before applying it. After review, apply with `dotnet ef database update` against the intended local database; do not reset or recreate the MySQL volume.

Before applying to an existing local database, check the migration SQL and current migration history, back up the database, then run `dotnet ef database update` using the connection string for that environment. Do not reset volumes or use `EnsureCreated` against a migrated database.
