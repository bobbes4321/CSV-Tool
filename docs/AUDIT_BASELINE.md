# CSV Tool: architecture, production readiness, product and UX audit

Audit date: 2026-09-06  
Status: Draft baseline for manual review and prioritization

This document consolidates two repository audits. Findings and recommendations
are review inputs, not an approved implementation plan. Priorities, scope, and
acceptance criteria can be revised before implementation begins.

## Scope and evidence

The audits inspected project instructions, durable context, architecture and
testing documentation, manifests and assembly definitions, and the principal
Core, Schema, Editor, configuration, search, reference, recovery, and test code.
Generated folders and unrelated project content were excluded.

The audits were read-only. No fixes were implemented and no tests were executed.
“Confirmed” means demonstrated by the inspected source paths, not reproduced in
Unity. Performance, filesystem durability, accessibility, and workflow impact
were not measured in a live session. Product priorities are architectural
judgments rather than user-research results.

Source links below are relative to this document. Symbols identify the inspected
areas so the references remain useful as line numbers change.

## Project invariants

- CSV files remain the canonical data; workspace assets contain metadata only.
- Unchanged content preserves original bytes, BOM, and newline style.
- Physical record/column identity stays distinct from filtered or visual position.
- Saves remain atomic and conflict-aware; recovery must never retarget edits.
- Schema, identity, and reference configuration is explicit.
- Core and Schema remain free of Unity dependencies.

See [architecture](ARCHITECTURE.md), [testing](TESTING.md), and
[project memory](../MEMORY.md).

# Audit 1: architecture and production readiness

## Executive verdict

The package has a sound foundation for a supervised editing tool, but it is not
ready for unrestricted use with authoritative production data. The largest
problems are recovery targeting, incomplete structural-edit integration, and
inconsistent schema/reference resolution, rather than the basic CSV serializer.

## Confirmed strengths

### Exact unchanged-byte preservation

`CsvDocument.Serialize` returns the original bytes when clean. Otherwise it reuses
untouched record text and individual line endings. This preserves noncanonical
quoting and mixed newline styles outside edited records.

Evidence: [CsvDocument.cs](../Assets/CSVTool/Core/CsvDocument.cs), `Serialize`.

### Strict Unicode decoding

`CsvEncodingInfo` recognizes UTF-8/16/32 BOMs and uses exception fallbacks,
avoiding silent replacement of invalid input. Legacy encodings are unsupported
rather than guessed.

Evidence: [CsvTypes.cs](../Assets/CSVTool/Core/CsvTypes.cs), `CsvEncodingInfo`.

### Physical-coordinate discipline for ordinary edits

Controller batches preflight coordinates and permissions before mutation.
Filtered paste translates visible rows into physical records. History records
row widths and reverses batches in reverse order.

Evidence: [CsvTableController.cs](../Assets/CSVTool/Editor/Window/CsvTableController.cs),
`SetCells`; [CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs), `PasteTsv`;
[CsvEditHistory.cs](../Assets/CSVTool/Core/CsvEditHistory.cs).

### Useful save and recovery primitives

Saves stage beside the destination and replace it; conflict checks compare actual
bytes and detect source deletion. Journals flush to disk, validate format and
duplicate coordinates, and preflight original values. These are useful building
blocks despite the integration problems below.

Evidence: [CsvDocument.cs](../Assets/CSVTool/Core/CsvDocument.cs), `SaveToFile`;
[CsvRecoveryJournal.cs](../Assets/CSVTool/Editor/Recovery/CsvRecoveryJournal.cs),
`Write`, `TryRead`, and `TryApply`.

### Real dependency boundaries

Core and Schema specify `noEngineReferences`; Editor explicitly depends on them
and `Neo.EditorUI`. Workspace DTOs contain configuration rather than CSV copies.

Evidence: [Core asmdef](../Assets/CSVTool/Core/CsvTool.Core.asmdef),
[Schema asmdef](../Assets/CSVTool/Schema/CsvTool.Schema.asmdef),
[Editor asmdef](../Assets/CSVTool/Editor/CsvTool.Editor.asmdef),
[CsvWorkspaceAsset.cs](../Assets/CSVTool/Editor/Configuration/CsvWorkspaceAsset.cs).

