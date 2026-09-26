import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from pcb_observer.phase0 import capture_board, render_layers


class PhaseZeroTests(unittest.TestCase):
    def test_capture_copies_source_without_writing_to_it(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "board.kicad_pcb"
            source.write_bytes(b"(kicad_pcb test)")
            snapshot = capture_board(source, root / "cache")
            self.assertEqual(snapshot.read_bytes(), source.read_bytes())
            self.assertEqual(source.read_bytes(), b"(kicad_pcb test)")
            self.assertNotEqual(snapshot.parent, source.parent)

    def test_render_uses_snapshot_and_produces_named_layers(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "snapshot.kicad_pcb"
            snapshot.write_text("board")
            with patch("pcb_observer.phase0.subprocess.run") as run:
                render_layers(snapshot, root / "renders", Path("kicad-cli"), ("F.Cu", "Edge.Cuts"))
            self.assertEqual(run.call_count, 2)
            first = run.call_args_list[0].args[0]
            self.assertIn("--mode-single", first)
            self.assertEqual(first[first.index("--layers") + 1], "F.Cu")
            self.assertEqual(first[-1], str(snapshot))
            self.assertTrue(str(root / "renders" / "F.Cu.svg") in first)


if __name__ == "__main__":
    unittest.main()
