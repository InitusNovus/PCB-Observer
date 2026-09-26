# KiCad Observer

Read-only spectator for agent-driven KiCad work: the agent edits and saves normally; the observer watches saved states and renders them for a human, independently. **Currently Phase 0 only**: a one-shot snapshot + SVG rendering feasibility probe for PCBs. There is no watcher, live status, history, semantic analysis, DRC, or schematic hierarchy support yet — see `docs/PCB_Observer_Spec_Draft_v0.1.md` and its companion `docs/Schematic_Observer_MultiSheet_Addendum_v0.1.md` for the intended product.

## Windows development

Requires Windows, the .NET 10 SDK (`dotnet`; `global.json` pins 10.0.401), and KiCad 10 (`kicad-cli.exe`). The repository is `C:\Dev\PCB-Observer`. Use **Windows Git** for this worktree.

```powershell
Set-Location C:\Dev\PCB-Observer
dotnet test PCB-Observer.slnx
dotnet run --project src/PcbObserver -- 'C:\Program Files\KiCad\10.0\share\kicad\demos\complex_hierarchy\complex_hierarchy.kicad_pcb'
```

The last command prints a `file:///` URL — open it in a browser (no server needed for Phase 0). Replace the demo path with another `.kicad_pcb` to test a real board. Output defaults to `%LOCALAPPDATA%\PCBObserver\phase0`, outside the source project. The source PCB is copied before rendering; KiCad CLI operates only on that copy. No `--check-zones` or zone refill is requested. The viewer shows **one layer at a time** until multilayer composition/coordinate alignment is proven. Back view is a horizontal mirror for visual inspection, not validated overlay geometry.

## State

The tool is F# on `net10.0` (ported from the original Python probe; Python is gone). Unit tests (xUnit) cover source copy and CLI argument construction; the demo render above was verified end to end with KiCad 10 on this machine. Browser interaction, layer alignment, drill and zone behavior, and production-board performance remain to be checked manually; see the Phase 0 acceptance scope in the PCB spec and the hierarchy test cases (§42) in the schematic addendum for what comes next.

No Git remote, commit, or GitHub repository has been created.