## Critical finding

### C1 — Recovery can apply an edit to the wrong record

**Evidence status:** Confirmed source-level failure path.

`UpdateRecoveryJournal` calls the overload that fingerprints current disk bytes,
while `current.Changes` describes the loaded document.

Example: load `A,old` followed by `B,old`; an external writer swaps those rows;
edit A locally. The journal records the new disk fingerprint but A's old physical
coordinate. After restarting, recovery sees a matching fingerprint and matching
original cell value, then changes B. Original-value checks cannot distinguish
records sharing that value.

**Recommendation:** Capture the fingerprint from the exact bytes loaded and
retain it until a successful save/reload.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`UpdateRecoveryJournal`;
[CsvRecoveryJournal.cs](../Assets/CSVTool/Editor/Recovery/CsvRecoveryJournal.cs),
`Write` and `TryApply`.

## High findings

### H1 — Atomic replacement does not make conflict detection atomic

**Evidence status:** Confirmed race window; power-loss consequences untested.

`SaveToFile` checks the destination, then serializes and writes the temporary
file, then replaces the destination. An external edit between the check and
replacement is silently overwritten. There is no explicit durable flush of the
CSV temporary file; power-loss behavior depends on the filesystem.

`SaveConflictCopy` also uses direct `File.WriteAllBytes` and permits selecting the
original path, bypassing both protections.

**Recommendation:** Define supported concurrent-writer guarantees, harden the
write protocol, and make Save Copy preserve the same safety expectations.

