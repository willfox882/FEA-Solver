"""
make_cube_step.py
-----------------
Generate a simple 10mm × 10mm × 40mm rectangular bar STEP file using Gmsh OCC.
Used as a test geometry for integration tests.

Usage:
  python make_cube_step.py [--output cube.step]
"""
import sys
import argparse
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent.parent / "src" / "FEASolver.Scripts"))


def make_bar(output_path: str,
             lx: float = 0.01,
             ly: float = 0.01,
             lz: float = 0.04) -> None:
    import gmsh
    gmsh.initialize()
    gmsh.option.setNumber("General.Verbosity", 0)
    try:
        gmsh.model.add("bar")
        gmsh.model.occ.addBox(0, 0, 0, lx, ly, lz)
        gmsh.model.occ.synchronize()
        gmsh.write(output_path)
        print(f"Wrote: {output_path}", file=sys.stderr)
    finally:
        gmsh.finalize()


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--output", default="bar.step")
    args = p.parse_args()
    make_bar(args.output)
