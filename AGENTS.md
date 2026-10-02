# Repository agent rules

Read [Contributing](CONTRIBUTING.md), [scripts](scripts/README.md) and the relevant
Core contract before changing behavior. Run commands from this repository root;
no parent lock or Loader source checkout is required for builds.

- Preserve unrelated edits. Commit only when requested; do not push, publish,
  install applications or change signing state without explicit authorization.
- CLI and GUI share the Core pipeline. Fix dependency behavior in reviewed source
  forks, never through post-build rewriting. Own generator pins independently;
  do not switch shared sources to satisfy this product's pin.
- Retain unsafe ZIP path/duplicate, download and Release hash/signature, ABI,
  native-name collision and incomplete-runtime validation. Installed deployment
  edits do not require revision regeneration or a new corruption-diagnostic system.
- Consumers tolerate additive fields. Raise format versions only for semantics
  that old consumers cannot safely interpret; strip private content at producers.
- Run the narrowest relevant regression and inspect outputs/diff. Never uninstall,
  clear app data or change package names during routine testing. Cleanup owns only
  this product's generated directories, not sibling sources or dependency caches.
- Keep applications, Interop outputs, Mods, signing material, secrets and private
  evidence outside Git/releases. Tracked docs describe stable procedures and
  behavior, not local builds, job status, hashes or device acceptance progress.
- Main installation and modernization rationale lives in LemonLoader's public
  docs. Keep Patcher commands and relevant contract decisions local so this repo
  remains independently maintainable.
