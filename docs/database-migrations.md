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

## Current status (2026-10-30)

The repository currently has no committed EF Core migrations. The race/category/wave domain refactor has changed the model, so the initial migration must be generated from this revised model. No database schema has been generated or applied as part of the source refactor commits.
