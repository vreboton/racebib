# Timing and split calculation

Phase 6.2 reads the frozen setup, ordered visits, optional frozen wave, current accepted slots, and gun history from Phase 6.1. The original capture, decision, and correction paths remain authoritative. The calculation creates no tables, records, or caches.

## Official start and elapsed time

- A wave registration uses the current official declaration for that wave. An unassigned registration uses the current race declaration. A declaration is current when no later declaration in its scope supersedes it. A scope with several current declarations, or recorded history with no current declaration, yields an unavailable gun and a readiness flag.
- Frozen scheduled time is included as context. Gun elapsed remains unavailable until an official declaration exists in the applicable scope.
- Individual start comes from accepted evidence at the frozen start occurrence. Without it, net elapsed is unavailable. Gun and net elapsed are computed independently.
- All API times are UTC instants. Durations are signed integer microseconds. A crossing before its applicable start keeps the signed elapsed value and includes an anomaly flag. Full durations span midnight and multiple days.

## Visits, cutoffs, and finish

- The response includes every configured occurrence in sequence, including missing and optional visits. Repeated physical checkpoints retain separate occurrence IDs. Accepted evidence includes the observation ID and decision revision.
- An adjacent segment requires usable evidence at both neighboring occurrences. A missing or inconsistent crossing leaves the segment unavailable. Negative adjacent chronology is flagged and suppresses the segment. Identical adjacent timestamps return zero and carry a flag.
- Accepted slots with conflicting decision, revision, or physical checkpoint context are flagged and excluded from elapsed and split calculations. A crossing outside the frozen window remains visible with a flag when race control explicitly accepted it.
- The frozen schema stores cutoff durations in seconds for visits and races. Absolute cutoff instants have no field in the frozen configuration. A checkpoint limit measures elapsed time at that visit, and a race limit measures elapsed time at the accepted finish. Both use the frozen cutoff basis, so moving the selected start moves the effective deadline. Equality returns `within_limit`. Missing or negative elapsed returns `unknown`. Exceeding a limit returns `over_limit`. These are calculated indicators.
- A usable accepted finish produces a provisional finish even when evidence is missing elsewhere. Report gun and net independently and expose `resultsElapsedMicroseconds` using the frozen results basis. Readiness reports missing official gun, individual start and finish, missing mandatory visits, and unknown configured cutoffs. A provisional finish is calculated evidence for later adjudication.

## Read API

The base path is `/api/v1/events/{eventId}/timing/races/{raceId}/results`.

| Method and path | Response |
|---|---|
| GET base path | `eventId`, `raceId`, `courseId`, `timeZoneId`, `offset`, `limit`, `total`, and `items`. The frozen roster is ordered by registration ID. Defaults are offset 0 and limit 25, with a maximum limit of 100. |
| GET base path plus `/{registrationId}` | Same event and frozen race metadata with a single `result`. |

Each result contains `registrationId`, `bibNumber`, `waveId`, `scheduledStartUtc`, `gunDeclarationId`, `officialGunAtUtc`, `individualStartAtUtc`, both timing bases, ordered `visits`, `provisionalFinish`, `raceCutoff`, and `readiness`. Unavailable timestamps, durations, or finish results are JSON null. Nullable elapsed fields have companion reason fields, including at the provisional finish. An indeterminate cutoff returns status `unknown` with `unavailableReason`. Stable reasons include `missing_official_gun`, `missing_individual_start`, `missing_crossing`, `inconsistent_gun_history`, `inconsistent_accepted_evidence`, and `invalid_chronology`. The response includes a frozen bib and registration ID.

The endpoints require an active Event Administrator or Race Director in the event's active organizer. Missing authentication returns 401. Other unauthorized access returns 403. A missing event, scoped setup, or frozen registration returns 404. Invalid pagination returns 400.

MySQL repeatable-read provides one snapshot for all reads in a request, including authorization and calculation inputs. A newly committed observation, invalidation, or gun replacement appears on a subsequent request. Separate pages are separate snapshots, so a late roster amendment can change pagination between requests. Reads are provisional and intended for managers.
