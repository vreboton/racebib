# Runner registration management

Open an event as its Race Director or Event Administrator and use **Runner registrations**. Search by registered name, Name on Bib or bib number. Filter by race or registration status. Open a registration to view its audit history, edit its registration and payment status independently, change its bib or cancel it.

Runner profiles belong to organizers and can be used across events. Each event registration links to an organizer's runner and a race in that event, with optional category and start wave. A runner can have one managed registration per event. Reopen the existing registration if a cancelled runner returns. The API preserves a cancelled registration and its bib reservation. Reopen, then explicitly change or clear the bib to release it.

Choose a category or wave from the selected race. Age-limited categories use age on the race date in the event time zone and require a birthdate. Event registration writes are closed when the event is completed or cancelled. Registration and payment status have separate values and neither triggers a payment.

## Name on Bib and publication

Name on Bib belongs to the registration and accepts a Unicode preferred name, nickname or pseudonym. The limit is 60 text elements and 120 UTF-16 code units. Blank values use the registered full name. Edit the value from **Registration details**. Print an assigned bib from those details.

The public participants page and its downloadable CSV expose bib number, effective Name on Bib and race for confirmed registrations in a published, active organizer event. Use the same public identity contract when integrating results, leaderboards or live timing. This repository currently has no public results, leaderboard or live timing presentation to update. Registered names and profile contact fields are available in organizer management views. A fallback full name is part of the public identity only when Name on Bib is blank.

The RD or registration provider administers the applicable Registration Guidelines. RaceBib stores the registration data and publication display identity covered by this milestone.

## Import a CSV

1. Open the event and select **Import CSV**. Use the [fictional sample](./runner-registration-sample.csv) as a template.
2. Upload a UTF-8, comma-delimited spreadsheet or Google Forms export. A BOM, quoted fields and quoted commas are supported. The limit is 1 MiB, 1,000 data rows and 100 source columns.
3. Choose a bib mode and validate the file. RaceBib suggests mappings using recognised headers.
4. Map missing fields, review row outcomes and proposed bibs, and validate again after changes. Download the error report if needed. Correct the errors in the original file and upload it again.
5. Confirm a valid preview within 20 minutes. Confirmation rechecks the source and proposed assignments against current records. If anything changed, preview again. If confirmation succeeds, the result shows the committed row count.

Map **First Name**, **Last Name** and **Race**. A race cell may contain an exact race name or an unambiguous numeric distance in km. Category and wave cells use names from that race. Optional fields include Name on Bib, Gender, Birthdate, Email, Mobile Number, Category, Start Wave, Bib Number, Team / Club, Emergency Contact, Registration Status and Payment Status. Supported name aliases include Bib Name, Display Name and Preferred Name. Enter dates as YYYY-MM-DD or M/D/YYYY. Enter status names as listed in the API below.

The importer matches a profile by case-insensitive first and last names and compatible email. It rejects ambiguous matches and repeated event registrations, including cancelled ones. It preserves existing profile data and Name on Bib on re-import. Review profiles manually before importing homonyms. Confirmation is all-or-nothing. Error reports contain row numbers and predefined messages. Public CSV export escapes spreadsheet formula prefixes.

## Bib assignment

- **Existing** preserves supplied bib strings and leaves blank bibs unassigned.
- **Automatic** requires blank supplied bibs and allocates the first free numbers in the inclusive range.
- **Mixed** reserves all supplied bibs before allocating blank rows from the range.
- The minimum digit count pads automatic numbers with leading zeros. Supplied leading zeros survive unchanged. Keep bib columns as text in spreadsheet tools.
- New bibs accept up to 32 ASCII letters, digits, hyphens or underscores. Duplicate, conflicting or exhausted assignments are rejected.
- Event-wide writes serialize on the same MySQL event row used by race configuration. The existing event/bib unique index remains the final constraint. MySQL permits multiple nulls in that index.

## HTTP API

Authenticated organizer management routes use the existing bearer-token scope checks.

| Method | Path | Purpose |
| --- | --- | --- |
| GET, POST | /api/v1/organizers/{organizerId}/runners | Search or create profiles |
| GET, PUT | /api/v1/organizers/{organizerId}/runners/{id} | Read or update a profile |
| GET, POST | /api/v1/events/{eventId}/registrations | List or manually create |
| GET, PUT | /api/v1/events/{eventId}/registrations/{id} | Read registration and audit history, or update |
| PUT | /api/v1/events/{eventId}/registrations/{id}/bib | Assign, change or clear a bib |
| POST | /api/v1/events/{eventId}/registrations/{id}/cancel | Cancel and retain the bib |
| POST | /api/v1/events/{eventId}/registrations/import/preview | Validate multipart CSV and return a confirmation token if valid |
| POST | /api/v1/events/{eventId}/registrations/import/confirm | Validate a preview and commit a batch |
| GET | /api/v1/public/events/{eventId}/participants | Anonymous paginated public participant projection |

Registration list query parameters are search, raceId, status and one-based page. POST and PUT accept runnerId, raceId, categoryId, waveId, bibDisplayName, bibNumber, team, status, paymentStatus and assignment. Create or select the organizer profile first. Registration statuses in order are Pending, Confirmed, Waitlisted, Cancelled, Dnf, Dns and Disqualified. Payment statuses in order are Unspecified, Unpaid, Paid, Waived and Refunded. Admin response status values are zero-based integers. Bib assignment has mode (0 existing, 1 automatic, 2 mixed), start, end and width.

The import routes accept a multipart file, optional mapping JSON with canonical field names and zero-based header positions, and optional assignment JSON with numeric mode. Confirm also accepts the preview token. Resubmit the same file, mapping and assignment. A protected ticket binds the actor, event, original bytes and resolved assignment to the preview. The confirmation receipt and all registrations commit together. Uploaded records live in request processing only. The app does not store uploaded files or their rows separately.

## Migration and verification

AddRunnerRegistrationManagement makes BibNumber nullable and adds the event-scoped display and payment data, profile details and an event/runner lookup index. Existing bibs are retained and existing payment states become Unspecified. The runner lookup index is non-unique so legacy data can be migrated without deleting duplicate history. All new manual creates and CSV confirms use the event write lock to enforce the one-runner-per-event policy. Before a downgrade, assign a unique bib to each unassigned registration. A downgrade also removes the milestone fields, so preserve any needed data separately.

Validate the migration on a disposable MySQL 8.4 database and run the backend unit and MySQL integration suites and frontend tests and production build. The MySQL tests cover organizer scope, invalid references, public DTO shape, Unicode, cancellation, audit, stale and repeated imports and concurrent allocation. Use a local or CI database with a racebib_test_ prefix as described in the repository's CI setup.

Local acceptance checks used the .NET 8 SDK and a disposable MySQL 8.4 container. The Release build and EF model check succeeded, and 53 backend unit and 17 MySQL integration tests passed. The integration suite exercised the migration, organizer denial, public identity, replay and stale preview rejection, concurrent allocation and database rollback.

On Node.js 22, all 25 frontend tests passed and the TypeScript and Vite production build succeeded. Vite reported its bundle-size warning.
