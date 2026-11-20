# Runner registration management

Open an event as its Race Director or Event Administrator and use **Runner registrations**. Search by registered name, Name on Bib or bib number. Filter by race or registration status. Open a registration to view its audit history, edit its registration and payment status independently, change its bib or cancel it.

Runner profiles belong to organizers and can be used across events. Each event registration links to an organizer's runner and a race in that event, with optional category and start wave. A runner can have one managed registration per event. Reopen the existing registration if a cancelled runner returns. The API preserves a cancelled registration and its bib reservation. Reopen, then explicitly change or clear the bib to release it.

Choose a category or wave from the selected race. Age-limited categories use age on the race date in the event time zone and require a birthdate. Event registration writes are closed when the event is completed or cancelled. Registration and payment status have separate values and neither triggers a payment.

## Name on Bib and publication

Name on Bib belongs to the registration and accepts a Unicode preferred name, nickname or pseudonym. The limit is 60 text elements and 120 UTF-16 code units. A blank value publishes the registered full name on printed bibs and public participant outputs. The RD editor shows the exact fallback once a profile is selected. Edit the value from **Registration details**. Print an assigned bib from those details.

The public participants page and its downloadable CSV expose bib number, effective Name on Bib and race for confirmed registrations in a published, active organizer event. Use the same public identity contract when integrating results, leaderboards or live timing. This repository currently has no public results, leaderboard or live timing presentation to update. Registered names and profile contact fields are available in organizer management views. A fallback full name is part of the public identity only when Name on Bib is blank.

The RD or registration provider administers the applicable Registration Guidelines. RaceBib stores the registration data and publication display identity covered by this milestone.

## Import a CSV

1. Open the event, select **Import CSV**, and choose **Download CSV Template**. The [fictional sample](./runner-registration-sample.csv) shows two completed rows.
2. Fill in First Name, Last Name, and Email on every data row. Leave optional fields blank as needed.
3. Select an active **Default Race**, upload a UTF-8 comma-delimited CSV, choose a bib assignment mode, then select **Validate & Preview**. The limit is 1 MiB and 1,000 data rows.
4. Review row errors, proposed bibs, and any rows assigned to the default race. Download the error report to correct invalid rows.
5. Select **Confirm Import** within 20 minutes of a valid preview. Confirmation rechecks the file, default race and proposed assignments. Validate again if any input or relevant event data changes.

Keep the exact template header names. Column order can vary, and optional columns can be omitted.

```csv
First Name,Last Name,Name on Bib,Gender,Birthdate,Email,Mobile Number,Race,Category,Start Wave,Bib Number,Team,Emergency Contact
```

Email is trimmed and matched case-insensitively within the organizer. A unique email match reuses the saved profile and its data. If the CSV name differs, the preview identifies the saved profile in a row warning and still allows confirmation. An unmatched valid email creates a profile at confirmation. A blank or invalid email, duplicate email row, or email shared by multiple organizer profiles must be corrected before import. Resolve duplicate profiles before importing. Each runner can have only one registration per event.

Race values match event race names exactly after trimming, ignoring case. A blank or unrecognized value uses the selected active default race. The preview identifies the affected rows and the confirmation reports how many registrations used the fallback. Ambiguous or inactive matches are errors. Category and Start Wave may be blank and remain unassigned. Populated values must belong to the resolved race.

Imports set registration status to **Confirmed** and payment status to **Paid**. The old Runner ID and status headers are unsupported. Enter dates as YYYY-MM-DD or M/D/YYYY, and keep bib numbers as text in the spreadsheet to preserve leading zeros.

An existing profile's birthdate is authoritative for age limits. If the CSV supplies a different date, verify the discrepancy and correct the CSV or update the profile. If the profile lacks a birthdate and the category has age limits, verify and save the date on the profile, or choose a category without age limits. A valid CSV date may populate a new profile. Imports do not update existing profile fields.

Confirmation is all-or-nothing. Error reports contain row numbers and predefined messages. Public CSV export escapes spreadsheet formula prefixes.

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

The import routes accept a multipart file, required defaultRaceId for an active race in the event, and optional assignment JSON with numeric mode. Confirm also accepts the preview token. Preview returns row results, validation feedback, an error CSV, and a token for valid files. It validates headers directly from the file. Request mapping and preview mapping fields have been removed. Preview tokens issued before this change require a new preview. Resubmit the same file, defaultRaceId, and assignment. A protected ticket binds the actor, event, original bytes, default race, resolved assignment, and resolved profile identity (ID, normalized email, saved names, and birthdate) to the preview. The confirmation receipt and all registrations commit together. Uploaded records live in request processing only. The app does not store uploaded files or their rows separately.

## Migration and verification

AddRunnerRegistrationManagement makes BibNumber nullable and adds registration display and payment data and runner profile fields. Existing bibs are retained and existing payment states become Unspecified. Before downgrading that migration, assign a unique bib to each unassigned registration and preserve any needed milestone data.

EnforceEventRunnerRegistration replaces the non-unique event/runner index with a unique MySQL index on the event, runner and a database-owned slot. The migration retains one indexed slot per historical pair and marks additional legacy rows with a null slot. Every registration remains intact. New application and ordinary SQL inserts use the reserved slot by default. The slot is never writable through the API. Direct database maintenance must preserve the slot and keep all historical records for a pair together.

The API checks all registrations for a runner, including legacy duplicates. It continues to take the parent event lock so duplicate validation, bib allocation and confirmation are atomic. Manual registration and import confirmation take the organizer lock after the event lock before reading runner identity and eligibility. Confirmation resolves email profiles again under this lock, including across different events for the same organizer. A competing import that creates a profile can make a preview stale. Validate again to reuse the newly saved profile. Runner profile writes take only the organizer lock. This keeps profile edits and other imports from changing the organizer's identity data between final validation and commit.

Validate the migration on a disposable MySQL 8.4 database and run the backend unit and MySQL integration suites and frontend tests and production build. The MySQL tests cover organizer scope, invalid references, public DTO shape, Unicode, cancellation, audit, stale and repeated imports and concurrent allocation. Use a local or CI database with a racebib_test_ prefix as described in the repository's CI setup.

Local acceptance checks used the .NET 8 SDK and a disposable MySQL 8.4 container. The Release build and EF model check succeeded, and 53 backend unit and 20 MySQL integration tests passed. The integration suite exercised the migration, organizer denial, public identity, replay and stale preview rejection, concurrent allocation and database rollback. The migration was rehearsed with two legacy registrations for the same pair and preserved both rows, bibs, statuses and the pseudonym. The HTTP suite covers conflicting identities and birthdates, stale eligibility, and concurrent manual and CSV writers for one runner.

On Node.js 22, all 26 frontend tests passed and the TypeScript and Vite production build succeeded. Vite reported its bundle-size warning. A headless Chrome check against the local production bundle and disposable API verified the public table and CSV identities, the printed fallback, and the authenticated editor hints.
