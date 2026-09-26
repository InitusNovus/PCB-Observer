# Schematic Phase 0 Findings v0.1 (Stage 0)

Date: 2026-09-27 · KiCad CLI: `kicad-cli.exe` 10.0.4 (Windows) · Fixtures:
`fixtures/sch/` (§42 Cases A–E, derived from the `complex_hierarchy` demo;
syntax anchored to the demo files, see `fixtures/sch/README.md`) · Raw
harness output: `fixtures/sch/matrix-results.json` · Harness:
`fixtures/sch/run-matrix.ps1` (Windows PowerShell 5.1 and pwsh).

## Baseline decision: whole-design export (one `kicad-cli sch export svg` call per capture)

Rationale (all measured, details below):

1. **Selective export is unavailable**: `--pages` cannot select any child page
   in kicad-cli 10.0.4 — every selection plots exactly the root page (§49-3).
   Per-page or per-instance rendering through the CLI is impossible today.
2. **Fixed cost dominates**: one export call costs ≈ 1.01 s wall time whether
   the design has 1 or 20 logical pages (marginal per-page cost ≈ 0 at this
   sheet size; 20 pages = 28 MB of SVG in the same 1.01 s, §49-6). Whole-design
   export is the cheapest strategy; N selective calls would cost N × 1.01 s.
3. **Consistency for free**: the emitted file set always reflects the current
   root + children state (child-only edits propagate, §49-5; add/remove/
   retarget reflected immediately, §49-4). The observer re-exports the whole
   design on save and derives page→sheet structure from file naming + instance
   data instead of maintaining a selective render graph.
4. **Whole-design output still gives per-page artifacts**: each page lands in
   its own SVG (naming rules, §49-1), so the viewer can serve pages
   individually from one capture.

## §49-1 Per-case export: SVG count, naming, page order, sheet→file mapping

**Answer**: One CLI call exports all pages. Root page → `<rootfile>.svg`; a
child page → `<rootfile>-<Sheetname>.svg`; a depth-n chain →
`<rootfile>-<Name1>-<Name2>-...-.svg` (sheet-name chain joined with `-`;
`case_c` produced `root-Drive-Phase-Gate.svg`). Spaces in `Sheetname` are kept
verbatim (`root-Channel A.svg`). Page order follows the `(page "N")` numbers in
each sheet symbol's `(instances (project ... (path ...) (page ...)))` block,
root first; the plotted order (CLI stdout sequence) flips when page numbers are
swapped. Export counts matched expectations for all five cases: A=1, B=3, C=4,
D=4, E=3 (exit 0 each).

Mapping caveats (measured): file names carry **no page numbers**, so they do
not change on reorder (§49-4 M2); and **duplicate `Sheetname`s collide** — two
sheets named "Same" both plotted to the same `root-Same.svg` (second overwrites
the first; 3-page design → 2 files; exit 0, silent). The observer must key
pages by the sheet-name chain + sheet-instance UUID path, never by file name
alone, and should treat duplicate output names as an anomaly signal.

Recorded: `matrix-results.json` `item1_perCaseExport` (counts, names, sizes,
plotted order, pass flags); `item3_caseD_pages`; duplicate-name probe below in
"Commands".

## §49-2 Case D: shared instances as separate files; refdes/instance data

