# Agent Note: Read Unity versions from bounded file headers

Status: implemented

## Problem

Whole-file regular-expression scanning of globalgamemanagers can match unrelated
version strings in asset data and fails when more than one distinct match exists.
Games that store globalgamemanagers inside data.unity3d have no standalone input
for that detector. Extracting entire game assets solely for version detection
also adds avoidable I/O and memory use.

## Decision

Explicit version overrides take priority. APK/directory detection then tries
globalgamemanagers, mainData and data.unity3d in that order, using direct streams.
Standalone files use the SerializedFile metadata version after the parsed header;
formats 9-21 use the 20-byte header and format 22 uses its 48-byte extended header.
The serialized format integer and payload endianness never become engine versions.

Bundle detection recognizes UnityFS/UnityWeb/UnityRaw signatures and reads their
engine revision after the player/compatibility version. UnityFS formats 6-8 are
supported; a compatibility value such as 5.x.x is never used as the engine version.
Only bounded ASCII fields are read. Engine strings are validated as complete
version fields, not searched within arbitrary text. No Bundle block decompression,
type database, object parsing or complete asset copy is needed.

Unknown/truncated/invalid headers fall through to the next known source. If no
source provides a valid version, the error requests an explicit override. I/O
failures and cancellation still propagate. ZIP safety validation precedes APK
reads, including explicit-override runs. Extraction returns the resolved version
and copies only the native game binary and IL2CPP metadata required by the tools.

This follows the header/metadata distinction in
[AssetStudio SerializedFile](https://github.com/Perfare/AssetStudio/blob/master/AssetStudio/SerializedFile.cs)
and [UnityPy BundleFile](https://github.com/K0lb3/UnityPy/blob/master/UnityPy/files/BundleFile.py).
The small reader intentionally covers version discovery, not general asset loading.

## Alternatives considered

- Search data.unity3d as text: easy to add, but compressed contents and unrelated
  strings recreate the same ambiguity. Read its explicit header fields instead.
- Prefer data.unity3d exclusively: its header is cheap, but many games use only
  standalone serialized files. Keep both layouts without guessing.
- Import a full asset library: offers broad object/Bundle decoding, but this
  operation needs only bounded version fields. Avoid new dependencies and costly
  decoding; unsupported/custom headers retain the existing manual override.

## Consequences

Version detection no longer reads an entire asset or materializes its text. It
works through non-seekable ZIP streams and ignores version-like body strings.
It does not verify the complete game asset or prove that a replaced asset matches
the running engine. Encrypted/custom bundles, stripped versions and unsupported
formats still need a user-supplied version; no guessed fallback is introduced.

Fixtures cover both current serialized header layouts/endian flags, extended file
sizes, Bundle signatures/format versions, compatibility vs engine strings,
truncation, invalid bounds, unknown versions, bounded non-seekable reads, source
precedence/fallback, overrides and cancellation. APK/directory extraction fixtures
assert that neither globalgamemanagers nor data.unity3d is copied into tool inputs.

## Prior-note Audit

[Patcher-owned downloads](../architecture/2026-10-03-patcher-owned-downloads.md)
remains unchanged: generation is temporary and download roots belong to Patcher.
[Independent front ends](../architecture/2026-10-03-independent-frontends-and-interop-reuse.md)
concerns packaging, normalization and logs, not version discovery.
No earlier active note owns the whole-file version scanning decision.
