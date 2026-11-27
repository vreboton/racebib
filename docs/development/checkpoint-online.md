# Milestone 8.1 and 8.2 implementation plan

Baseline: main at f151ca4497452d35e77ef2b75c47e5ce42cea2bb. This page records the original plan and the online rehearsal. See [offline checkpoint capture](checkpoint-offline.md) for current operating and recovery procedures.

## Architecture audit

Race configuration owns ordered course visits. Timing Setup already creates physical checkpoints, maps occurrences, grants or revokes station assignments, arms races, admits late roster entries, and declares race or wave starts. Race Control reviews accepted evidence and official decisions. Setup mutations lack a manager UI and setup read model. Existing organizer invitations provision administrators and directors, so a station-only account invitation is needed to onboard first-time operators without granting organizer access.

The shared API already verifies current assignment generation, frozen bib ownership, occurrence scope, and idempotency. Repeated physical visits can be submitted without an occurrence for later review or explicitly selected before submission. Observed and received instants are separate. Add an authenticated server time probe; preserve the device reading as evidence even when clock quality is poor.

## Implementation

1. Add read-only manager setup data and exact-email account lookup. Expose checkpoint creation and mapping, assignment and revocation, arming, late roster admission, and gun declarations in Race Control. Reuse organizer invitation delivery for checkpoint accounts without adding organizer membership.
2. Build a separate installable web application in race-day-pwa as a Vue 3, TypeScript, Vite and Vuetify application with its own build. Use same-origin API requests through a local development proxy or a production reverse proxy. Authentication and assignments come from the shared API.
3. A bib entry captures an immutable timestamp, assignment context, observation UUID and operation UUID synchronously before lookup. Each capture has its own workflow and multiple lookups can run in parallel. Prompt for occurrence if needed. A retry resends exactly the stored request using the original key.
4. Keep pending online captures in tab-scoped session storage, partitioned by account. Require explicit foreground retry, no offline bib roster, offline capture, service worker cache, or background upload. Suspend capture when the browser is offline or API connectivity cannot be verified.
5. Sample server time with a round-trip estimate. Warn when skew or uncertainty is high or the estimate is stale. Keep the raw device timestamp and surface server clock flags in receipts.
6. Test the API on disposable MySQL and exercise timing capture, retry, and clock policy in the dedicated application. Keep admin navigation and deployment rehearsal separate.

## Local operator workflow

1. In Event Details, finish Race Configuration and confirm bibs in Runner Registrations. Move the event to Ready. The manager can create physical checkpoints and map visits before arming.
2. In Organizer Members, an Event Administrator can send a checkpoint-account-only invitation. The operator accepts using the existing invitation page. This creates or verifies an account without adding an organizer membership. In Race Control → Timing Setup, a manager grants that account access to a physical checkpoint.
3. In Timing Setup, arm each race with its results and cutoff bases, then move the event Live using Event Details. Declare the actual gun per race or wave. Late roster admission requires a confirmed registration, available bib and reason. Race Control handles timing exceptions and official decisions.
4. With the local API running, run npm ci and npm run dev in frontend/race-timing-web for administration at http://localhost:5173. For the checkpoint app, run npm ci and npm run dev in race-day-pwa. Open http://localhost:5174. Vite proxies /api to http://127.0.0.1:5080 by default. Set RACEBIB_API_ORIGIN to a different local API address if needed. npm run build creates the independent dist bundle. On a dedicated HTTPS origin, route /api to the same RaceBib backend.
5. Sign in, choose an assigned event and one of its checkpoints, and press Enter or Record crossing as the bib arrives. A single occurrence submits automatically. Repeated visits require a choice or an explicit request for race-control review. Lookups and submissions for different captures can complete out of order.
6. Retry a capture whose receipt is uncertain using its own Retry capture action. Retry keeps the original timestamp and operation identity. A 403 after a submitted request requires manager verification if access was revoked before a replay could confirm its receipt.

The clock check estimates device offset by comparing the server instant to the midpoint of a round trip. It warns beyond 2 seconds estimated skew, above 500 ms half-trip uncertainty, or when a sample is unavailable. Results are estimates and asymmetric latency can bias them. Captured instants are preserved as read from the device. If the connection is unavailable, the station uses the last verified assignment for a limited time and saves captures for synchronization.

The checkpoint application stores captures in IndexedDB on the current browser profile, partitioned by operator account. A capture appears in the list and its bib field clears after the local write commits. The server receipt confirms the recorded outcome. Sign back in with the same account on the same origin and browser profile to recover pending captures after restarting. When upgrading, open each tab that still holds pending captures and sign in with the original account so the app can migrate those records. A failed migration keeps the original tab data.

