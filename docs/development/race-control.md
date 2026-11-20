# Race Control implementation plan

Reuse the event workspace tabs, the timing manager policy, raw observation history and revision-checked resolution, and the Phase 6.2 results calculator. Use event-scoped pages for actionable evidence and a separate race-scoped view of calculated warnings.

Registration status controls registration and check-in. Add a separate append-only official decision per frozen registration and revision. Decisions carry actor, UTC timestamp and reason. A manager can return any decision to Pending and must supply a reason for every change. Finished requires a usable accepted finish and a bib without a competing live owner. It does not automatically attest that mandatory visits were recorded. Compare the supporting calculation at the time of classification to current evidence and flag a decision for re-review when it changes.

Resolve observations through the existing manager action. Preview available occurrences and occupied slots, then include the expected decision revision with the reason. Keep raw evidence immutable. The backend enforces organizer scope, event scope and event-lock serialization for writes. Build the Vuetify workspace inside Event Details, with four tabs, paginated results and exceptions, and explicit request states.

## API and operation

All paths are relative to `/api/v1/events/{eventId}/timing`. Active Event Administrators and Race Directors in the event's organizer can read and decide. Timing assignments alone give no access.

| Path | Purpose |
|---|---|
| GET `/control` | Armed races, frozen roster counts, accepted visit and finish counts, actionable observation count, and bib conflict count. |
| GET `/control/exceptions?kind=evidence\|bib&offset=0&limit=25` | Page actionable observations by latest decision or frozen bibs with a competing live owner. |
| GET `/control/races/{raceId}/runners` | Page the frozen roster with provisional calculations, warning codes and current official decisions. Calculation warnings are shown for each roster page, including pages with zero warnings. |
| GET `/control/observations/{observationId}/resolution` | Raw and receipt timestamps, previous decisions, accepted slot, and per-occurrence preview. |
| POST `/observations/{observationId}/resolve` | Existing manager endpoint applies the requested occurrence or invalidation using the reason and expected revision. The server checks the slot, chronology and live bib ownership again. |
| POST `/control/races/{raceId}/runners/{registrationId}/classification` | Record `classification` (0 Pending, 1 Finished, 2 DNS, 3 DNF, 4 DQ), `expectedRevision`, and `reason`. Pending with revision 0 represents the initial state. |
| GET `/control/audit?kind=evidence\|classification` | Paged observation decisions or official classification transitions, with actor, time, revision and reason. |

Pagination accepts a nonnegative offset and a limit from 1 through 100. Different requests can see different snapshots. Resolution preview is advisory. Another manager can occupy a slot between preview and confirmation. A conflicting acceptance leaves the incumbent intact and requires its own explicit review.

Classification changes are available while Live or Completed and are serialized with evidence changes under the event lock. The additive migration creates `OfficialRunnerDecisions` with a frozen event/registration/race foreign key, an actor foreign key, and a unique revision key. It does not backfill classification from registration status. Reversal is a new Pending decision. An explicit decision can reaffirm or correct a previous result. A versioned fingerprint captures the applicable gun declaration, valid accepted evidence and bib ownership state for re-review. Missing visits and cutoff warnings remain informational until a manager makes a decision.
