# PCB Observer

Read-only spectator for agent-driven KiCad PCB work. **Currently Phase 0 only**: a one-shot snapshot + SVG rendering feasibility probe. There is no watcher, live status, history, semantic analysis, or DRC yet.

## Windows development

Requires Windows PowerShell 7, `uv`, and KiCad 10 (`kicad-cli.exe`). The repository is `C:\Dev\PCB-Observer` (`/mnt/c/Dev/PCB-Observer` in WSL). Use **Windows Git** for this worktree.

```powershell
Set-Location C:\Dev\PCB-Observer
uv run --python 3.11 python -m unittest discover -s tests -v
uv run --python 3.11 python -m pcb_observer.phase0 'C:\Program Files\KiCad\10.0\share\kicad\demos\complex_hierarchy\complex_hierarchy.kicad_pcb'
```

The last command prints a `Serve render directory:` command. Run it in another PowerShell terminal and open `http://127.0.0.1:8765/`. Replace the demo path with another `.kicad_pcb` to test a real board. Output defaults to `%LOCALAPPDATA%\PCBObserver\phase0`, outside the source project. The source PCB is copied before rendering; KiCad CLI operates only on that copy. No `--check-zones` or zone refill is requested. The viewer shows **one layer at a time** until multilayer composition/coordinate alignment is proven. Back view is a horizontal mirror for visual inspection, not validated overlay geometry.

## State

KiCad 10's `pcb export svg --mode-single --layers` generated F.Cu, B.Cu, Edge.Cuts, F.Silkscreen and B.Silkscreen SVGs for the bundled `complex_hierarchy` demo on this Windows machine. Automated unit tests cover source copy and CLI argument construction. Browser interaction, layer alignment, drill and zone behavior, and production-board performance remain to be checked manually; see `docs/PCB_Observer_Spec_Draft_v0.1.md` for the intended product and Phase 0 acceptance scope.

No Git remote, commit, or GitHub repository has been created.
