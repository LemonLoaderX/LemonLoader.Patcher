# Agent Note: Keep generation provenance lightweight

Status: implemented

## Problem

Explicit Interop exports rehash game binaries, metadata, generator dependencies
and every generated DLL for a record that injection does not consume. Those reads
add work without proving compatibility or supporting any reuse workflow.

## Decision

Explicit exports retain versions, origins, existing download identity and assembly
names/sizes. They do not compute new game/tool/output digests. Ordinary temporary
generation writes no record. Download and Release validation remain unchanged.

This partially supersedes export hash production in
[composable workflows](../architecture/2026-10-04-composable-file-workflows.md).
Its ordinary file interchange and no automatic Interop cache remain authoritative.

## Alternatives considered

- Make full hashes an optional export: useful for private forensic comparisons,
  but adds another product option for metadata with no current consumer.
- Delete all provenance: removes all work but loses inexpensive version/source
  context useful for Mod development. Keep only that context.

## Consequences

Exports no longer establish content identity. DLL matching remains the caller's
responsibility, as it already is for manifest-free injection. No schema bump is
needed because runtime consumers do not interpret this host record.
