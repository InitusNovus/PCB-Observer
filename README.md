# KiCad Observer

Read-only spectator for agent-driven KiCad work: the agent edits and saves normally; the observer watches saved states and renders them for a human, independently — watcher → stable capture → immutable snapshots → latest-wins render queue → atomic bundles → local HTTP+SSE → LIVE/HOLD/HISTORY viewer. **Phase 1 MVP implemented for PCBs** (schematics: Stage 0 experiments complete, product implementation deferred — see the roadmap). The agent, KiCad, and the source project are never modified (FR-002/AT-010 proven).

## Windows development

Requires Windows, the .NET 10 SDK (`dotnet`; `global.json` pins 10.0.401), and KiCad 10 (`kicad-cli.exe`). The repository is `C:\Dev\PCB-Observer`. Use **Windows Git** for this worktree.

```powershell
Set-Location C:\Dev\PCB-Observer
dotnet test PCB-Observer.slnx

# Live observer (watch mode):
dotnet run --project src/PcbObserver -- watch 'C:\path\to\board.kicad_pcb' [--port 8765] [--cli PATH] [--debounce-ms 500] [--output DIR]
# → opens a loopback URL; every save of the board updates the viewer (LIVE).

# Legacy one-shot Phase 0 probe (kept):
dotnet run --project src/PcbObserver -- 'C:\Program Files\KiCad\10.0\share\kicad\demos\complex_hierarchy\complex_hierarchy.kicad_pcb'

# Schematic hierarchy experiments (addendum §42/§49):
powershell -ExecutionPolicy Bypass -File fixtures/sch/run-matrix.ps1 -KiCadCli 'C:/Program Files/KiCad/10.0/bin/kicad-cli.exe'

# Schematic hierarchy observer (watch-sch):
dotnet run --project src/PcbObserver -- watch-sch 'C:\path\to\root.kicad_sch' [--port 8765] [--debounce-ms 500] [--output DIR]
# whole-design export from the root on every stable save; logical sheets keyed
# by sheet-name chain (shared children render per instance); missing children
# flagged explicitly; sheet list + per-sheet viewport memory + HOLD/HISTORY.
```

Observer data lives under `%LOCALAPPDATA%\PCBObserver` (snapshots, atomic render bundles `renders/<seq>/`, metadata, size logs) — never inside the watched project. Render bundles publish by directory rename; history is capped (50, protecting the last-published + 10 most recent). The server binds 127.0.0.1 only. The viewer shows copper single-active (F.Cu/B.Cu radio) with non-copper overlays; multilayer composition/alignment (spec §11.2) stays a documented caveat. Back view is a horizontal mirror, not validated overlay geometry.

## State

- **Implemented (PCB Phase 1 MVP)**: FileSystemWatcher directory watch with debounce/activity-cap and filename-presence rule; stable capture (retry + s-expression structure check); sha256-deduped immutable snapshots with monotonic sequences surviving crashes; latest-wins render queue (no in-flight cancel, no auto-retry — next save re-renders); atomic self-describing bundles (§39 manifests; 12-layer default set, `--layers` override); loopback HTTP+SSE server (state-first SSE event, traversal-blocked asset serving, port fallback); live viewer (5-state freshness badge, HOLD/HISTORY/Latest with hash+time rows, one-tick bundle switch, centered viewport + localStorage persistence, layer toggles incl. Mask/Paste/Fab/Cmts). 28 unit tests + headless E2E verified on KiCad 10 demos (replace-save cycles, missing-CLI failure/recovery, port-conflict fallback, source-dir untouched, kill-then-edit, red-team matrix).
- **Schematic side**: Stage 0 validation complete (`docs/Sch_Phase0_Findings_v0.1.md` — whole-design export baseline proven; `--pages` cannot select child pages in kicad-cli 10.0.4; missing-child fails silently) **and the schematic observer MVP shipped**: `watch-sch` reuses the observer core (Watch/Store/Queue/Server) with a hierarchy resolver (`Sch.fs`), project-level snapshots, whole-design root-context export, page manifests keyed by sheet-name chain, and a page-based viewer (sheet list with missing flags, per-sheet viewport memory, HOLD/HISTORY, no auto-jump). E2E verified against the Stage 0 fixtures: nested chains, shared child ×3 instances, sheet rename, child-only edits, missing child → explicit `missing` page status + preserved last bundle.
- **Next work**: `docs/ROADMAP.md` (deferred scope, ProjectAdapter refactor trigger, open questions, sequenced next milestones). **Browser-only verification**: `docs/Manual_Checklist.md`.
- Specs: `docs/PCB_Observer_Spec_Draft_v0.1.md`, `docs/Schematic_Observer_MultiSheet_Addendum_v0.1.md`.

No Git remote, commit, or GitHub repository has been created.
