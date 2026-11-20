# Bulk bib assignment

Open **Bulk assign bibs** in **Runner Registrations**. Choose all races or one race, enter the inclusive range and minimum digits, and preview. Compare the proposed bibs and skip reasons before confirming. Confirm only when the displayed assignments are the ones you want.

Pending, Confirmed, and Waitlisted registrations can receive a bib if their race is active, they are unchecked, and their stored bib is null. Existing bibs remain assigned, including bibs reserved by withdrawn registrations. Historical DNS, DNF, and Disqualified registrations are skipped. Registration and payment statuses stay as they were. Bulk assignment follows the existing registration-write lifecycle and is unavailable for Completed or Cancelled events.

Allocation starts with the oldest registration, using registration ID to break timestamp ties. The existing allocator takes the next available number in the range and preserves the configured padding. Occupied bibs across the entire event count even when previewing just one race. The inclusive range may span at most 100,001 numbers, the minimum digits can be 0 through 10, and one preview supports up to 1,000 registrations in scope. For an oversized event scope, select a smaller race.

## API

Both operations require existing organizer management access and use the event registration API.

| Operation | Request | Response |
|---|---|---|
| POST /api/v1/events/{eventId}/registrations/bibs/preview | JSON with optional raceId, start, end, and width | Scope, eligible, skipped, and change counts, rows with current and proposed bibs or skip reasons, an allocation error if applicable, and a token when there are assignments to confirm |
| POST /api/v1/events/{eventId}/registrations/bibs/confirm | JSON with the token returned by preview | assignedCount and skippedCount after the transaction commits |

Preview reads only. The server protects the token with ASP.NET Data Protection, binds it to the event and staff account, and expires it after ten minutes. Confirmation accepts only that token and recomputes the plan under the event write lock. Changes to the event lifecycle, in-scope registrations or versions, race eligibility, shown rows, or approved allocations can invalidate the preview. A successful confirmation changes the registration versions, so replaying the token returns HTTP 409. An expired or stale preview requires a fresh preview and approval.

The database applies all assignments and their audits in one transaction. Every applied assignment increments the version once and records Registration.BibAssigned with the actor, registration, organizer, event ID, and timestamp. Preview reports collisions with legacy bibs that compare equivalent under the database collation. If such a bib is introduced after preview, the unique index aborts confirmation and the transaction rolls back. Staff can preview a different range.
