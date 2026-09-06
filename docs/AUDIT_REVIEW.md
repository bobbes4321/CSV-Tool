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

## High findings

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

### H5 — Validation is not centralized before save

**Evidence status:** Confirmed behavior and validation gap.

Grid/controller saves lack a complete data-validation gate: types, required
values, unique identities, and reference integrity are not enforced centrally.
This can lead to duplicated or inconsistent validation behavior across editing
surfaces.

**Recommendation:** Provide shared data validation and one coordinated save gate
across all editing surfaces and automation.

[CsvTableController.cs](../Assets/CSVTool/Editor/Window/CsvTableController.cs),
`Open`, `ValidateEdit`, `Save`.

## Medium findings

### M2 — Paste silently drops overflow rows

`PasteTsv` skips rows beyond the visible map and reports success for the remainder.
Physical targeting is correct, but no explicit partial-paste warning is shown.

**Status:** Confirmed. **Recommendation:** Report affected and omitted cells before
applying a paste whose dimensions or protections differ from the selection.

Evidence: [CsvGrid.cs](../Assets/CSVTool/Editor/Grid/CsvGrid.cs), `PasteTsv`.

### M4 — Regex can stall the editor

Token extraction uses `Regex.Matches` without timeout; configuration validation
does not compile patterns. Pathological input/configuration can hang or throw
during navigation.

**Status:** Missing safeguards confirmed; pathological runtime behavior not
reproduced. **Recommendation:** Validate patterns and bound evaluation time.

Evidence: [CsvValueIndex.cs](../Assets/CSVTool/Editor/Index/CsvValueIndex.cs),
`ExtractTokens`; [CsvSchemaValidation.cs](../Assets/CSVTool/Schema/CsvSchemaValidation.cs).

## Scaling and missing capabilities

The current design plausibly serves modest, single-user datasets. Large-scale
readiness is unproven.

| Dimension | Evidence and implication |
|---|---|
| Automation and CI | Pure Core/Schema boundaries help, but a headless dataset validator and machine-readable diagnostics are missing. |
| Schema evolution | Detect added, removed, renamed, duplicate, or reordered headers when opening a table; show the difference from configured schema and require explicit updates to affected column mappings and references. A later migration system can build on this. |

Evidence: [CsvDocument.cs](../Assets/CSVTool/Core/CsvDocument.cs),
`Load`, `GetChanges`, `InsertColumn`, `Serialize`;
[CsvTableController.cs](../Assets/CSVTool/Editor/Window/CsvTableController.cs), `RebuildSearch`;
[CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`UpdateRecoveryJournal`, `PollExternalState`, `EnsureReferenceTargetOpen`;
[CsvValueIndex.cs](../Assets/CSVTool/Editor/Index/CsvValueIndex.cs), `ResolveToken`.

## Test coverage and gaps

Existing tests meaningfully cover physical batches, protected edits, ordinary
undo, trailing fields, multiline quoting, unchanged bytes, and conflicts occurring
before save. The most valuable remaining gap is coverage of realistic sequences
that combine structural edits, schema/reference resolution, validation, paste,
save, and reload.

Evidence: [Core tests](../Assets/CSVTool/Core/Tests/CsvDocumentTests.cs),
[controller tests](../Assets/CSVTool/Editor/Tests/CsvTableControllerTests.cs),
[reference tests](../Assets/CSVTool/Editor/Index/Tests/CsvValueIndexTests.cs),
[recovery tests](../Assets/CSVTool/Editor/Recovery/Tests/CsvRecoveryJournalTests.cs).

Priority additions:

- Operation-sequence and property tests across edits, structural changes,
  undo/redo, save, reload, schema resolution, and validation.
- UTF-16/32, invalid Unicode, mixed endings, and edited round trips.
- Real Grid/Record focus, paste, navigation, and structural-undo scenarios.
- Regex validation and timeout behavior for configured token extraction.
- Schema-change detection and explicit mapping/reference-update scenarios.

Current UI helper/reflection tests do not establish complete window behavior.
Unity EditMode remains the authoritative runner; see [TESTING.md](TESTING.md).

## Curated technical actions and roadmap

| Rank | Action | Relative effort |
|---|---|---|
| 1 | Fix structural undo/revert and rebuild schema, header, selection, and layout together. | Medium |
| 2 | Use one physical selector resolver for schema, references, and autocomplete. | Medium |
| 3 | Extract shared dataset validation for UI and CI, including identity/reference checks. | Medium |
| 4 | Add partial-paste warnings and regex validation/timeouts. | Small–medium |
| 5 | Add deterministic edit/save/reload sequence tests and real Grid/Record scenarios. | Medium |
| 6 | Detect schema changes on open and preview required mapping/reference updates. | Medium, staged |

Actions 1–5 are practical correctness and usability hardening. Action 6 provides
a staged path for schema evolution while preserving the architecture.

## Workplace adoption assessment

**Good fit:** Version-controlled, single-user editing of the project’s current
CSV assets, with the retained structural, reference, validation, paste, regex,
schema-change, and regression-test work completed incrementally.

The current file sizes and observed editor responsiveness do not justify separate
large-file or performance work at this stage.

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

Add Back/Forward commands—available as visible buttons, keyboard shortcuts, and
mouse side buttons where Unity exposes them—restoring table, record, column,
filter, scroll, and mode. Offer reference peek before leaving the current table;
make filter resets reversible.

**Example:** Inspect an ability from a filtered unit list, then return exactly
where editing stopped.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs),
`NavigateToPhysicalCell`, `OnReferenceNavigationRequested`.

### P8 — Turn Changes into a reviewable change set

**Category:** Professional pipeline feature  
**Value:** High · **Frequency:** Daily or per review · **Cost:** Medium–large · **Risk reduction:** High

Phase 1: group by configured identity and show full before/after values and
structural operations in the existing Changes panel. Selective reverts, report
export, and Git-revision comparison are deferred.

**Example:** A producer reviews “18 units adjusted” with field-level differences
rather than a long list of row numbers.

Evidence: [CsvToolWindow.cs](../Assets/CSVTool/Editor/Window/CsvToolWindow.cs), `DrawChangesPanel`.

### P9 — Make layouts and settings deliberate

**Category:** Immediate UX  
**Value:** High · **Frequency:** Continuous · **Cost:** Medium · **Risk reduction:** Medium

Add responsive toolbar overflow, resizable table/record/changes panes, readable
density/font options, visible focus, and non-color status cues. Persist layout
per workspace/table with Reset View.

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
