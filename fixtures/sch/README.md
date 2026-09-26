# Schematic hierarchy fixtures (Stage 0)

Five `.kicad_sch` fixtures covering addendum §42 Cases A–E, used by
`run-matrix.ps1` to answer the §49 1–7 experiment matrix. Results:
`docs/Sch_Phase0_Findings_v0.1.md` and `matrix-results.json` (harness output).

- Derived from: KiCad 10.0.4 shipped demo
  `C:/Program Files/KiCad/10.0/share/kicad/demos/complex_hierarchy/`
  (`complex_hierarchy.kicad_sch`, `ampli_ht.kicad_sch`).
- Method: demo file copies + anchored text edits only. Every inserted/edited
  s-expression is copied verbatim from the demo files (see "Observed syntax").
  No key name was written from assumption.
- Self-validation (all pass, kicad-cli 10.0.4):

```
kicad-cli sch export svg --output <out> fixtures/sch/<case>/root.kicad_sch
# case_a_flat=1 page, case_b_basic=3, case_c_nested=4, case_d_shared=4, case_e_mutation=3
```

## Observed syntax (extracted from the demo files, verbatim)

Root header — `complex_hierarchy.kicad_sch` (file `version 20250114`,
`generator_version "9.0"`; read/written fine by kicad-cli 10.0.4):

```
(kicad_sch
	(version 20250114)
	(generator "eeschema")
	(generator_version "9.0")
	(uuid "5b9623a5-6d01-41fc-9865-e1bc779418c8")
	(paper "A4")
	(title_block
		(title "Complex hierarchy: demo")
```

Hierarchical sheet symbol (root, one of two in the demo; indented with TABs):

```
	(sheet
		(at 71.12 111.76)
		(size 50.8 36.83)
		(exclude_from_sim no)
		(in_bom yes)
		(on_board yes)
		(dnp no)
		(stroke
			(width 0)
			(type solid)
		)
		(fill
			(color 0 0 0 0.0000)
		)
		(uuid "00000000-0000-0000-0000-00004b3a1333")
		(property "Sheetname" "ampli_ht_vertical"
			(at 71.12 110.9975 0)
			(effects
				(font
					(size 1.524 1.524)
				)
				(justify left bottom)
			)
		)
		(property "Sheetfile" "ampli_ht.kicad_sch"
			(at 71.12 149.2001 0)
			(effects
				(font
					(size 1.524 1.524)
				)
				(justify left top)
			)
		)
		(instances
			(project "complex_hierarchy"
				(path "/5b9623a5-6d01-41fc-9865-e1bc779418c8"
					(page "2")
				)
			)
		)
	)
```

Key facts anchored by the above:

- Sheet identity/pointers are **properties**: `"Sheetname"` (display name, used
  in export file names) and `"Sheetfile"` (child file, relative path).
- The **page number lives per sheet instance** inside the sheet symbol's
  `(instances (project "<name>" (path "/<root-uuid>[/<parent-sheet-uuid>...] (page "N"))))`
  block. The path is the root file uuid plus the sheet uuids of the ancestor
  chain (empty for direct children of the root).
- The root file closes with its own page assignment:

```
	(sheet_instances
		(path "/"
			(page "1")
		)
	)
	(embedded_fonts no)
)
```

- A child file shared by multiple sheet symbols stores **one instance block per
  sheet path per symbol**, each with its own reference designator — this is how
  the demo (`ampli_ht.kicad_sch`, referenced twice) keeps R2xx / R3xx apart:

```
		(instances
			(project "complex_hierarchy"
				(path "/5b9623a5-6d01-41fc-9865-e1bc779418c8/00000000-0000-0000-0000-00004b3a1333"
					(reference "R210")
					(unit 1)
				)
				(path "/5b9623a5-6d01-41fc-9865-e1bc779418c8/00000000-0000-0000-0000-00004b3a13a4"
					(reference "R310")
					(unit 1)
				)
			)
		)
```

- Symbols without an instance entry for a given sheet path fall back to the
  symbol's stored `property "Reference"` value (verified in case D, see
  findings §49-2).
- No `.kicad_pro` is needed for CLI export; the demo's
  `(project "complex_hierarchy")` string does not need to match the root file
  name for per-instance refdes resolution (verified in case B/D).

## Cases

| Case | Directory | Tree | Pages | Derivation (all: demo copy + anchored edits) |
|---|---|---|---|---|
| A flat | `case_a_flat/` | root only | 1 | demo root, both `(sheet ...)` blocks deleted (region between first `\t(sheet` and `\t(sheet_instances`) |
| B basic | `case_b_basic/` | Root → Power, MCU | 3 | demo root; sheet 1: `Sheetname` "Power", `Sheetfile` "power.kicad_sch" (uuid kept); sheet 2: "MCU" → "mcu.kicad_sch"; `ampli_ht.kicad_sch` copied as `power.kicad_sch` and `mcu.kicad_sch` (kept sheet uuids 4b3a1333/4b3a13a4 so the children's instance paths still match → per-instance refs resolve) |
| C nested | `case_c_nested/` | Root → Drive → Phase → Gate (depth 3) | 4 | demo root: single sheet "Drive" → "drive.kicad_sch"; `drive.kicad_sch` = child copy + inserted sheet "Phase" → "phase.kicad_sch" (path `/root/4b3a1333`, page 3); `phase.kicad_sch` = child copy + inserted sheet "Gate" → "gate.kicad_sch" (path `/root/4b3a1333/4b3a13a4`, page 4); `gate.kicad_sch` = child copy. Inserted blocks are verbatim demo sheet blocks with substituted name/file/uuid/path/page |
| D shared | `case_d_shared/` | Root → Channel A/B/C, all → `channel.kicad_sch` | 4 | demo root; three sheet symbols "Channel A/B/C" all `Sheetfile "channel.kicad_sch"`, uuids 4b3a1333 / 4b3a13a4 / 4b3a13b0, pages 2/3/4; `channel.kicad_sch` = child copy + a third instance block (path .../4b3a13b0, refs R3xx→R4xx) duplicated from each R3xx instance block, so only R-symbols have full 3-instance data (the rest demonstrate fallback, see findings) |
| E mutation | `case_e_mutation/` | = case B copy | 3 | byte-identical copy of `case_b_basic/`; the harness mutates a temp copy of it (rename / reorder / retarget / add / remove / missing-child), never the fixture itself |

## Harness

```
powershell -ExecutionPolicy Bypass -File fixtures/sch/run-matrix.ps1 `
  -KiCadCli "C:/Program Files/KiCad/10.0/bin/kicad-cli.exe"
```

Executes the §49 1–7 matrix against these fixtures in a temp work dir and
writes `fixtures/sch/matrix-results.json` (per-export exit code, wall ms,
plotted order, file names/sizes, netlist refdes map, latency runs, failure
mode). Works under Windows PowerShell 5.1 and pwsh.
