# Registration readiness and roster export

Open **Race readiness** under **Runner Registrations**. The event summary always covers the full event. The race filter narrows the breakdown and the roster export. Races with no registrations appear with zero counts.

| Metric | Denominator or rule |
|---|---|
| Total registrations | Every stored registration in scope, including withdrawn and historical registrations |
| Confirmed, Pending, Waitlisted, Withdrawn | Each counts only its own status. The stored API value for Withdrawn is Cancelled |
| Historical | DNF, DNS, and Disqualified combined |
| Confirmed without a bib | Confirmed registrations with a null stored bib. Legacy invalid or blank stored bibs are tracked separately |
| Bibs assigned | Non-null stored bibs, including bibs reserved by withdrawals and invalid legacy values |
| Confirmed checked in or not checked in | Confirmed registrations only, divided by whether a check-in timestamp exists |
| Inactive races with registrations | Number of inactive races with at least one registration, regardless of status |
| Eligible without a bib | Pending, Confirmed, and Waitlisted registrations in active races, with a null bib and no check-in timestamp |

**Action required** covers confirmed registrations without bibs, confirmed registrations in inactive races, invalid stored bibs, and registrations sharing a bib under the event's database collation. Invalid bibs use the allocator's ASCII, length, and allowed-character rules. The event-wide unique index normally prevents duplicates.

**Attention** covers unassigned Pending or Waitlisted registrations eligible for a bib, inactive races retaining registrations, and a scope with no confirmed registrations. **Ready** means the registration roster has no listed warnings. Check-in progress is informational. The assessment covers registration readiness only.

## API and roster

- `GET /api/v1/events/{eventId}/readiness` returns the event summary, assessment, and up to 1,000 races with their own counts and assessment.
- `GET /api/v1/events/{eventId}/registrations/export` accepts optional `raceId`, registration `status`, and `bib=Assigned|Unassigned`. Combine filters to narrow the roster. Exports over 5,000 matching registrations return HTTP 422. An empty export contains the header row.

Roster rows are ordered by race name and ID, runner last and first name, then registration ID. The filename contains the event ID and UTC export date. The UTF-8 CSV includes Bib Number, First Name, Last Name, Name on Bib, Race, Registration Status, Check-in Status, Check-in Time (UTC), and Team. Import Bib Number as text in spreadsheet software to retain leading zeros. Formula-looking values are prefixed with an apostrophe so ordinary text, punctuation, and quoted multiline fields can be exported safely.

Both operations use existing organizer management permissions and work in every event lifecycle state. They leave registrations, versions, and audit history intact. Dashboard responses contain counts rather than runner details. Rosters exclude email, phone, birthdate, emergency contacts, and payment information. The roster download uses a no-store cache directive.
