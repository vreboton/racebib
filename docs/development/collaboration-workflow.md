# GitHub collaboration workflow

GitHub Issues hold the agreed scope and acceptance criteria. Pull requests hold the patch, verification evidence, review findings and the repository owner's decision.

## From issue to approval

1. **Define the work.** The owner can use ChatGPT for architecture or to turn an audit finding into scoped work. File a feature or bug/audit issue with acceptance criteria, a test plan and risks. Keep secrets and production data out of GitHub.
2. **Start Codex manually.** In the Codex desktop interface, open the local RaceBib project folder and start a new chat in that project. Paste the issue URL and a request such as the one below. If Codex cannot access GitHub, paste the issue body and relevant discussion after redacting sensitive details. GitHub issue creation alone does not start a Codex task. [OpenAI desktop project instructions](https://developers.openai.com/blog/building-frontend-uis-with-codex-and-figma)
3. **Implement on a feature branch.** Codex reads the issue and [repository guidance](../../AGENTS.md), branches from current `origin/main`, implements the agreed scope, and runs relevant local checks.
4. **Open a PR and check CI.** Use the PR template against `main`. Include `Closes #<number>`, evidence, skipped checks with reasons and operational impact. Check the PR's GitHub Actions results for the latest revision.
5. **Review and correct.** Give ChatGPT the issue, PR diff, and CI results through GitHub access or redacted excerpts. Ask for findings tied to changed files and acceptance criteria. Record actionable findings on the PR, hand them back to Codex on the same branch, and recheck CI and review after updates.
6. **Decide as the owner.** The repository owner reviews the final diff and evidence, resolves outstanding decisions, and performs any approved merge. Production operations require separate authorization.

```text
Implement https://github.com/vreboton/racebib/issues/<number>.
Read AGENTS.md and the issue discussion. Use a feature branch from current
origin/main, run relevant checks, and open a PR with Closes #<number>.
Do not merge or deploy.
```

Issue templates appear in GitHub's issue chooser and the PR template populates new PR descriptions once the files are on the default branch. [GitHub template behavior](https://docs.github.com/en/communities/using-templates-to-encourage-useful-issues-and-pull-requests/about-issue-and-pull-request-templates)

## Existing CI and owner decisions

[Build](../../.github/workflows/build.yml), [deployment safety](../../.github/workflows/deployment-safety.yml), and [local controller safety](../../.github/workflows/local-controller-safety.yml) run on PRs targeting `main` and on pushes to `main` or `feature/**`. Build covers .NET and EF Core consistency, disposable MySQL integration, and the frontend. The safety workflows cover isolated deployment checks and mocked Windows controller tests. [Image publication](../../.github/workflows/publish-images.yml) gates on successful builds from pushes to `main` in this repository. It publishes release artifacts and images. Production operations follow their separate authorization process.

At the issue #10 review, GitHub reported `main` as unprotected and returned no applicable branch rules. For a separate owner-approved settings change, use a branch protection rule or a branch ruleset targeting `main`. Require a PR, block force pushes and deletion, require conversation resolution, and choose the observed Actions check contexts for `backend`, `backend-mysql-integration`, `frontend`, `deployment-safety`, and `windows-mock-rehearsal` from a successful PR. If another eligible reviewer is available, require an independent approval and refresh approval after new changes. On a solo repository, the owner should review the final diff and merge manually to avoid an approval requirement the owner cannot satisfy on their own PR. Keep auto-merge disabled and review bypass permissions before enabling the rule. [Branch protection](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches) · [Ruleset options](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets)

A future owner-approved integration could dispatch tasks from an explicit label or command. It would need scoped permissions and a separate decision about who can dispatch work. Keep the same PR, CI and human approval gates.
