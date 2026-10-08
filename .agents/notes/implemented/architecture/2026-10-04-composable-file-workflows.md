# Agent Note: Compose generation and injection through ordinary files

Status: implemented

## Problem

The combined patch pipeline requires generation for every injection and uses a
patch request for generator inputs. Independent Mod development, scripted tool
invocation and injection using existing DLLs cannot select only their needed
stage. Custom-generator post-processing retains Cecil solely to repair metadata
already fixed in the producing fork. Temporary generation records also hash files
that are then deleted without use.

## Decision

Export digest production below is partially superseded by
[lightweight provenance](../simplification/2026-10-04-lightweight-interop-provenance.md).

InteropRequest/InteropPipeline own independent generation from APK/directory or
explicit binary/metadata/version. They require no Loader Release, deployment,
signing or native main/Unity startup layout. GameInteropGenerator accepts this
request rather than PatchRequest. CLI generate-interop and GUI's generation mode
use the same pipeline with transactional output replacement.
Core types use the product namespace. PatchRequest composes InteropRequest and
ApkPostProcessingOptions rather than duplicating their fields and validation.
Independent processing does not convert its request to an injection request.

Pipeline constructors own full request normalization and validation. CLI parsing
only constructs requests, and the CLI maps constructor failures before execution;
it does not classify runtime failures as usage errors. Tool resolution introduces
new paths, so injection rechecks those paths against the APK and optional Interop
export destinations. Repeating every request check adds filesystem work without
strengthening that resolved-tool boundary; path and overwrite checks remain.

Injection consumes ordinary top-level DLLs via --interop. inject requires this
input and rejects generation options; patch can generate or consume existing
DLLs. Existing DLLs need no Patcher manifest, Unity version or generation-tool
discovery/download. Injection retains installed layout/native collision/Release
validation and output publication/rollback. DLL path/duplicate/nonempty validation
protects host writes; no hash identity or invented game-match admission exists.
The user owns matching external DLLs to the exact game's binary/metadata.

ApkProcessingPipeline exposes SDK alignment/signing separately via process-apk
and GUI. It requires an actual operation, distinct input/output and safe ZIP paths;
it does not require a Loader or Unity layout. patch/inject may still compose these
operations. All front ends share the same implementations and cancellation rules.

Generated DLL rewriting and Patcher's Mono.Cecil reference are removed. Custom
generators own correct output. Host manifests and input/tool/output hashes are
created only for explicit generation exports; transient default patch generation
still checks that output DLLs exist. Bundled generator provenance and verified
downloads remain checked. There is no new persistent Interop cache.

README presents file stages and makes Patcher optional; WORKFLOW documents actual
generator commands, installed mappings, minimal JSON and SDK operations. Loader
owns installed semantics. Documentation is procedure/contract, never local test
state. No new repositories, plugin mechanism or intermediate package format exist.
AGENTS.md provides the short source/command entry point; CONTRIBUTING.md owns
development, style, test selection and output cleanup rules. These documents remain
local to Patcher so its independent checkout needs no parent handbook. Task status
and per-run evidence remain in ignored output rather than another public guide.

## Alternatives considered

- Split executables/repositories for every stage: can isolate distribution, but
  multiplies builds/version coordination for one product. Expose focused requests
  and commands first, while preserving a one-click combined workflow.
- Automatically cache generated DLLs: accelerates repeated work but retains
  unwanted persistent storage/identity logic. Explicit DLL input leaves ownership
  and lifetime with the caller.
- Repair arbitrary generators after invocation: accepts older output, but hides
  producer bugs and maintains unnecessary binary rewriting. Require correct tools.
- Demand a Patcher manifest for external DLLs: could carry generation identity,
  but couples scripts to this product and cannot establish ABI correctness by
  itself. Ordinary DLLs are the interchange contract.

## Consequences

Only-generation and only-injection are independently usable. Injection never
claims a detected Unity version when it did not read one. Existing valid patch
commands remain composed convenience workflows. External DLL selection remains a
user responsibility; broad custom-generator compatibility is deliberately retired.
Explicit exports retain development evidence and its cost; ordinary transient
generation avoids that work. Input-generation errors and failed tool runs preserve
previous exports, and failed SDK runs preserve previous APKs.

Tests cover manifest-free injection without generation-binary/version/tool inputs,
APK/directory preservation, conflict/cancellation behavior, raw generation requests,
CLI stage option isolation, GUI mode visibility and absence of Cecil references.
Real generation/SDK checks complement fixtures; they do not qualify installed games.

## Prior-note Audit

[Independent front ends](2026-10-03-independent-frontends-and-interop-reuse.md)
partially overlaps: separate archives/logs remain; custom-generator normalization
is superseded by producer-owned correctness.
[Patcher-owned downloads](2026-10-03-patcher-owned-downloads.md) remains authoritative
for download lifetime and no automatic Interop caching.
[Unity header reads](../bug-fix/2026-10-04-read-unity-version-headers.md) remains the
generation version-discovery contract, now owned by the generation request.
[Publication boundaries](../bug-fix/2026-10-03-publication-boundaries.md) retains
export safety and commit/cancellation rules across all stages.
