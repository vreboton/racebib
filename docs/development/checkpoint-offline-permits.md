# Event-scoped offline station permits

## Security architecture

A signed permit authorizes the checkpoint PWA to store raw crossings locally for one authenticated operator, event, checkpoint, assignment generation and offline policy revision. It grants no API access and is never accepted as a credential by an upload endpoint. Observation submission retains live account, organizer, assignment-generation, frozen roster, timing window, clock and idempotency checks. Capture records retain their original observed timestamp. The local record may identify the permit under which it was saved.

The existing timing-management roles, Race Director and Event Administrator, can change the duration for an active assignment. The default is 12 hours. Timing Setup exposes explicit 24-, 36- and 48-hour approvals with a required reason. The API accepts authorized values from 12 through 48 hours and requires the current assignment generation. Changing the policy updates a separate revision so pending evidence keeps its original assignment generation. Revocation and regrant reset the duration to 12 hours. Policy changes and issuance are audited with scope, duration or bounds, and the original actor. Signed permits and private keys are excluded from audit metadata.

An authenticated checkpoint operator requests a permit explicitly from the selected station. A permit can be issued only while the event is Live, its approved schedule is open, and an armed course mapped to that checkpoint has an active frozen timing window. Expiry is the earliest of the approved duration, event closure, and the end of the latest applicable frozen interval already active at issue. Later-starting disjoint intervals cannot extend an issued permit. Event edits cannot silently extend a permit. A successful assignment refresh invalidates cached permits if the scope or policy revision changes, or the known operational window shrinks beyond their bounds. Neither polling nor reauthentication silently renews a permit.

The API signs the exact JSON payload with ECDSA P-256 and SHA-256. The PWA verifies the bytes using Web Crypto and a public key pinned in the app bundle, then checks the signed operator, event, station, generation, policy revision and expiry. A supplied replacement key is never accepted from the network or browser storage. Legacy assignment caches are not permits. HTTPS is required outside local loopback development. An ordinary online station can still record with its live assignment when the permit key is unavailable. An unsupported or misconfigured build cannot grant offline capture.

## Key provisioning

Set the API configuration value `OfflineStationPermits:SigningKeyPem` to a dedicated P-256 private key through the approved secret configuration mechanism. Set `VITE_OFFLINE_PERMIT_PUBLIC_KEY` to the matching base64url-encoded DER SubjectPublicKeyInfo before building the standalone PWA. Keep the private key out of the bundle, source tree, build logs and screenshots. Match the same signing key across API instances. Treat rotation as a coordinated change to the API key and the PWA pin. Old cached app bundles may continue accepting their existing permits until they update or those permits expire.

For disposable local rehearsals, generate an ephemeral P-256 key pair. Set the API key and build the PWA with its public pin. The production-bundle browser fixture also needs that disposable private key in `REHEARSAL_PERMIT_PRIVATE_KEY` to emulate issuance. Never use a production key in a rehearsal.

## Offline revocation and clock limits

A disconnected station cannot learn of revocation, account deactivation, schedule edits or event completion. The exposure can last until the permit's signed deadline. Explicit online account denial clears the cached authorization, and a station denial clears the affected permit. An origin sharing the profile can observe local permit removal. Server submission remains subject to live authorization, even if the crossing was locally permitted. Retain rejected and expired evidence for reconciliation rather than rewriting its original grant or timing.

At issuance, a device more than two minutes away from the signed server time is asked to correct its clock. While the app runs, the countdown takes the greater of wall time and a monotonic estimate. The profile retains timestamps from saved captures and a latched expiry. Moving the clock backward below the saved watermark disables offline capture. Moving it forward beyond expiry ends offline capture, and moving it back does not restore a latched expired permit. Browser restart loses the monotonic clock baseline. A browser cannot attest elapsed time during a disconnected restart, detect every clock manipulation, or defend against a privileged party who can alter origin code or browser storage. The permit is account-bound in the application, and is not hardware-attested. Keep an offline device and profile under operator control and use automatic time.

After expiry or a detected rollback, queued evidence remains in account-scoped IndexedDB. Authenticate online to drain eligible records and explicitly request a new permit before going offline again. Never clear site data when there is unreconciled evidence.

## Race-day validation

A 24-, 36- or 48-hour approval only covers time inside the event and checkpoint window. Test each station's real offline duration, phone restart, installed mode and staffing plan. A simulated clock jump verifies expiry logic but does not substitute for a multi-day field soak. Plan a manual timing fallback for outages past the approved deadline or device/profile failure.
