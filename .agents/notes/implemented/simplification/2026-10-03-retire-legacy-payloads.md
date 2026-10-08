# Agent Note: Retire legacy Android payload inputs

Status: implemented

## Problem

Layout 8 retains obsolete runtime identities, mixed-tree hashes, deployment
revision and external-DEX promotion. MonoVM is outside the maintained Loader
target, yet these branches still complicate Patcher and its fixtures.

## Decision

Release validation, APK assembly and the explicit APK verifier support only
layout 9 with API26+ CoreCLR. Android requires embedded crypto helpers; Bionic
requires its private OpenSSL inputs. Unsupported layouts fail before assembly.
DEX promotion and game DEX/smali enumeration are removed; directory injection
requires only the native layout and preserves game code. Deployment emits only
non-seed path/policy overrides. No tree hashes, revision or runtime/Interop audit
JSON is copied or recomputed for injection.
An all-seed payload omits deploymentFiles rather than emitting an empty list.

Release hashes, ZIP/path/duplicate checks, ABI/native-collision validation and
runtime completeness remain. Required CoreLib/JIT/engine/crypto inputs must be
present and nonempty. The two crypto implementations cannot be mixed. Unknown
additive metadata/files are tolerated when they do not violate those contracts.
Installed assets remain editable without hash regeneration or startup scans.
Installed layout remains 9. Release envelopes accept formats 2 and 3: format 2
retains its existing metadata consistency checks, while format 3 derives ABI,
CoreCLR engine, crypto and game-independent layout from the payload RID and
verified inventory. The engine hash no longer needs a duplicate top-level field.
Version/source and minimum API remain required. Loader owns the
[field contract](https://github.com/LemonLoaderX/LemonLoader/blob/main/docs/android/ARTIFACTS.md).
This preserves already published archives without requiring redundant metadata
from new producers. Patcher 2.0.0 cannot read format 3; the explicit envelope
version lets it reject new archives before trying removed required properties.
Historical tools remain in Git history.

Main installation rationale belongs to Loader docs; this local note records the
Patcher contract so the repository remains independently maintainable.
README links by task to docs/USAGE.md, docs/INTEROP.md and CONTRIBUTING.md rather
than repeating command options and developer procedures. CLI help remains the
complete installed option reference; the script catalog names maintained entries.

## Alternatives considered

- Keep isolated legacy readers: preserves old releases, but maintains unused
  recipes the maintainer explicitly retires. Use historical tooling for them.
- Keep scanning game DEX for directory mode: can reconstruct old helper numbering,
  but current injection adds no DEX and gains nothing from that enumeration.
- Replace removed digests with per-file diagnostics: identifies individual files,
  but adds an unwanted framework and overhead. Keep ordinary loading errors and
  existing pre-install Release validation.

## Consequences

Layout-8, MonoVM and external-DEX releases cannot be patched by current Patcher.
Fixtures now exercise both current runtime profiles, explicit legacy rejection,
APK/directory preservation, all policies and retained security boundaries.
Explicit APK verification checks structure/policies, not authenticity of manually
edited installed content. Release validation protects input bytes before injection.
Neither validates device TLS, ART loading or Unity behavior.

## Prior-note Audit

The existing deterministic release-archive note concerns packaging bytes rather
than payload inputs and remains unchanged. No earlier Patcher payload decision
note exists; public historical release notes describe their own versions and are
not rewritten as current support promises.
