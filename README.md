# RaceBib

RaceBib is a multi-organizer race event management and race operations platform for road races, trail races, marathons, and ultramarathons.

## Milestone 1

The initial milestone establishes the Event Management System: organizer-scoped event configuration, categories, courses, checkpoints, runners, registrations, authentication, and administration UI.

The Checkpoint Timing PWA and RFID Reader Application are separate future applications. Neither timing capture nor RFID hardware integration is included in this milestone.

## Architecture

- Backend: ASP.NET Core Web API (.NET 8), Clean Architecture, EF Core, MySQL, JWT.
- Frontend: Vue 3, TypeScript, Vite, Vuetify 3, Vue Router, Pinia, Axios.
- Deployment: Docker Compose with separate API, web, and MySQL services.

See `docs/architecture.md` and `docs/development-plan.md` for implementation decisions.
