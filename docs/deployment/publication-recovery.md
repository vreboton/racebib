# Retained publication recovery

This procedure publishes release metadata only. It neither deploys nor rebuilds images. Review and publish this implementation to protected `main` with separate authorization before dispatching its new workflow. No recovery release was created during implementation.

## Retained evidence

The following existing releases were observed published with `immutable=false`. Keep their tags, releases, assets and GHCR objects unchanged. Their original manifests are 455 bytes, and their recorded asset SHA-256 values match the downloaded bytes. Original build and deployment-safety checks succeeded; both image publishing jobs succeeded; final release verification failed. All recorded attempts below are attempt 1.

| Source commit | Release ID | Build / safety / publication run IDs | Original manifest SHA-256 |
| --- | --- | --- | --- |
| `2ff875ac20ae8599576b6eb2176fc302423bbeab` | `448185688` | `44024063207` / `44024063152` / `44024381869` | `b4f97c2b987e3c20a83f209c93117456fc50bb97f232c8fa71e0a2e8fdb5aa9f` |
| `02f28e51c259289b8b9c20ec4f12cffba551acd5` | `448219693` | `44028266860` / `44028266847` / `44028566796` | `a5dac00982d0594878658328d3d18986a8b7e65495afa6fb5417f3f0626ec5c0` |

Exact image references in those manifests:

```text
2ff875ac20ae8599576b6eb2176fc302423bbeab
ghcr.io/vreboton/racebib-api@sha256:be534a46c85271abba799c3c711584603298e89ca34887c764f7982acd3aa357
ghcr.io/vreboton/racebib-web@sha256:ed21f350641048a52929085c1e86b3d30ddb8a18fb63559eeaca4f9a1c9d59fb

02f28e51c259289b8b9c20ec4f12cffba551acd5
ghcr.io/vreboton/racebib-api@sha256:56a031ccc86fab4ca35ad82de6880074a0a98fb994616e76acec5a7ba12f8d90
ghcr.io/vreboton/racebib-web@sha256:dc8b0024fc94143267339c66d0948717a5e9c666b11dc4f00a3d1e1cefdfb9fa
```

Live GHCR object availability was **not verified locally**: the available workstation GitHub credential has repository access but no package-read scope. Anonymous token requests were denied, and credentialed registry reads returned a scope denial / manifest-not-found response. These responses do not establish deletion. The references above match retained manifests and successful publisher output, not a completed local pull. Before recovery can publish, obtain approved package read access and require `recovery-plan` to pass end to end; the workflow's repository token also needs explicit package Actions access. Do not weaken digest checks to work around this limitation.

`recovery-plan` rechecks live evidence before any publication: exact source/main ancestry and lightweight tag, manifest size/hash, source build/safety workflow attempts, successful preflight and both actual publisher jobs, failed/cancelled finalizer, every manifest field against logged publication inputs, and image digests against each build-push output. GHCR checks root digest bytes/header, original attempt-qualified tag, linux/amd64 manifest/config digests and source/revision labels. Layer integrity remains enforced by Docker's digest-checked pull; the plan does not download/run layers. It rejects missing logs, skipped jobs, partial/mismatched assets and moved tags. It does not convert a failed image build into a release.

## Commands on a trusted workstation

Use Bash, Python 3 and GitHub CLI, an authenticated account with repository Actions dispatch/read, contents read and package read access, and reviewed local scripts. Avoid `set -x`. Authenticate via the approved GitHub CLI flow; do not paste a token into a command. For private packages, the selected credential must grant `read:packages` and package access (a suitable PAT classic or approved equivalent may be necessary).

```bash
gh auth login --hostname github.com
export GH_TOKEN="$(gh auth token --hostname github.com)"
export GHCR_TOKEN="$GH_TOKEN"
export GHCR_USERNAME="$(gh api user --jq .login)"
SOURCE=02f28e51c259289b8b9c20ec4f12cffba551acd5
python3 -B deploy/scripts/release.py recovery-plan "$SOURCE"
```