The app requests persistent browser storage when available. Granting it reduces the risk of eviction. Clearing site data or removing the browser profile still removes local captures. If storage fails, the app pauses capture and retry. Keep any tab with unmigrated captures open while you resolve the storage error. The manifest provides app identity and icons. The service worker caches the shell, and queued captures upload in the foreground after an authenticated connectivity check.

## API integration

The checkpoint app uses the existing JWT login at `/api/v1/auth/login` and uses `/api/v1/me/timing-clock` and `/api/v1/me/timing-assignments` to verify connectivity and station access. Completed events are omitted from the assignment feed, while their existing grants and evidence remain available in event history. The checkpoint app refreshes assignments every 15 seconds while online and when the tab returns to the foreground. For an assigned station it calls the event-scoped `registrations?bib=...` and `observations` endpoints under `/api/v1/events/{eventId}/timing/checkpoints/{checkpointId}`. Each submission carries the server-issued assignment generation, a client observation UUID, and an Idempotency-Key. Only a matching server receipt changes the capture to recorded. The manager setup endpoints use the existing event timing permissions.

## Browser rehearsal

Run the rehearsal only against a disposable local MySQL database and API. Use Development mode with a synthetic seeded administrator, development invitation delivery, and API port 5092. Set the local API origin on both Vite processes to http://127.0.0.1:5092. From each frontend directory start Vite with the following PowerShell commands, using port 5193 for administration and 5194 for the checkpoint app.

```powershell
$env:RACEBIB_API_ORIGIN = 'http://127.0.0.1:5092'
node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 5193 --strictPort
```

From race-day-pwa, install the Playwright Chromium browser with `npx playwright install chromium`. Set `REHEARSAL_EMAIL` and `REHEARSAL_PASSWORD` to the disposable administrator credentials. Optionally set `REHEARSAL_ARTIFACTS` to a directory to save screenshots, or `REHEARSAL_BROWSER` to a local Chromium-compatible executable. Run `npm run rehearsal`.

The script creates a new synthetic organizer and 12K/24K event. It seeds setup data through the shared API and creates both registrations through the administration UI. It signs in as a station-only operator, records start and finish in reverse server receipt order, selects a repeated aid visit, observes a slot conflict, and retries a deliberately dropped receipt with the identical submission. In Race Control it verifies missing and over-limit evidence, applies Finished and DNF decisions, and invalidates the accepted finish to verify that the official Finished decision remains visible with a re-review warning.

## Time zones and Suspicious Clock

Event, race and wave schedules represent UTC instants. Event management displays them in the configured IANA zone. The editor converts wall times to UTC, rejects nonexistent DST times, and asks for an offset when a wall time occurs twice. Changing the display zone of an existing event preserves its scheduled instants unless the operator edits the schedule. Absolute timestamps from the JSON API include a UTC designator. Legacy offsetless event timestamps are interpreted as UTC. Capture and gun requests require an explicit UTC designator or offset.

The checkpoint UI displays event-local capture time alongside a raw UTC value available on the capture details. The device clock remains labeled separately. Split and cutoff calculations continue to compare instants and durations. UTC check-in exports remain explicitly labeled.

A Suspicious Clock decision can mean an observation fell outside the race's frozen timing window or was more than two minutes ahead of server receipt. A close match between device and server time can rule out drift while the frozen window still rejects the crossing. Editing the event schedule does not change past observations, decisions, or the already-armed window.

## Timing window safeguards and diagnostics

Arming requires an event end after its start, a window that has not expired, and an active race start inside the inclusive window. Active waves must be ordered and start from the race start through the event end. A past scheduled start is allowed within an active window. The setup records a frozen copy of the schedule, so later event edits do not revise validation for an already armed race.

Timing Setup compares the current event schedule with each frozen window. It shows server-evaluated Upcoming, Active, or Expired status as of the last load or refresh. An event edit warns when timing is already armed. There is no adjustment or rearming operation in this workflow.

Capture outcomes remain compatible. New SuspiciousClock decisions store a stable reason code and expose an optional diagnostic containing the code, observedAtUtc, and boundaryAtUtc. The codes are observed_before_timing_window, observed_after_timing_window, and observed_ahead_of_server_clock. Precedence is before, after, then future skew. Exact endpoints and the exact two-minute tolerance are allowed. Idempotent retries return the initial decision and its corresponding diagnostic. Older decisions retain their original reason and revision and have no structured diagnostic.
