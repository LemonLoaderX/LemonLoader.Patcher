# Agent Note: Patcher-owned downloads without Interop caching

Status: implemented

## Problem

Output-adjacent .tools directories spread persistent downloads across game
directories and download the same inputs again when output locations change.
Automatic Interop reuse adds a second persistent copy of large game-specific
assemblies, identity hashing and cache maintenance for repeated generation that
is not the normal user workflow. Tool terminal sequences also appear as unreadable
text because GUI logs are plain text rather than a terminal emulator.

## Decision

Download roots use AppContext.BaseDirectory/.tools, independent of game paths,
output paths and the working directory. Patch runs and unity-dependencies share
the same UnityDependencies subdirectory. An explicit unity-dependencies --cache
still overrides its root. Separate front-end installations own their downloads;
the application directory must be writable when downloads are needed. No fallback
scatters files into game directories. Existing download and Release validation
remains authoritative.

Interop is generated only in the disposable per-run workspace. Only an explicit
--interop-output retains an export. The Interop cache, key, restoration/publication
code and force-regeneration controls are removed. Cached assemblies are not
migrated. Old output-adjacent downloads may be moved manually to the application
cache, then undergo normal validation; Patcher never searches game parents for
old caches or deletes their contents implicitly.

Core requests non-colored child output and removes CSI/OSC terminal sequences
before forwarding tool messages. GUI display, copy/save and CLI diagnostics
therefore receive the same plain text, including tools that ignore NO_COLOR.
No terminal renderer or GUI-specific reinterpretation is introduced.

## Alternatives considered

- Default-off Interop caching avoids normal disk growth while permitting repeat
  work, but retains an unwanted cache subsystem and controls. Remove it instead.
- Output-adjacent downloads are writable beside outputs but couple reusable tools
  to unrelated game locations. The executable owns persistent downloads instead.
- A per-user central cache supports read-only installations and cross-installation
  sharing, but hides data outside the portable Patcher directory the maintainer
  requests. Use the application directory without a silent fallback.
- Asking tools to disable color costs almost nothing, but some tools force ANSI.
  Strip their control sequences at the shared output boundary as well.

## Consequences

Patching another directory reuses downloaded tools/references rather than leaving
another .tools tree. Normal generation retains no game-specific Interop cache;
repeat generation runs the tools again. Moving the whole Patcher directory keeps
its downloads together. Old scattered caches require explicit relocation or
cleanup. Invalid cached downloads still reject or repair; Interop export overlap
and ZIP/ABI/runtime safety rules remain unchanged.

Regression fixtures cover APK/directory/output-independent roots, shared Unity
download roots, explicit overrides, absence of cache identity in exported metadata,
and actual child stdout/stderr with colors, erase/cursor controls and OSC hyperlinks.
Terminal-only lines do not become empty log rows; visible message text is preserved.

## Prior-note Audit

[Independent front ends and reusable generation](2026-10-03-independent-frontends-and-interop-reuse.md)
partially overlaps. Its automatic-cache decision is superseded; separate archives,
adjacent bundled tools, redundant-normalization removal and virtualized logs remain.
[Publication boundaries](../bug-fix/2026-10-03-publication-boundaries.md) retains
download/cache validation, export safety and cancellation rules. Legacy payload
retirement is unrelated to host download lifetime.
