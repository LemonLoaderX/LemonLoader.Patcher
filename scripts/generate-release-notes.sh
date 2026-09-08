#!/usr/bin/env bash
# stdout is the release body; diagnostics go to stderr.
set -euo pipefail
cd "$(dirname "$0")/.."
tag="${1:?Usage: generate-release-notes.sh TAG [PREVIOUS_TAG|root] [TARGET]}"
previous="${2-}"
target="${3:-HEAD}"
pattern='v[0-9]*'
[[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9][A-Za-z0-9.-]*)?$ ]] || { echo "Invalid product version tag: $tag" >&2; exit 1; }
[[ "$(git rev-parse --is-shallow-repository)" == false ]] || {
  echo "Release notes require full history and tags (fetch-depth: 0)." >&2; exit 1;
}
target=$(git rev-parse --verify "$target^{commit}")
if git show-ref --verify --quiet "refs/tags/$tag"; then
  [[ "$(git rev-parse "refs/tags/$tag^{commit}")" == "$target" ]] || {
    echo "Target does not match existing tag $tag." >&2; exit 1;
  }
fi
if [[ -z "$previous" ]]; then
  previous=$(git describe --tags --abbrev=0 --match "$pattern" --exclude "$tag" "$target") || {
    echo "No previous product tag. Pass a previous tag, or root for the first release." >&2; exit 1;
  }
fi
if [[ "$previous" == root ]]; then
  range="$target"
else
  [[ "$previous" != "$tag" ]] || { echo "Previous and next tags must differ." >&2; exit 1; }
  base=$(git rev-parse --verify "refs/tags/$previous^{commit}")
  git merge-base --is-ancestor "$base" "$target" || {
    echo "Previous tag is not an ancestor of the target." >&2; exit 1;
  }
  range="$base..$target"
fi
# Ignore tag boundaries inside the explicit range: render one release body.
generated=$(git-cliff --config cliff.toml --ignore-tags '.*' --tag "$tag" "$range")
[[ -n "${generated//[[:space:]]/}" ]] || { echo "Generated notes are empty." >&2; exit 1; }
# Read optional additions from the target commit, never from an uncommitted file.
notes="docs/releases/$tag.md"
if git cat-file -e "$target:$notes" 2>/dev/null; then
  git show "$target:$notes"
  printf '\n\n'
fi
printf '%s\n' "$generated"
if [[ "$previous" != root ]]; then
  printf '\n**Full changelog:** https://github.com/LemonLoaderX/LemonLoader.Patcher/compare/%s...%s\n' "$previous" "$target"
fi
