# Automatic release notes

Normal publication needs only reviewed source, the product version update, and
the version tag. CI generates the release body from Git commits with git-cliff
2.10.1 before building. No PR, API token, AI service, or per-version notes file is
required to generate the body.

## Commit messages

Use Conventional Commits for automatic grouping:

```text
feat(runtime): support runtime selection
fix(patcher): preserve APK entry casing
build(deps): update the maintained Interop fork
fix!: reject incompatible payloads

BREAKING CHANGE: Upgrade the companion patcher before using this release.
```

Features, fixes, performance, dependency updates, refactoring, documentation and
maintenance receive separate sections. Unstructured messages remain under
**Other changes**; there is no need to rewrite history. Breaking changes carry a
bold marker and their breaking-change description. Other commit bodies are not
published. Each entry links to its commit, and the body ends with a comparison
link pinned to the target SHA.

Write subjects suitable for users. The generator does not infer behavior from
diffs, deduplicate related commits, translate text, inspect dependency fork
history, or invent compatibility/test claims. CI, docs and maintenance commits
remain visible to avoid silently dropping changes.

## Optional extra information

Only when needed, commit `docs/releases/<tag>.md` before tagging. CI prepends
that file to the generated changes. Use it for required companion versions,
upgrade instructions, download selection, known limitations, or validation scope.
It is an addition, not a replacement for the generated log; omit duplicate titles
and commit lists. Missing or blank files require no action.

The generator reads this file from the target commit, ignoring uncommitted
edits. Existing historical version files remain intact.

## Baseline and preview

CI checks out full history and tags. The previous version is the nearest
reachable matching product tag, excluding the current tag:

- Patcher matches `v[0-9]*` within its own repository.

Unrelated tags do not split the generated body. Version tags are release
boundaries, so reserve matching tags for product releases. This selection uses
Git history, not GitHub's mutable latest-release setting.

**Preview release notes** is an optional Actions workflow. Supply the next tag;
leave the previous tag empty for automatic selection. The workflow writes a job
summary and a downloadable `release-notes` artifact without publishing.

With Git, Bash and git-cliff 2.10.1 installed, run from the product checkout:

```bash
bash scripts/generate-release-notes.sh NEXT_TAG > /tmp/release-notes.md
# Explicit baseline and target, for preview/recovery:
bash scripts/generate-release-notes.sh NEXT_TAG PREVIOUS_TAG TARGET_SHA
bash scripts/test-release-notes.sh
```

If no matching ancestor exists, generation stops rather than publishing the
entire upstream history. For an intentional first release, pass `root` as the
previous argument in the preview/local command. Add a reviewed starting version
tag before normal automatic publication, or explicitly adapt the first-release
workflow. Shallow history, missing/non-ancestor baselines and a target that differs
from an existing version tag fail validation.

## Publication and recovery

The tag workflow generates notes once, uploads them as a CI artifact, builds the
products, then consumes that same artifact when publishing. Notes are separate
from runtime/package assets. Tests and generation must pass before compilation.

The release job creates or resumes a draft, uploads assets and checksums, and
publishes it. Retries never replace already-public assets or notes. Correct
published prose with `gh release edit --notes-file`; binary changes need a new
version. Do not move published tags. Keep build logs, local paths, private inputs
and credentials out of commit subjects and optional public notes.
