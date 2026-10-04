# Agent Note: Commit publication before cleaning backups

Status: implemented

## Problem

Interop export can replace directories containing game inputs. Directory injection
can follow links outside its root. Backup cleanup is fallible and non-atomic, so
rolling back after partial deletion destroys both the new output and old files.
Cancellation after publishing also misreports a completed operation as cancelled.

## Decision

Request validation rejects overlapping Interop export and input/output paths,
including alignment/signing executables. Tools discovered on PATH are checked
after resolution and before any export.
APK outputs likewise cannot overwrite game, Release, deployment, Interop, tool or
keystore inputs, including resolved SDK executables and application tool caches.
Write targets, protected inputs and existing ancestors reject filesystem links;
input aliases must not bypass output collision checks. Copying and APK
assembly check cancellation while operating on staging; directory injection rolls
back applied files if cancelled before its commit. Backups use cancellable copy,
and cancellation is checked again before replacing each destination, including
the last file. File/directory publication
checks cancellation before rename, and reports completion once committed.

Backup deletion happens after commit. Its failure retains new output and remaining
backup with a warning, never restores a partly deleted tree. CLI and GUI share
these Core rules; the GUI runs Core work off the dispatcher and keeps progress
delivery on its existing queue. A completed optional Interop export remains an
independent result even if subsequent APK work is cancelled.

Release cache admission uses the same full validation as explicit Release inputs;
only owned invalid caches are replaced. ZIP entry names are validated before
reading game inputs or extracting releases, including directory entries. Required
Loader/NativeHost/IL2CPP support inputs are nonempty install-time requirements,
not installed-file scans or new revision metadata.

## Alternatives considered

- Returning an error for post-commit cleanup keeps strict cleanup semantics, but
  makes success ambiguous and cannot restore deleted backup bytes. Warn instead.
- Skipping path checks for manual directory users is convenient but permits
  accidental destructive export and writes through junctions outside the input.
- Continuing cancellation after the final rename is responsive but contradicts
  the published state. Treat rename as the commit boundary.

## Consequences

Cancellation cannot promise removal of an independently requested Interop export.
Concurrent mutation of filesystem links during injection is outside this local
single-writer contract. Optional old backups may require manual cleanup after a
warning. Corrupt owned caches are redownloaded; valid cached releases remain
offline-reusable rather than polling latest on every launch.

Regression fixtures cover destructive path overlap, partial backup deletion,
junction rejection, cancelled publication, mandatory inputs, unsafe ZIP names and
cache repair through a synthetic HTTP response.

## Prior-note Audit

[Legacy payload retirement](../simplification/2026-10-03-retire-legacy-payloads.md)
partially overlaps on install-time validation; its format and editable-installed
contract remains. The deterministic archive decision concerns release packaging
bytes and is unchanged.
