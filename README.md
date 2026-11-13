# RaceBib

RaceBib is a multi-organizer race event management and race operations platform for road races, trail races, marathons, and ultramarathons.

## Milestone 1: Event Management System

The first milestone establishes organizer-scoped event configuration, race categories, courses, checkpoints, runners, registrations, authentication and the administration interface.

The Checkpoint Timing PWA and RFID Reader Application are separate future applications. Timing capture, offline synchronization and RFID hardware integration are explicitly out of scope for Milestone 1.

## Technology

- Backend: ASP.NET Core Web API (.NET 8), C#, EF Core, MySQL, JWT, Swagger.
- Frontend: Vue 3, TypeScript, Vite, Vuetify 3, Vue Router, Pinia, Axios.
- Deployment: Docker Compose with API, web and MySQL services.

## Repository layout

```
backend/
  RaceTiming.Api/
  RaceTiming.Application/
  RaceTiming.Domain/
  RaceTiming.Infrastructure/
  RaceTiming.sln
frontend/
  race-timing-web/
docker/
docs/
docker-compose.yml
.env.example
```

## Local setup

1. Install .NET 8 SDK, Node.js 22+, npm and Docker Desktop.
2. Copy `.env.example` to `.env`.
3. Replace all sample passwords and the JWT signing key with strong random values. Do not commit `.env`.
4. Start the containers:

   ```bash
   docker compose up --build
   ```

5. Frontend: http://localhost:8080
6. API health: http://localhost:5080/health
7. Swagger is enabled in Development at http://localhost:5080/swagger.

The current development branch contains Milestone 1 event management and Milestones 2.1–2.3 organizer security, member management, invitations and platform administration. Review [organizer member management](docs/organizer-membership.md), [platform administration and security](docs/platform-administration.md) and [database migrations](docs/database-migrations.md) for API, role, audit and schema details. Production deployment still requires environment-specific SMTP, HTTPS, secret, and migration setup.

## Development

Backend:

```bash
cd backend
dotnet restore RaceTiming.sln
dotnet build RaceTiming.sln
```

Frontend:

```bash
cd frontend/race-timing-web
npm install
npm run dev
```

See [architecture](docs/architecture.md) and [development plan](docs/development-plan.md).