Compare the output against the evidence above and investigate any difference. Prefer this later source because it includes the administrator failure cleanup. To recover the earlier source independently, set `SOURCE=2ff875ac20ae8599576b6eb2176fc302423bbeab` and repeat; never substitute its images into the later manifest.

After review, with release immutability enabled and the workflow available on protected main:

```bash
gh workflow run recover-publication.yml --repo vreboton/racebib --ref main -f source_sha="$SOURCE"
gh run list --repo vreboton/racebib --workflow recover-publication.yml --event workflow_dispatch --limit 10
# Copy the exact new run ID from the matching source's 'recover publication SHA' title.
RECOVERY_RUN_ID=REPLACE_WITH_MATCHING_RUN_ID
gh run watch "$RECOVERY_RUN_ID" --repo vreboton/racebib --exit-status
ATTEMPT="$(gh run view "$RECOVERY_RUN_ID" --repo vreboton/racebib --json attempt --jq .attempt)"
RELEASE_TAG="racebib-$SOURCE-recovery-$RECOVERY_RUN_ID-$ATTEMPT"
python3 -B deploy/scripts/release.py verify "$SOURCE" --release-tag "$RELEASE_TAG"
unset GH_TOKEN GHCR_TOKEN GHCR_USERNAME
```

The workflow's repository `GITHUB_TOKEN` has contents write only for new tag/release creation, actions read and packages read. Enable package access for this repository's Actions. It checks out the trusted workflow revision on main, never the selected historical source. It has no image publishing permissions or deployment steps. Manual dispatch authorizes publication, not production deployment; production environment approval remains separate.

The new lightweight tag points to the **original** source. Its immutable release contains unchanged `release.json` plus `recovery.json`, which binds original release ID/tag and manifest hash to this unique recovery workflow run/attempt. Consumers demand `immutable=true`, both asset hashes, tag/source/receipt agreement, successful recovery run/job and the original source/image proofs. Default canonical release verification still rejects the old mutable release. Explicit recovery selection is mandatory; no newest-tag guessing or fallback exists. Previously stored manifest identity remains unchanged for rollback.

## Interrupted publication

If the original release has a complete verified manifest in a retained draft, the same recovery path can copy it without publishing or editing that draft. If its manifest is missing or partially uploaded, stop: this implementation deliberately refuses to reconstruct provenance from guessed run IDs or tags. Investigate separately or build a reviewed new source commit through the ordinary workflow.

If recovery fails after creating its new ref/draft/published release, retain it unchanged. Inspect the failed run; do not use `gh release upload --clobber`, tag force updates, release deletion or an image rebuild. A new workflow attempt creates a distinct tag. Re-run **all jobs**, not only a failed step, so it revalidates evidence and prepares a fresh directory:

```bash
gh run rerun "$RECOVERY_RUN_ID" --repo vreboton/racebib
gh run watch "$RECOVERY_RUN_ID" --repo vreboton/racebib --exit-status
# Re-read ATTEMPT and construct RELEASE_TAG using the commands above.
```

Even a published immutable artifact from a failed recovery run remains undeployable. A successful attempt is required. If settings or API behavior still return `immutable=false`, investigate without changing the old artifacts, then use a fresh attempt after correcting settings.

## Deployment remains separate

Recovery does not establish database compatibility, TLS readiness, restore safety or AWS health. Follow [the EC2 runbook](aws-ec2.md), install the reviewed scripts first, and pass the exact recovery tag when selecting this source. Production state preserves it for subsequent rollback. Configure protected, read-only private-repository/package credentials on EC2 if needed; runner credentials do not travel through SSH. Rehearse verification and digest pulls on an isolated staging host before seeking approval for deployment. Preserve original Actions logs and GHCR attempt tags for the recovery/rollback period; expired/deleted evidence fails closed.

GitHub documents that [enabling immutable releases affects future releases](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes). Retaining an old mutable release as historical evidence is safe; treating it as an immutable deployable release is not.
