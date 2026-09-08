#!/usr/bin/env bash
# Integration tests use disposable Git repositories, never product history.
set -euo pipefail
source_root=$(cd "$(dirname "$0")/.." && pwd)
fixture=$(mktemp -d)
mkdir -p "$fixture/scripts"
cp "$source_root/scripts/generate-release-notes.sh" "$fixture/scripts/"
cp "$source_root/cliff.toml" "$fixture/"
cd "$fixture"
git init -q
git config user.name "Release notes test"
git config user.email "release-test@example.invalid"
git config commit.gpgsign false
git config core.hooksPath /dev/null
git -c core.autocrlf=false add .
git commit -qm "chore: initial fixture"
first=v1.0.1
next=v1.0.2
later=v1.0.3
# No baseline must fail explicitly; root is the intentional first-release mode.
if bash scripts/generate-release-notes.sh "$first" > /dev/null 2>&1; then exit 1; fi
bash scripts/generate-release-notes.sh "$first" root > first.md
git tag "$first"
git commit --allow-empty -qm "feat(runtime): select runtime"
git commit --allow-empty -qm "fix!: reject incompatible payload" -m "BREAKING CHANGE: Upgrade the companion patcher."
git commit --allow-empty -qm "Legacy unstructured change"
git tag unrelated-99
git commit --allow-empty -qm "build(deps): update maintained fork"
mkdir -p docs/releases
printf '## Upgrade\n\nUse the companion patcher.\n' > "docs/releases/$next.md"
git add docs/releases
git commit -qm "docs: describe upgrade"
git tag -a "$next" -m "$next"
target=$(git rev-parse HEAD)
bash scripts/generate-release-notes.sh "$next" > actual.md
grep -q '^## Features' actual.md
grep -q '^## Fixes' actual.md
grep -q '^## Dependencies' actual.md
grep -q '^## Other changes' actual.md
grep -q 'Legacy unstructured change' actual.md
grep -q '\*\*BREAKING:\*\*' actual.md
grep -q 'Upgrade the companion patcher' actual.md
grep -q "^## Upgrade" actual.md
grep -q "compare/$first...$target" actual.md
if grep -q 'initial fixture' actual.md; then exit 1; fi
# Existing tags and target-pinned supplements remain deterministic as HEAD advances.
printf 'Uncommitted text must not leak\n' > "docs/releases/$next.md"
git commit --allow-empty -qm "fix: future change"
bash scripts/generate-release-notes.sh "$next" "" "$target" > repeated.md
cmp actual.md repeated.md
if bash scripts/generate-release-notes.sh "$next" > /dev/null 2>&1; then exit 1; fi
if bash scripts/generate-release-notes.sh '../invalid' > /dev/null 2>&1; then exit 1; fi
if bash scripts/generate-release-notes.sh "$later" missing > /dev/null 2>&1; then exit 1; fi
if bash scripts/generate-release-notes.sh "$later" "$next" "$first" > /dev/null 2>&1; then exit 1; fi
git clone -q --depth=1 "file://$fixture" shallow
if (cd shallow && bash scripts/generate-release-notes.sh "$later" > /dev/null 2>&1); then exit 1; fi
echo "Release-note integration tests passed."