Evidence: [CsvDocument.cs](../Assets/CSVTool/Core/CsvDocument.cs), `SaveToFile`;
[CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `SaveConflictCopy`.

### H2 — Structural undo leaves protections and presentation stale

**Evidence status:** Confirmed source-level failure path.

`InsertColumn` resolves schema again; controller `Undo`/`Redo` only rebuild search.
Insert before a name-selected read-only column, then undo: its resolved physical
index remains shifted, allowing edits to the actual protected column. Window
undo/redo also omits grid column-layout invalidation. Row insertion before the
header does not refresh the controller's cached header index.

**Recommendation:** Rebuild schema, header location, selection, and layout as one
coordinated response to structural changes, including undo/redo.

Evidence: [CsvTableController.cs](../Assets/CSVTool/Editor/Window/CsvTableController.cs),
`InsertColumn`, `Undo`, `Redo`, `InsertDataRow`;
[CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `UndoCurrent`, `RedoCurrent`.

### H3 — Insertion removes recovery for the whole session

**Evidence status:** Confirmed, documented limitation.

`UpdateRecoveryJournal` deletes the existing journal whenever structural changes
exist. Previously recoverable cell edits are lost along with insertions after a
crash or domain reload. The warning is truthful but insufficient for production
recovery guarantees.

**Recommendation:** Extend recovery to structural operations and preserve the
whole unsaved session; communicate the current limitation before an insertion.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`UpdateRecoveryJournal`; [MEMORY.md](../MEMORY.md), known limitations.

### H4 — Reference resolution can use display labels as identity

**Evidence status:** Confirmed source-level failure path.

`CsvReferenceResolver.FindColumn/FindTableColumn` use `GetHeader`, which returns
presentation labels. Renaming a source column's display label can disable its
references. A target column displayed as `id` can capture a reference intended
for another physical column whose actual header is `id`.

These helpers bypass the stronger schema resolver; autocomplete uses another
resolver. Workspace case-sensitivity policy is not consistently honored.

**Recommendation:** Use one physical selector resolver for schema, references,
navigation, and autocomplete. Keep presentation labels separate.

Evidence: [CsvValueIndex.cs](../Assets/CSVTool/Editor/Index/CsvValueIndex.cs),
reference helpers; [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`ResolveTargetColumn`;
[CsvResolvedSchema.cs](../Assets/CSVTool/Schema/CsvResolvedSchema.cs).

### H5 — CSV diagnostics do not protect edits or saves

**Evidence status:** Confirmed behavior and validation gap.

The editor forces permissive quotes; parse diagnostics are neither displayed nor
checked by save. An unclosed quoted field can absorb subsequent physical lines
into one record. Editing it serializes interpreted values and can cement
unintended structure. `StrictQuotes` only changes diagnostic severity.

Grid/controller saves lack a complete data-validation gate: types, required
values, unique identities, and reference integrity are not enforced centrally.

**Recommendation:** Surface parse diagnostics, gate unsafe saves, and provide
shared data validation across all editing surfaces and automation.

Evidence: [CsvDocument.cs](../Assets/CSVTool/Core/CsvDocument.cs), `Load`, `ParseFields`;
[CsvTableController.cs](../Assets/CSVTool/Editor/Window/CsvTableController.cs),
`Open`, `ValidateEdit`, `Save`.

### H6 — An ordinary edit can change classification after reload

**Evidence status:** Confirmed source-level failure path.

`"#id",old` loads as data because its raw text starts with a quote. Editing the
second cell produces `#id,new`: `AppendEscaped` does not preserve quoting for
comment prefixes. Reload classifies it as a comment, affecting editing,
filtering, and references. Classification is not recomputed in memory. Blank
inserted rows similarly reload as structural blanks.

**Recommendation:** Define classification-preserving serialization and explicit
classification policy for inserted/edited rows.

Evidence: [CsvRecord.cs](../Assets/CSVTool/Core/CsvRecord.cs), `AppendEscaped`;
[CsvDocument.cs](../Assets/CSVTool/Core/CsvDocument.cs), `Classify`.

## Medium findings

### M1 — Incomplete change/revert model

Extend a short row with a value, then clear it: the row remains wider and dirty,
but `IsCellDirty` excludes new empty cells, so `Changes` is empty and `RevertAll`
does nothing. Structural insertions are not faithfully represented by cell
differences; reverting an inserted populated row can leave an empty extra record.

**Status:** Confirmed. **Recommendation:** Represent shape and structural changes
explicitly in the change/revert model.

Evidence: [CsvRecord.cs](../Assets/CSVTool/Core/CsvRecord.cs), `IsCellDirty`;
[CsvDocument.cs](../Assets/CSVTool/Core/CsvDocument.cs), `RevertAll`.

### M2 — Paste silently drops overflow rows

`PasteTsv` skips rows beyond the visible map and reports success for the remainder.
Physical targeting is correct, but no explicit partial-paste warning is shown.

**Status:** Confirmed. **Recommendation:** Report affected and omitted cells before
applying a paste whose dimensions or protections differ from the selection.

Evidence: [CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs), `PasteTsv`.

### M3 — Recovery hardening gaps

Writers can exceed the reader's 128 MiB total limit. `TryApply` does not upper-bound
record indices before `GetCell`. “Keep Journal” does not isolate the retained
journal from subsequent overwrites.

**Status:** Confirmed. **Recommendation:** Align read/write limits, validate all
coordinates, and preserve incompatible journals separately.

Evidence: [CsvRecoveryJournal.cs](../Assets/CSVTool/Editor/Recovery/CsvRecoveryJournal.cs);
[CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `TryRecoverCurrent`.

### M4 — Regex can stall the editor

Token extraction uses `Regex.Matches` without timeout; configuration validation
does not compile patterns. Pathological input/configuration can hang or throw
during navigation.

**Status:** Missing safeguards confirmed; pathological runtime behavior not
reproduced. **Recommendation:** Validate patterns and bound evaluation time.

Evidence: [CsvValueIndex.cs](../Assets/CSVTool/Editor/Index/CsvValueIndex.cs),
`ExtractTokens`; [CsvSchemaValidation.cs](../Assets/CSVTool/Schema/CsvSchemaValidation.cs).

### M5 — Path confinement is lexical

Workspace discovery uses unconditional case-insensitive containment, unlike the
platform-aware configuration resolver. Symlinks/junctions are not resolved.

**Status:** Implementation confirmed; root escape through these mechanisms is a
risk requiring platform tests. **Recommendation:** Unify path policy and test
filesystem aliases and case behavior on supported platforms.

Evidence: [CsvTableController.cs](../Assets/CSVTool/Editor/Window/CsvTableController.cs),
`CsvWorkspaceController.IsWithinFolder`;
[CsvWorkspaceAsset.cs](../Assets/CSVTool/Editor/Configuration/CsvWorkspaceAsset.cs),
`CsvWorkspacePathResolver`.

## Low finding

### L1 — Distribution and reproducibility need explicit ownership

Distribution is project-shaped: no package manifest or CI workflow was found in
the restricted inventory, and `Neo.EditorUI` sits outside `Assets/CSVTool`. The
MCP dependency has a floating Git URL, although the lockfile records a commit.

**Recommendation:** Define the release package boundary and reproducible
dependency/update workflow.

Evidence: [Editor asmdef](../Assets/CSVTool/Editor/CsvTool.Editor.asmdef),
[manifest](../Packages/manifest.json), [lockfile](../Packages/packages-lock.json).

## Scaling and missing capabilities

The current design plausibly serves modest, single-user datasets. Large-scale
readiness is unproven.

| Dimension | Evidence and implication |
|---|---|
| Frequent edits | Each edit rebuilds search across rows × maximum width, scans changes, fingerprints the disk file, and rewrites/flushed recovery synchronously. This creates increasing interactive latency. |
| Structural edits | `InsertColumn` deduplicates targets with repeated `List.Contains`, making that step quadratic in row count. |
| Large files | Loading retains original bytes, raw record strings, parsed values, and baseline value lists; saving allocates full text and encoded output. Input-size limits and bounded history are absent. |
| Many tables | Opened targets remain resident. References scan target records per token. Only the current table is polled for external changes, so reference results can remain stale. |
| Multiple users | Conflict detection exists, but a coordinated concurrent-writer policy and multi-file consistency workflow are missing. |
| Automation and CI | Pure Core/Schema boundaries help, but a headless dataset validator and machine-readable diagnostics are missing. |
| Schema evolution | Explicit metadata is a good base; schema versions, migration plans, and coordinated cross-table checks are missing. |

Evidence: [CsvDocument.cs](../Assets/CSVTool/Core/CsvDocument.cs),
`Load`, `GetChanges`, `InsertColumn`, `Serialize`;
[CsvTableController.cs](../Assets/CSVTool/Editor/Window/CsvTableController.cs), `RebuildSearch`;
[CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`UpdateRecoveryJournal`, `PollExternalState`, `EnsureReferenceTargetOpen`;
[CsvValueIndex.cs](../Assets/CSVTool/Editor/Index/CsvValueIndex.cs), `ResolveToken`.

Other missing production capabilities include explicit CSV dialect/classification
options and a headerless configuration path. These fit the current design
without replacing CSV as the source of truth.

## Test coverage and gaps

Existing tests meaningfully cover physical batches, protected edits, ordinary
undo, trailing fields, multiline quoting, unchanged bytes, and conflicts occurring
before save. However, the corpus round-trip test exercises the original-byte
shortcut; it does not establish correct parsing or edited serialization.
Structural tests cover isolated Core operations, while reference tests cover a
simple happy path.

Evidence: [Core tests](../Assets/CSVTool/Core/Tests/CsvDocumentTests.cs),
[controller tests](../Assets/CSVTool/Editor/Tests/CsvTableControllerTests.cs),
[reference tests](../Assets/CSVTool/Editor/Index/Tests/CsvValueIndexTests.cs),
[recovery tests](../Assets/CSVTool/Editor/Recovery/Tests/CsvRecoveryJournalTests.cs).

Priority additions:

- Operation-sequence and property tests across edits, structural changes,
  undo/redo, save, reload, and recovery.
- Malformed CSV and recovery-journal fuzzing.
- UTF-16/32, invalid Unicode, mixed endings, and edited round trips.
- Replacement failure and concurrent-write race injection.
- Crash/domain-reload recovery and incompatible-journal retention.
- Real Grid/Record focus, paste, navigation, and structural-undo scenarios.
- Large-file latency, memory, many-table, and frequent-edit budgets.
- Supported-platform filesystem/path behavior.

Current UI helper/reflection tests do not establish complete window behavior.
Unity EditMode remains the authoritative runner; see [TESTING.md](TESTING.md).

## Top 10 architecture actions and roadmap

| Rank | Action | Relative effort |
|---|---|---|
| 1 | Bind recovery to the exact loaded baseline; regression-test external row reorder. | Small |
| 2 | Fix structural undo/revert and rebuild schema, header, selection, and layout together. | Medium |
| 3 | Preserve recovery across structural edits and retain incompatible journals separately. | Medium |
| 4 | Define concurrent-save guarantees; harden atomic/durable writes and Save Copy. | Medium–large |
| 5 | Use one physical selector resolver for schema, references, and autocomplete. | Medium |
| 6 | Surface parse errors, gate unsafe saves, and preserve classification across edits. | Medium |
| 7 | Add deterministic fault tests and generated edit/save/reload sequences for actions 1–6. | Medium |
| 8 | Extract shared dataset validation for UI and CI, including identity/reference checks. | Medium |
| 9 | Establish performance budgets; cache changes, bound work, and incrementally update indexes/search. | Medium–large |
| 10 | Add versioned schema migrations with previews, then reference-impact analysis and semantic CSV diffs. | Large, staged |

Actions 1–7 are immediate production hardening. Actions 8–9 establish automation
and scale. Action 10 provides differentiation while preserving the architecture.

## Workplace adoption assessment

**Go:** A restricted pilot using version-controlled, recoverable data and a single
writer per file.

**No-go:** Unrestricted authoritative production editing at the audited baseline.

Before trusting production data, close the critical/high findings, run the
authoritative Unity suite plus missing integration/failure tests, demonstrate
recovery across domain reloads, and publish supported file-size, encoding,
filesystem, and concurrent-writer limits.

# Audit 2: product and UX

## Product verdict

The strongest direction is a schema-aware game-data workbench: designers edit
meaningful records, programmers maintain contracts, and everyone can inspect the
consequences of changes while CSV remains authoritative.

Grid, the shared Record inspector, contextual Find, column navigation, and the
Changes panel form a useful foundation. The biggest opportunity is connecting
them into complete workflows.

## Daily workflows to optimize

| User | Representative task | Product opportunity |
|---|---|---|
| Designer | Compare units, adjust stats, inspect abilities, and review a balance change. | Saved views, bulk operations, reference previews, readable diffs. |
| Programmer | Configure a table, investigate invalid data, evolve schema, and review a contribution. | Guided metadata setup, actionable validation, CI parity, migration previews. |
| Technical artist | Maintain effect/audio identifiers and inspect dependencies. | Searchable reference pickers and explicitly configured Unity asset previews. |
| Producer | Understand what changed, who should review it, and whether a release dataset is ready. | Read-only change reports, ownership metadata, validation summaries. |

## Current interaction model

Grid supports rectangular selection, clipboard operations, F2/Enter editing,
keyboard movement, resizing, double-click autosizing, and freezing. However,
header/gutter clicks select a cell rather than a whole column/row; context menus
expose insertion rather than common editing operations. These departures from
spreadsheet expectations need completion or clearer affordances.

Evidence: [CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs),
`HandleInput`, `HandleGridCommand`.

Record mode supplies grouped fields and typed controls, but errors and help text
can occupy the same rectangle. Its additional search combines with the table
filter, creating two layers of visibility. Reference/search navigation can clear
filters and force Grid mode; the notification explains the change but offers no
restoration.

Evidence: [CsvRecordView.cs](../Assets/CSVTool/Editor/Record/CsvRecordView.cs),
`DrawField`, `DrawRecordList`;
[CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `NavigateToPhysicalCell`.

Setup is implementation-oriented: an empty window asks for an asset,
configuration uses `DrawDefaultInspector`, and workspace validation reports
counts to the Console. “Schema OK” describes selector diagnostics, not dataset
health. This matters especially for people who did not configure the workspace.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`DrawEmptyState`, `GetSchemaIssueCount`;
[CsvWorkspaceAsset.cs](../Assets/CSVTool/Editor/Configuration/CsvWorkspaceAsset.cs),
`CsvWorkspaceAssetEditor`.

## Scope and sequencing

- **Immediate UX fixes:** Make edit state, visibility, validation, and navigation
  understandable.
- **Workflow improvements:** Reduce repetitive editing and setup work.
- **Professional pipeline features:** Reuse operations and diagnostics in review
  and CI.
- **Ambitious differentiators:** Build on explicit schema relationships and
  dependable multi-table operations.

Avoid a general formula engine, arbitrary spreadsheet scripting, a bespoke Git
client, cloud collaboration infrastructure, or speculative AI editing. They add
semantics and maintenance before solving the observed workflows.

Use existing Git review and ownership processes. Expose checkout/lock status
where a studio's provider supports it; do not imply that a local badge provides
distributed locking.

Accessibility needs interaction testing. Custom-drawn cells, fixed dimensions,
and mouse-driven lists do not establish keyboard or assistive-technology
usability. Localization needs layout work because visible strings and widths are
embedded throughout the UI. Preserve invariant CSV values while localizing
presentation.

## Product acceptance testing

Existing tests cover navigation mathematics, field resolution, clipboard
serialization, and autocomplete, but not task completion across the window.
Add acceptance scenarios with each improvement:

- Keyboard-only setup, editing, navigation, and save.
- Invalid input followed by navigation or mode switching.
- Filtered paste with protected, hidden, and overflowing destinations.
- Reference navigation and return to the original editing context.
- Narrow docking, large-monitor layouts, high DPI, and long translated labels.
- Large-table searches, record browsing, and column autosizing.

Evidence: [navigation tests](../Assets/CSVTool/Editor/Tests/CsvGridNavigationTests.cs),
[Record tests](../Assets/CSVTool/Editor/Record/Tests/CsvRecordViewTests.cs),
[clipboard tests](../Assets/CSVTool/Editor/Tests/CsvGridClipboardTests.cs),
[column navigation tests](../Assets/CSVTool/Editor/Tests/CsvGoToColumnTests.cs).

## Top 15 product improvements

Ordered by expected value across professional workflows. Cost is relative;
frequency means likely use when the capability applies. Risk reduction concerns
user mistakes and operational uncertainty. These priorities remain subject to
manual review.

### P1 — Make validation actionable and consistent

**Category:** Immediate UX  
**Value:** Very high · **Frequency:** Daily · **Cost:** Medium · **Risk reduction:** High

Replace the modal schema list with an Issues panel containing severity, record
identity, field, explanation, and jump/fix actions. Separate “configuration
resolved” from “data validated.” Allocate distinct space for field errors and
help, and preserve rejected input for correction.

**Example:** A designer finds and repairs every invalid cost without opening
records individually.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`ShowSchemaIssues`; [CsvRecordView.cs](../Assets/CSVTool/Editor/Record/CsvRecordView.cs),
`DrawField`.

### P2 — Explain selection and visibility before an operation

**Category:** Immediate UX  
**Value:** Very high · **Frequency:** Continuous · **Cost:** Small–medium · **Risk reduction:** High

Show “24 of 600 records visible; 6 selected,” active filter chips, hidden-column
counts, and badges for comment/section/blank rows. Distinguish record position
from file line number and show configured identity prominently.

**Example:** A designer clearing six filtered records understands exactly what
will be affected.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`DrawStatusBar`, `DrawToolbar`; [CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs), `DrawCell`.

### P3 — Add paste preflight when scope is surprising

**Category:** Workflow improvement  
**Value:** Very high · **Frequency:** Daily · **Cost:** Medium · **Risk reduction:** High

Report destination dimensions, visible-row targeting, protected cells, overflow,
and schema errors. Offer explicit mapping for external columns while keeping
ordinary matching pastes fast.

**Example:** Preview an Excel balance block and identify three unmapped columns
before applying it as one operation.

Evidence: [CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs), `PasteTsv`.

### P4 — Introduce reusable table views

**Category:** Workflow improvement  
**Value:** Very high · **Frequency:** Daily · **Cost:** Medium–large · **Risk reduction:** Medium

Save column visibility/order/widths, freeze boundary, filters, and view-only
sorting. Separate personal preferences from shared role presets. Sorting must
not rewrite CSV order. Current UI exposes substring filtering and frozen-count
buttons, but no data sorting or view presets.

**Example:** A “Combat balance” view shows identity, HP, damage, and cost, sorted
by cost.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `DrawToolbar`;
[CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs), `CsvGridSettings`.

### P5 — Provide a small, typed bulk-operation toolkit

**Category:** Workflow improvement  
**Value:** Very high · **Frequency:** Daily · **Cost:** Medium · **Risk reduction:** High

Start with fill down, set selected cells, numeric offset/scale, and clear, with
affected-record previews and one undo step. Surface them in selection/header
context menus. Avoid an unrestricted formula language.

**Example:** Increase selected enemy HP by 10%, inspect rounding, then apply.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`OnStructureMenuRequested`; [CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs), `ClearSelection`.

### P6 — Complete keyboard-first editing and expose shortcuts

**Category:** Immediate UX  
**Value:** High · **Frequency:** Continuous · **Cost:** Medium · **Risk reduction:** Medium

Add non-editing Tab traversal, whole-row/column selection, keyboard reference
opening, and a compact searchable command menu. Route Save consistently across
Grid and Record while making text undo versus document undo predictable.

**Example:** Edit ten records and save without reaching for the mouse.

Evidence: [CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs),
`HandleKeyboard`, `HandleGridCommand`;
[CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `HandleWindowShortcuts`.

### P7 — Preserve navigation context

**Category:** Immediate UX  
**Value:** High · **Frequency:** Daily · **Cost:** Medium · **Risk reduction:** Medium

Add Back/Forward restoring table, record, column, filter, scroll, and mode. Offer
reference peek before leaving the current table; make filter resets reversible.

**Example:** Inspect an ability from a filtered unit list, then return exactly
where editing stopped.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`NavigateToPhysicalCell`, `OnReferenceNavigationRequested`.

### P8 — Turn Changes into a reviewable change set

**Category:** Professional pipeline feature  
**Value:** High · **Frequency:** Daily or per review · **Cost:** Medium–large · **Risk reduction:** High

Group by configured identity, show full before/after values and structural
operations, allow selected reverts, and export a human-readable report. Extend
comparison to a selected Git revision through a small adapter.

**Example:** A producer reviews “18 units adjusted” with field-level differences
rather than a long list of row numbers.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `DrawChangesPanel`.

### P9 — Make layouts and settings deliberate

**Category:** Immediate UX  
**Value:** High · **Frequency:** Continuous · **Cost:** Medium · **Risk reduction:** Medium

Add responsive toolbar overflow, resizable table/record/changes panes, readable
density/font options, visible focus, and non-color status cues. Persist layout
per workspace/table with Reset View. Virtualize long lists and bound autosizing
work. Localize UI strings without changing stored identifiers.

**Example:** Use a compact docked window, then a wide comparison layout on a large
monitor without rebuilding the view manually.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `DrawCurrentView`;
[CsvRecordView.cs](../Assets/CSVTool/Editor/Record/CsvRecordView.cs), `DrawRecordList`;
[CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs), `AutoSizeColumn`.

### P10 — Add guided setup and usable metadata editing

**Category:** Workflow improvement  
**Value:** High · **Frequency:** Onboarding/schema changes · **Cost:** Medium · **Risk reduction:** High

Empty-state actions should create/select a workspace, choose a root, preview
discovered tables, and explicitly select identity/display columns. Use header
pickers with physical positions, named groups, and inline configuration errors.

**Example:** A programmer configures duplicate headers correctly, then gives
designers a readable form without exposing nested DTO lists.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `DrawEmptyState`;
[CsvWorkspaceAsset.cs](../Assets/CSVTool/Editor/Configuration/CsvWorkspaceAsset.cs),
`CsvWorkspaceAssetEditor`.

### P11 — Unify search scopes and add previewed replace

**Category:** Workflow improvement  
**Value:** High · **Frequency:** Daily · **Cost:** Medium · **Risk reduction:** High

Keep contextual Find, but distinguish current record, selected columns, visible
records, table, and workspace. Add table quick-switch, favorites, and recent
tables. Start replacement with literal matching and explicit counts.

**Example:** Replace a deprecated sound identifier only in the sound column, then
inspect every affected record.

Evidence: [CsvContextualFindPopup.cs](../Assets/CSVTool/Editor/Search/CsvContextualFindPopup.cs);
[CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `DrawSidebar`.

### P12 — Provide a persistent recovery/conflict workspace

**Category:** Immediate UX  
**Value:** High · **Frequency:** Occasional, consequential · **Cost:** Medium–large · **Risk reduction:** Very high

Show recovery timestamp, recoverable scope, local/disk comparison, export, and
explicit restore/discard outcomes. Surface unavailable recovery before structural
actions; link retained journals to usable recovery actions. This depends on the
architecture audit's recovery fixes.

**Example:** After a domain reload, inspect the recovered changes before saving.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`SaveCurrent`, `TryRecoverCurrent`.

### P13 — Use the same validation in the editor and CI

**Category:** Professional pipeline feature  
**Value:** Very high · **Frequency:** Every integration · **Cost:** Medium–large · **Risk reduction:** Very high

Export stable diagnostics with table, configured identity, physical location,
rule, and severity, with links back into the tool. Include dataset/schema
revision in reports. Add ownership/reviewer metadata without inventing a separate
approval system.

**Example:** A pull request fails on a missing ability reference, and its author
opens the precise field.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `ShowSchemaIssues`;
[CsvWorkspaceAsset.cs](../Assets/CSVTool/Editor/Configuration/CsvWorkspaceAsset.cs).

### P14 — Support templates, duplication, and appropriate field editors

**Category:** Workflow improvement  
**Value:** High · **Frequency:** Frequent content creation · **Cost:** Medium · **Risk reduction:** Medium

Templates should define explicit defaults and require a new identity, never
guess one. Add expandable multiline editing and structured token/reference
pickers while retaining raw-value access.

**Example:** Duplicate a weapon archetype, enter its ID, choose an effect by
display name, and edit its description comfortably.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `InsertRow`;
[CsvRecordView.cs](../Assets/CSVTool/Editor/Record/CsvRecordView.cs), `DrawField`.

### P15 — Build dependency-aware change planning

**Category:** Ambitious differentiator  
**Value:** High · **Frequency:** Periodic, high leverage · **Cost:** Large · **Risk reduction:** High

Begin with “used by” lists and reference previews. Later preview key renames and
schema migrations across affected tables as one reviewable plan. Add opt-in
Unity asset previews through explicit adapters. This requires dependable
reference resolution and coordinated multi-file operations first.

**Example:** Before renaming an ability, inspect every referencing unit and all
proposed edits.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`OnReferenceNavigationRequested`;
[CsvWorkspaceAsset.cs](../Assets/CSVTool/Editor/Configuration/CsvWorkspaceAsset.cs),
reference metadata.

## Recommended product direction

**Edit, understand, validate, and review game data in context.**

Make Grid plus Inspector the primary authoring surface, saved views the daily
workflow accelerator, and shared validation/change reporting the professional
backbone. Dependency-aware previews and migrations can then become the
distinctive capability that justifies using CSV Tool alongside established
spreadsheet tools.

## Manual review notes

Use this space to record changes to the proposed baseline before implementation.

- Accepted priorities:
- Deferred or rejected ideas:
- Target users and representative datasets:
- Required production-adoption conditions:
- First implementation milestone:
