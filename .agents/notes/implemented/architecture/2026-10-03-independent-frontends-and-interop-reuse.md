# Agent Note: Independent front ends and reusable generation

Status: implemented

The Interop-cache decision below is superseded by
[Patcher-owned downloads without Interop caching](2026-10-03-patcher-owned-downloads.md).
Independent archives, generator normalization and bounded GUI logs remain current.

## Problem

Published CLI/GUI directories depend on a shared parent Tools directory, so moving
one executable directory breaks generation. Reinjecting a game for a different
runtime or deployment repeats Cpp2IL and Interop work even when generator inputs
are unchanged. Replacing a full log TextBox every timer tick also makes large
tool output expensive to render and forces readers back to the end.

## Decision

Each front end owns an independently movable publish directory containing its
Tools/Il2CppInterop and legal files. GUI/CLI are separate archives per RID, with
the executable at archive root. Runtime lookup uses only its adjacent Tools;
explicit generator overrides remain supported. CI uploads both archives and the
combined checksum list. The generator is built once and copied into each tree.
CLI and GUI publish as framework-dependent single-file executables containing
managed assemblies and runtime configuration. GUI native graphics libraries
remain adjacent rather than extracting from the executable at startup. External
generator files remain in Tools/Il2CppInterop with their existing provenance contract.

Generation reuses a host-side cache under the existing output-adjacent .tools
directory. Identity covers actual game binary/metadata, Unity version and reference
contents, Cpp2IL contents, generator contents and generation contract version.
Cached DLL names/sizes/hashes must match the host manifest before copying into an
isolated workspace; malformed/missing/modified entries regenerate. Publication is
transactional, cancellation propagates, and optional cache write errors warn.
ForceInteropGeneration / --force-interop bypass reuse. Cache identities never enter
the installed APK and never become Loader startup admission checks.

The pinned generator already normalizes parameter HasDefault flags. Patcher skips
the redundant Cecil pass for that generator; explicit overrides retain it.

GUI logs render as bounded virtualized rows, with bounded pending output, selectable
text, optional follow, copy/save actions and elapsed time. Validation precedes
clearing prior output. Final status is applied after draining stage messages.
Input changes update automatically suggested APK names while preserving explicitly
chosen outputs. Completed outputs can open their containing directory.

## Alternatives considered

- A shared tool saves archive space but couples movable applications to a parent
  layout. Separate copies make each download usable by itself.
- Embedding the tool in each EXE hides its directory but requires extracting its
  multi-file managed dependency graph and adds startup/publication complexity.
  Adjacent Tools has a predictable explicit lifetime and satisfies portability.
- Bundling GUI native libraries further reduces visible files but requires a
  persistent extraction directory. Bundling only managed dependencies reduces
  the root file count without that extra disk footprint or startup work.
- Reusing wrappers without checking inputs is faster but risks native ABI/type
  mismatch. Hashing host generation inputs is much cheaper than regenerating.
- Removing all Interop normalization breaks custom older generators; retain only
  their compatibility path.
- Replacing the log text less frequently retains full-string layout costs and
  loses user scroll position. Virtualized rows bound visible work instead.

## Consequences

GUI/CLI package names change; downloads select a front end. The required .NET
runtime remains external. Duplicate tools increase total asset storage, while
individual CLI downloads no longer include GUI dependencies.
GUI managed DLLs no longer clutter the executable directory; the executable
grows by the bundled content and its native graphics files remain required.
Historical generation caches
consume disk and can be deleted to reclaim space; no automatic retention policy
or installed-file diagnostic inventory is introduced. Cached content is verified
before reuse; concurrent external writers are outside the single-writer contract.

Packaging fixtures verify all four deterministic archives and executable modes.
Historical cache fixtures covered identity, corruption, unlisted/unsafe names and
cancellation; they retire with that cache. Actual relocation checks must run
generation, not just --version.
GUI output suggestions, final statuses, log flooding and minimum-size rendering
need front-end tests in addition to shared Core regressions.

## Prior-note Audit

[Publication boundaries](../bug-fix/2026-10-03-publication-boundaries.md) partially
overlaps: its commit/cancellation and safety rules remain authoritative.
[Deterministic archives](../bug-fix/2026-10-03-deterministic-linux-archives.md)
remains unchanged except that the same serializer handles separate front ends.
Legacy payload retirement is unrelated to host generation caching.
