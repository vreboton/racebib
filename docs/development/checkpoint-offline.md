# Offline checkpoint capture

Sign in on the checkpoint device while connected. Select the assigned event and checkpoint, verify that capture is enabled, and wait for the app to report that it is available offline. Record a synthetic crossing only in a rehearsal event. The bib field clears after the local database commits. The queue distinguishes locally saved evidence, bib lookup, visit selection, synchronization, server receipts, timing exceptions, and captures that require operator action.

## Application shell

The standalone production Vite build generates a service worker with an allowlist and a version derived from the shell contents. The worker caches the HTML, bundled static assets, manifest, and icons. It serves cached assets and the app entry point when offline. Authenticated API calls go directly to the network with cache disabled. The deployment should use HTTPS and serve the worker script with revalidation. Production builds on loopback also support the worker for local rehearsals.

An updated worker installs into a separate cache and waits for the old app windows to close. Opening the app again loads the new shell. The worker does not reload an open station or touch IndexedDB. Keep serving complete versioned bundles during rollout.

## Offline authorization and storage

A successful authenticated assignment refresh grants up to 12 hours of local capture on that browser profile. An assignment must have been open when last verified. The station shows whether assignments are cached and when they were checked. When online, fresh assignment data replaces the snapshot, including removal of completed events and revoked grants. A backwards device clock earlier than the verification timestamp also ends offline capture. The deadline is a client-side operational limit. The backend still authenticates and authorizes every request.

The device remembers the last account and that account's minimal assignment feed and selection in local storage. The queue is partitioned by account. First-time login, provisioning, and switching accounts require the API. Sign-out disables offline access until the next login. If a browser restart ends the authenticated tab session, the previous operator can still record locally within the offline window and must sign in again before upload. Bearer tokens remain in tab session storage. Anyone with access to an unlocked, provisioned browser profile may see and use that profile's offline station, so use a dedicated profile and lock the device when unattended.

IndexedDB database `racebib-checkpoint` uses an account and observation UUID composite key. It preserves the raw bib, UTC wall-clock time, event and checkpoint, original assignment generation, device ID, operation key, and clock sample or absence of a sample. The app records whether the clock check was stale and never applies its estimated offset to a crossing. Separate fields track lookup results, occurrence selection, finalized request, retry count, next retry time, last error, and server receipt. Legacy tab records migrate by account and observation UUID. The old copy is removed only after the import commits.

The app requests persistent browser storage where supported. Clearing site data, deleting the browser profile, or browser eviction when persistence is unavailable can remove records. A storage failure pauses capture and leaves the typed bib in place if the write failed. Use another provisioned station or the race's manual timing process if the device cannot store captures. Keep the affected profile and any tab with legacy records available for recovery.

## Synchronization and recovery

The foreground station checks connectivity and authorization at startup, when connectivity returns, when it returns to the foreground, and periodically. **Sync now** checks the live service and skips the delay for retryable items. The service worker does not upload records. The app uses an exclusive Web Lock per observation and reloads the latest database record under the lock. Chromium and other browsers with Web Locks are required for synchronization. A closed or interrupted tab releases its lock. The following tab can recover a saved item or replay a finalized request.

- **Saved locally** means the browser database committed. When connected, the server resolves the bib against the frozen roster. One known visit can be submitted automatically. Repeated visits captured offline are sent without an occurrence so Race Control can resolve them. During online entry the operator can choose a visit or send it for review.
- **Synchronizing** means a finalized payload and idempotency key are stored and the submission is in progress. If a receipt is lost, retries send the same payload and key. Connection failures, throttling, and transient server errors back off from 2 seconds to a maximum of 2 minutes.
- **Sign in to sync** means the session is missing or expired. Sign in with the original account. Its queue resumes after a successful authenticated check.
- **Recorded** requires a matching server observation UUID and an accepted receipt. **Recorded · needs review** means the observation was stored with a timing exception. Confirm the decision in Race Control before treating it as a valid split.
- **Requires operator action** means the app kept the evidence locally and stopped automatic retries. This includes an unknown bib, a closed event, revoked access, or a permanent validation error. If a finalized submission later receives an authorization failure, the server may already have recorded it before access changed. Ask Race Control to check the original observation before creating any replacement.

For escalation, keep the profile intact and give Race Control the event, checkpoint, original bib and observed UTC time, and observation UUID from the capture details. Compare these against Race Control's observation history and approved manual review process. Restoring a grant or changing event state does not rewrite the evidence or its original grant generation. There is no server policy change, relational migration, bulk upload endpoint, offline runner roster, or automatic deletion of unresolved captures.

## Rehearsal

Use only a disposable local MySQL database and Development API with a synthetic seeded administrator. Follow the [online rehearsal setup](checkpoint-online.md#browser-rehearsal) for the API and administration app. For the checkpoint server run `npm ci`, then `npm run build` and `npm exec vite -- preview --host 127.0.0.1 --port 5194 --strictPort`, with `RACEBIB_API_ORIGIN` pointing to the disposable API. Set `REHEARSAL_OFFLINE=true` and run `npm run rehearsal`. The rehearsal supports custom origins through `REHEARSAL_API_ORIGIN`, `REHEARSAL_ADMIN_ORIGIN`, and `REHEARSAL_CHECKPOINT_ORIGIN`.

Run `node rehearsal/browser-checks.mjs` after the PWA build for the additional Chromium checks against a synthetic HTTP fixture. The fixture covers worker updates and interrupted synchronization across tabs.

For a physical device check, provision the station, go fully offline, record several distinct bibs, refresh, close and reopen the browser, then restart the device. Verify the cached shell, selection, saved capture count and raw timestamps before reconnecting. Sign in again if needed. Compare receipts and timing exceptions with Race Control, and test how the device behaves when persistent storage is denied.