**Answer**: Yes — all 3 instances of the single `channel.kicad_sch` export as
3 separate SVGs (`root-Channel A/B/C.svg`), because file naming uses the
`Sheetname`, not the `Sheetfile`. Refdes are resolved **per sheet-instance UUID
path** from the child's symbol instance blocks: Channel A shows R2xx (29
comps), Channel B shows R3xx (29), Channel C shows R4xx for the 18 R-symbols
that carry a third instance block. Symbols **without** an instance entry for a
path fall back to the stored `property "Reference"` value — Channel C's other
11 symbols therefore duplicate Channel A's refs; `sch export netlist` emits a
KiCad annotation warning ("주석 오류… / annotation errors, fix in the
schematic editor") yet still exits 0 with Channel A=29, B=29, C=28 netlist
entries (one duplicate collapses). Also verified: no `.kicad_pro` is needed and
the `(project "...")` string does not need to match the root file name for
per-instance resolution.

Observer implication: an instance's identity = root uuid + sheet uuid chain;
the child file's `(instances)` table is the authoritative refdes source; a
missing entry = fallback duplicate (detectable via netlist duplicate refs).

Recorded: `matrix-results.json` `item2_caseD_sharedInstances` (netlist warning
text, compsPerSheet with sample refs, Channel-C ref-reuse examples).

## §49-3 Case D: addressing the two shared instances individually with `--pages`

**Answer**: Impossible in kicad-cli 10.0.4. Tested `--pages 3,4`, `3`, `1`,
`1,2,3,4`, `9` on case D: each produced exactly one file, `root.svg` (the root
page), never a child page; `--pages "Channel B"` (name form) exits 1; blank or
omitted `--pages` = all pages. Reproduced identically on the **pristine demo
project** (with its `.kicad_pro`) and on a case-D copy with legacy
`(sheet_instances)` entries added for the child paths — so it is CLI behavior,
not a fixture artifact. Shared (and any child) instances cannot be individually
addressed via `--pages` in this version. This is the primary driver of the
whole-design baseline decision.

Recorded: `matrix-results.json` `item3_caseD_pages` (all probes with exit
codes + produced files); findings "Commands" section for the demo/legacy
probes.

## §49-4 Case E: export behavior after page reorder / sheet rename (+ add/remove/retarget)

**Answer** (sequential mutations on a temp copy of case E; each step
re-exported):

| Mutation | Layout after | Export result |
|---|---|---|
| M0 baseline | Power(2), MCU(3) | 3 files: root, root-Power, root-MCU; order root→Power→MCU |
| M1 rename `Power`→`Power Supply` | same pages | output renames to `root-Power Supply.svg` (spaces kept); order unchanged |
| M2 reorder: swap page 2↔3 | Power Supply(3), MCU(2) | **same file names**, plot order flips to root→MCU→Power Supply |
| M3 retarget MCU→`power.kicad_sch` | both sheets → power.kicad_sch | still 3 pages; `root-MCU.svg` now renders the power child; refs fall back to stored `Reference` properties (child has no instance entry for that second path) |
| M4 add sheet `Extra`→`extra.kicad_sch` page 4 | +Extra(4) | 4 files; Extra last in plot order; page numbers 1,2,3,4 |
| M5 remove `Power Supply` (page 3 after M2 swap) | MCU(2), Extra(4) → pages 1,2,4 | 3 files exported; page gap preserved (no renumbering); order root→MCU→Extra |

Overall: every export reflects the current file state immediately (no
caching/staleness); naming follows `Sheetname` only (reorder renames nothing);
page assignment data, not file content, drives order. Observer takeaway:
detect structural change by diffing the sheet tree (Sheetname/Sheetfile/uuid/
page), not output names.

Recorded: `matrix-results.json` `item4_caseE_mutations` (per-mutation layout,
plotted order, files, ms) incl. the M5b `--pages` probes on the gapped design.

## §49-5 Child-file-only edit reflected when exporting from root?

**Answer**: Yes. Edited only `mcu.kicad_sch` (title block text) in a case-B
work copy — root file SHA256 verified unchanged before/after — then exported
from root: `root-MCU.svg` content changed (full dir hash differs; size
1,464,336 → 1,461,535 bytes) while `root.svg`/`root-Power.svg` pages were
re-emitted identically. The CLI resolves the full hierarchy from the root at
every invocation; there is no per-file staleness to manage.

Recorded: `matrix-results.json` `item5_childOnlyEdit` (rootFileUntouched=true,
mcuSvgChanged=true, hashes, export result).

## §49-6 Latency 1/5/10/20 logical pages (3 runs each, mean/spread)

**Answer**: flat ≈ 1.01 s for 1→20 pages; the kicad-cli process startup is the
entire cost (P=20 wrote 20 SVGs / 28 MB in the same wall time as 1 page /
448 KB). Method: harness generates a temp root referencing the case-D child
N = P−1 times (P=1 is the flat root), then times `kicad-cli sch export svg`
wall time, fresh output dir per run, 3 runs per size.

| Logical pages (root + N child instances) | Runs (ms) | Mean (ms) | Spread max−min (ms) |
|---|---|---|---|
| 1 (root only) | 1008.8 / 1010.4 / 1012.0 | 1010.4 | 3.2 |
| 5 (root + 4) | 1010.0 / 1010.8 / 1009.2 | 1010.0 | 1.6 |
| 10 (root + 9) | 1010.0 / 1010.4 / 1013.0 | 1011.1 | 3.0 |
| 20 (root + 19) | 1011.3 / 1015.4 / 1009.5 | 1012.1 | 5.9 |

Observer implication: per-page CLI renders would multiply the ~1.0 s fixed
cost by page count; whole-design export amortizes it to one call per capture.
Debounce/queue budgets (§7.3) should assume ≈ 1.0–1.5 s per capture on this
machine regardless of design size at schematic-demo scale.

Recorded: `matrix-results.json` `item6_latency` (all raw runs, min/max/spread,
generated root layouts).

## §49-7 Missing-child failure behavior

**Answer**: `kicad-cli sch export svg` **fails silently**: with the referenced
child deleted, it still exits 0, prints nothing but the normal "plotted"
lines (stderr empty), and emits the missing sheet's page as a frame-only empty
SVG — 56,797 bytes vs 1,461,535 bytes with the child present (same page,
`--pages 4` probe identical). Verified twice: harness case-E step (deleted
`extra.kicad_sch`) and a manual case-B probe (deleted `mcu.kicad_sch` →
`root-MCU.svg` 56,842 bytes vs 1,464,336).

Observer implication: **exit codes cannot detect a broken hierarchy.** The
observer must (a) parse the root's `Sheetfile` references and check existence
before/after capture, and/or (b) flag emitted pages whose size is near the
empty-frame floor (~57 KB here) as "missing source" states (aligns with the
plan's "Waiting for source" rule A8).

Recorded: `matrix-results.json` `item7_missingChild` (exit codes, stderr, file
sizes) + manual probe below.

## SVG sizes & render times per case (item 1, single full export each)

| Case | Pages | Wall (ms) | SVG files (bytes) |
|---|---|---|---|
| A flat | 1 | 1009.9 | root.svg 457,414 |
| B basic | 3 | 1011.6 | root 478,549 · root-MCU 1,464,336 · root-Power 1,459,288 |
| C nested | 4 | 1013.7 | root 467,314 · root-Drive 1,469,870 · root-Drive-Phase 1,473,340 · root-Drive-Phase-Gate 1,463,664 |
| D shared | 4 | 1009.8 | root 498,507 · root-Channel A 1,460,703 · B 1,468,621 · C 1,453,304 |
| E mutation | 3 | 1008.7 | identical to B (byte-identical fixture) |

Child pages ≈ 1.45 MB each (dense resistor networks rendered as vector paths);
root pages with only sheet symbols ≈ 0.46 MB. Empty-frame page ≈ 57 KB.

## Observer mapping rules (design input for the schematic MVP)

1. Page → file name: `<rootBase>.svg` (root) / `<rootBase>-<Sheetname
   chain>.svg` (children); `-` separator; spaces kept; **not page-numbered**;
   **collides on duplicate Sheetname**.
2. Page order & numbering: per sheet instance from
   `(instances (project ... (path ...) (page "N")))` in the parent file; root
   page from its own `(sheet_instances)`. Identity of an instance = root uuid +
   sheet uuid chain.
3. Refdes: child symbol `(instances ... (path ... (reference ...)))` keyed by
   that chain; fallback to stored `property "Reference"` when absent
   (duplicate refs possible; netlist warns but exits 0).
4. Integrity: verify `Sheetfile` existence (and watch for ~57 KB empty-frame
   pages); never trust exit codes or stderr for hierarchy health.
5. No `.kicad_pro` required for CLI export; project-name string matching is not
   needed for instance resolution (v9-format files, generator_version "9.0",
   read fine by 10.0.4).

## Open questions (→ ROADMAP (d))

1. `--pages` child-page selection broken in kicad-cli 10.0.4 — re-test on newer
   KiCad releases; a fix would reopen selective re-render (deferred per plan
   §7 item 3).
2. Channel C netlist comp count 28 vs A/B's 29 under duplicate-ref annotation
   conflict — exact KiCad dedup rule not investigated (affects only duplicate-
   ref diagnostics, not export).
3. KiCad 10-native file format (our fixtures are demo `version 20250114`,
   `generator_version "9.0"`) — behavior of 10-native files with
   `(sheet_instances)` per child and any new instance keys not verified.
4. `--drawing-sheet` / theme / variant flags' effect on output naming and size
   (untouched; not needed for the whole-design baseline).
5. Latency measured on one machine and demo-scale sheets only; large-production
   schematics need a re-measure before queue budgets are finalized (§36).

## Evidence: exact commands & observable outcomes

Self-validation (all: exit 0, expected page count; Korean-locale stdout
"'...svg'에 플롯됨. / 완료."):

```
"C:/Program Files/KiCad/10.0/bin/kicad-cli.exe" version                       # 10.0.4
kicad-cli sch export svg --output C:/tmp/pcbo-stage0/demo_baseline \
  "C:/Program Files/KiCad/10.0/share/kicad/demos/complex_hierarchy/complex_hierarchy.kicad_sch"
# -> 3 files: complex_hierarchy.svg, -ampli_ht_vertical.svg (page 2), -ampli_ht_horizontal.svg (page 3)
# per fixture (case_a..case_e): 1/3/4/4/3 files respectively (validate/ dirs)
```

Harness (plan §"회로도 행렬" invocation):

```
powershell -ExecutionPolicy Bypass -File fixtures/sch/run-matrix.ps1 \
  -KiCadCli "C:/Program Files/KiCad/10.0/bin/kicad-cli.exe"
# -> items 1-7 executed; fixtures/sch/matrix-results.json written
```

Targeted probes (all under temp work dirs; outcomes quoted above):

```
kicad-cli sch export netlist --output case_d.net fixtures/sch/case_d_shared/root.kicad_sch
#   exit 0 + annotation warning on stderr; A=29/B=29/C=28 comps
kicad-cli sch export svg --pages 3,4 --output <dir> fixtures/sch/case_d_shared/root.kicad_sch
#   exit 0, only root.svg (same for 1|2|3|4|1,2|1,2,3,4|2-4|9; blank = all pages;
#   "Channel B" -> exit 1; also reproduced on the pristine demo project and with
#   legacy sheet_instances entries added)
kicad-cli sch export svg --output <dir> <dup-Sheetname root.kicad_sch>
#   exit 0; both same-named pages written to one root-Same.svg (overwrite)
kicad-cli sch export svg --output <dir> <case_b copy with mcu.kicad_sch deleted>
#   exit 0; root-MCU.svg = 56,842 B empty frame (vs 1,464,336 B with child present)
```

Timebox: all 7 items answered within budget; no partial/C5 fallback used.

Files created by Stage 0: `fixtures/sch/README.md`,
`fixtures/sch/case_a_flat/root.kicad_sch`,
`fixtures/sch/case_b_basic/{root,power,mcu}.kicad_sch`,
`fixtures/sch/case_c_nested/{root,drive,phase,gate}.kicad_sch`,
`fixtures/sch/case_d_shared/{root,channel}.kicad_sch`,
`fixtures/sch/case_e_mutation/{root,power,mcu}.kicad_sch`,
`fixtures/sch/run-matrix.ps1`, `fixtures/sch/matrix-results.json` (harness
output), this document. No changes under `src/` or `tests/`.
