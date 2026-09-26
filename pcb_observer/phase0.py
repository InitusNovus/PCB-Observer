"""One-shot, read-only KiCad SVG feasibility probe (not a live watcher)."""

import argparse
import hashlib
import json
import shutil
import subprocess
from pathlib import Path

LAYERS = ("F.Cu", "B.Cu", "Edge.Cuts", "F.Silkscreen", "B.Silkscreen")


def capture_board(source: Path, cache: Path) -> Path:
    """Copy a saved PCB into observer-owned storage; never render the source directly."""
    cache.mkdir(parents=True, exist_ok=True)
    before = source.stat()
    payload = source.read_bytes()
    after = source.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise RuntimeError("PCB changed during capture; try again")
    digest = hashlib.sha256(payload).hexdigest()
    target = cache / f"{digest}.kicad_pcb"
    if not target.exists():
        target.write_bytes(payload)
    return target


def render_layers(snapshot: Path, output: Path, cli: Path, layers: tuple[str, ...]) -> None:
    output.mkdir(parents=True, exist_ok=True)
    for layer in layers:
        subprocess.run(
            [str(cli), "pcb", "export", "svg", "--mode-single", "--layers", layer,
             "--page-size-mode", "2", "--exclude-drawing-sheet", "--output",
             str(output / f"{layer}.svg"), str(snapshot)],
            check=True, timeout=120,
        )


def main() -> None:
    parser = argparse.ArgumentParser(description="Render an observer-owned KiCad snapshot to SVG")
    parser.add_argument("pcb", type=Path, help="Source .kicad_pcb (never modified)")
    parser.add_argument("--output", type=Path, default=Path.home() / "AppData/Local/PCBObserver/phase0")
    parser.add_argument("--cli", type=Path, default=Path(r"C:\Program Files\KiCad\10.0\bin\kicad-cli.exe"))
    args = parser.parse_args()
    source = args.pcb.resolve(strict=True)
    if source.suffix != ".kicad_pcb":
        parser.error("Expected a .kicad_pcb file")
    if not args.cli.is_file():
        parser.error(f"KiCad CLI not found: {args.cli}")
    output = args.output.resolve()
    if output == source.parent or source.parent in output.parents:
        parser.error("Output must be outside the source project")
    snapshot = capture_board(source, output / "snapshots")
    render_dir = output / "renders" / snapshot.stem
    render_layers(snapshot, render_dir, args.cli, LAYERS)
    shutil.copyfile(Path(__file__).with_name("viewer.html"), render_dir / "index.html")
    (render_dir / "manifest.json").write_text(json.dumps({"snapshot_sha256": snapshot.stem, "layers": LAYERS}, indent=2))
    print(f"Source unchanged: {source}")
    print(f"Snapshot: {snapshot}")
    print(f"Viewer: {render_dir / 'index.html'}")
    print("Serve render directory: uv run --python 3.11 python -m http.server 8765 --directory \"" + str(render_dir) + "\"")
    print("Open http://127.0.0.1:8765/")


if __name__ == "__main__":
    main()
