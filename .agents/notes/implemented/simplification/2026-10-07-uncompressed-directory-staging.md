# Agent Note: Avoid compression in transient directory staging

Status: implemented

## Problem

Directory injection uses the same ZIP payload assembler as APK injection, then
immediately extracts that temporary archive into its transactional overlay. The
temporary deflate/inflate work consumes CPU without reducing the published
directory's size. It is not part of an archive distribution requirement. Extracting
to a separate overlay and copying that overlay into the destination transaction
also reads and writes the entire payload twice.

## Decision

The internal payload merge accepts the compression policy chosen by its caller.
APK injection retains optimal compression for assets and stored native libraries.
Directory injection stores the temporary payload without compression, then extracts
it directly into the destination transaction's staging directory. DirectoryInjector
owns staging and cleanup; its caller supplies payload preparation. The final file
moves and backups remain on the destination volume, even when the temporary ZIP is
on another volume. No intermediate overlay copy or cross-volume rename is needed.

The game root rejects links before staging starts. All destination validation and
the post-preparation cancellation check complete before any game file is replaced.
Preparation failures clean staging without changing game files; publication failures
retain the existing rollback behavior. Validation exceptions retain their type.

All path, duplicate, native collision, runtime completeness, policy and cancellation
checks remain shared. This is an internal choice, not a new CLI option, output
format, cache or second payload assembler.

## Alternatives considered

- Fastest compression for final APK assets reduces packaging CPU but increases
  the distributed APK size. It is not selected as the default tradeoff.
- Writing directory payloads directly avoids the temporary ZIP entirely, but
  duplicates assembly/validation paths or requires a larger writer abstraction.
  Removing the unnecessary compression achieves a bounded improvement first.
- Keeping optimal compression in staging reduces temporary disk usage, but pays
  for compression and decompression of files published immediately afterward.
- Moving an independently extracted overlay can save a copy on the same volume,
  but needs a cross-volume fallback and transfers cleanup ownership after creation.
  Preparing directly in the transaction removes that branch and the extra copy.
- Extracting directly over game files eliminates staging too, but can expose a
  partial installation on cancellation or extraction failure. Staging remains.

## Consequences

The temporary ZIP is larger without compression; removing the separate overlay
reduces other temporary storage and payload I/O. Final directory contents and APK
compression remain unchanged. Real-input comparisons check every output file,
and scripts/test.ps1 covers APK/directory content, conflicts, interrupted preparation,
cancelled publication and previous-output preservation. Performance measurements
and their input identities remain private under ignored output.

## Prior-note audit

[Composable workflows](../architecture/2026-10-04-composable-file-workflows.md)
retains one Core implementation and ordinary DLL inputs. [Publication boundaries](../bug-fix/2026-10-03-publication-boundaries.md)
retains transactional output and cancellation semantics. Both partially overlap
and remain in force. Download ownership, Unity header detection, retired payloads
and deterministic release archives are independent and unchanged.
