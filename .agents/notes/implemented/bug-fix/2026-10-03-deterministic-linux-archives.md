# Agent Note: Cross-process deterministic Linux archives

Status: implemented

## Problem

System.Formats.Tar generates PAX extended-header names using Environment.ProcessId.
Fixing ordinary entry modification times does not make the resulting tar.gz bytes
stable between packaging processes. Same-process repack tests miss this behavior.

## Decision

Linux packaging uses the standard library's GNU tar writer and GNU entries, with
fixed modification, access and change times. This retains long filenames and Unix
executable modes without process-specific PAX header names or archive rewriting.
Windows ZIP packaging is unchanged. Consumers use ordinary tar.gz extraction;
there is no new product/schema version or requirement to reject historical PAX.

The script regression invokes the real packager in separate PowerShell processes
against identical synthetic publish trees. It compares both archives/checksums,
then reads Linux entries to verify long paths, permissions, ownership and time.
It runs in the normal script suite and uses only isolated Output/Tests fixtures.

## Alternatives considered

- Keeping PAX supports arbitrary paths, but its hidden header name has no public
  deterministic-name override. Rewriting raw tar bytes or using private reflection
  introduces a second serializer and fragile coupling to runtime internals.
- USTAR avoids the PID but limits representable paths. GNU retains long-path
  support through the existing structured API and ordinary tar interoperability.
- Comparing extracted contents only checks payload equivalence but misses the
  checksum instability that affects reproducibility and publication auditing.

## Consequences

New Linux archives use GNU headers; existing PAX archives remain readable.
No binaries, dependencies or APK payloads change. Identical published files can
be repackaged deterministically across processes on the same toolchain; this does
not claim cross-runtime gzip equivalence or deterministic recompilation.
