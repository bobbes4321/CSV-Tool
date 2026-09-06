# Audit execution ledger

Last updated: 2026-09-06  
Review baseline: `docs/AUDIT_REVIEW.md` (draft; its recommendations are not automatic scope approval).

## Scope and baseline

- Tree at intake: modified `AGENTS.md`; untracked `docs/AUDIT_BASELINE.md` and
  `docs/AUDIT_REVIEW.md`. These pre-existing changes are preserved.
- Project version: Unity `6000.3.10f1`. Three interactive `Unity.exe` processes
  were present at intake; no batch Unity process may be started in this milestone.
- The approved implementation scope is H2, H4, H5, M2, and M4. C1, H1, H3 and
  the product proposals remain deferred unless explicitly added to scope.

## Correctness milestone ledger

| ID | Status | Affected files / symbols | Dependencies | Proposed implementation | Required regression evidence | Unresolved decisions | Milestone |
| --- | --- | --- | --- | --- | --- | --- | --- |
| H2 | fixed | `CsvTableController` structural methods, `Undo`, `Redo`, `FindHeader`, `ResolveSchema`; `CsvToolWindow.UndoCurrent/RedoCurrent`; `CsvGrid` caches | physical document history; grid / record projections | `StructuralRevision` triggers the controller rebuild and a window refresh of map, layout, indexes, and record projection. | New controller sequences cover row-before-header and read-only-column insertion through undo/redo; connected Unity EditMode validation passed 48/48. | Core history still exposes no operation kind; controller detects changed shape/header location. | 1–2 |
| H4 | fixed | `CsvTableController.TryResolvePhysicalColumn`; reference, autocomplete, Record, and Go To Column paths | H2 schema refresh; workspace case-sensitivity policy | One controller resolver accepts raw headers/configured indices, rejects ambiguity, and excludes display labels; ambiguous target tables return `Ambiguous`. | Label-collision, duplicate-header, explicit-index, timeout, and duplicate-target-table regressions; connected Unity EditMode validation passed 48/48. | Reference DTOs remain name selectors; a future explicit reference-index DTO would improve configuration ergonomics but is not needed for unambiguous resolution. | 1–2 |
| H5 | fixed | `CsvDatasetValidator`; `CsvTableController.Save`; `CsvToolWindow.SaveCurrent`; workspace/reference collection | H4 physical reference resolver | Shared no-Unity-API Editor service creates stable physical diagnostics; save overload validates the workspace before core save without blocking unrelated unopened tables. | Validator and save-gate regressions cover types, required, range, enum, identity, references, and unopened unrelated tables; connected Unity EditMode validation passed 48/48. | Automation must call `CsvTableController.Save(validationTables)` rather than bypassing it through `CsvDocument.SaveToFile`. | 1–2 |
| M2 | fixed | `CsvGrid.PasteTsv`; clipboard tests; window permission callback | controller batch permission preflight | Paste now builds a physical preflight and refuses every partial condition before events/mutation. | Planner tests prove overflow/protected destinations are reported without document mutation; connected Unity EditMode validation passed 48/48. | P3 confirmation/mapping UI remains deferred; this safety milestone deliberately rejects surprising partial pastes. | 1–2 |
| M4 | fixed | `CsvSchemaValidation`; `CsvReferenceResolver.ExtractTokens`; schema/index tests | .NET regex APIs available to no-Unity assemblies | Schema compilation validates pattern/group; runtime extraction uses a shared 100 ms timeout and returns `Invalid`. | Schema tests cover malformed/missing group; Index test covers catastrophic pattern timeout; connected Unity EditMode validation passed 48/48. | Timeouts bound evaluation time but not all possible memory pressure from extremely many matches; token-count caps are deferred until measured. | 1–2 |

## Product backlog (planning only)

| Stage | Items | Rationale / dependency |
| --- | --- | --- |
| UX-1: actionable safety | P1, P2, P3 | Build on H5 diagnostics, physical selection clarity, and M2 preflight. Smallest coherent slice: an issues panel plus visibility/selection summary and paste-preflight presentation. |
| UX-2: daily editing | P6, P7, P9, P10, P11 | Keyboard flow, reversible navigation, responsive layout, guided explicit configuration, and scoped search follow the safety foundation. |
| UX-3: review and reuse | P4, P8, P13 | Saved non-destructive views and reviewable change reports depend on stable physical selection and shared diagnostics; CI export comes after diagnostic schema stabilization. |
| Audit numbering gap | P5, P12, P14, P15 | `AUDIT_REVIEW.md` contains no proposals for these IDs. They are blocked pending a reviewed definition; no feature is inferred. |

The recommended first UX milestone is UX-1: it has the highest risk reduction,
depends directly on the correctness work, and has narrow observable acceptance
tests. It deliberately does not implement saved views, navigation history, or CI
transport.

## UX-1 execution

| IDs | Status | Scope | Acceptance evidence | Deferred boundary |
| --- | --- | --- | --- | --- |
| P1, P2, P3 | fixed | `CsvToolWindow`: an Issues panel presents shared schema/data diagnostics with physical jump actions; the status bar reports visible versus eligible rows, rectangular selection size, and configured identity; the existing all-or-nothing paste preflight is surfaced with an explicit rejection dialog. | Connected Unity EditMode (`CsvTool.Editor.Tests`): 43 passed, 0 failed, 0 skipped. | No edit-in-place fix controls, persistence, external-column mapping, or paste confirmation flow. Diagnostics are recomputed during IMGUI repaint; cache/throttle before large-dataset work. |

## Deferred findings

| ID | Status | Reason / next action |
| --- | --- | --- |
| C1 | deferred | Recovery fingerprint targeting is a separate correctness risk. It needs a dedicated loaded-bytes fingerprint lifecycle and recovery sequence tests before implementation. |
| H1 | deferred | Atomic check-and-replace race needs a platform-specific save-semantics decision and test strategy. |
| H3 | deferred | Audit action is not in the approved Milestone 1 list; retain source-of-truth/data-safety scope until separately prioritized. |
