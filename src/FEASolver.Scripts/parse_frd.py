"""
parse_frd.py
------------
Parse CalculiX .frd results file → results.json

Usage:
  python parse_frd.py --frd model.frd --output results.json
"""

import argparse
import json
import sys
import logging
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from utils.frd_reader import FrdReader

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s", stream=sys.stderr)
log = logging.getLogger(__name__)


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--frd", required=True)
    p.add_argument("--output", required=True)
    return p.parse_args()


def _frd_completeness(results: dict, output_path: Path) -> None:
    """
    Log per-block node counts and compare against the total mesh node count.
    Reads mesh_data.json from the same directory as --output if available.
    Runs automatically after every solve; output goes to the solver log (stderr).
    """
    # Try to get total mesh node count from mesh_data.json in the work directory.
    mesh_n: int | None = None
    mesh_json = output_path.parent / "mesh_data.json"
    if mesh_json.exists():
        try:
            data = json.loads(mesh_json.read_text(encoding="utf-8"))
            mesh_n = len(data.get("nodes", []))
        except Exception as exc:
            log.debug("Could not read mesh_data.json for completeness check: %s", exc)

    def _count(key: str) -> int:
        return len(results.get(key, {}).get("node_ids", []))

    n_disp   = _count("displacement_x")
    n_stress = _count("vonmises")
    n_strain = _count("strain_vonmises")
    n_forc   = _count("reaction_x")

    mesh_str = str(mesh_n) if mesh_n is not None else "?"
    log.info("FRD completeness (total mesh nodes = %s):", mesh_str)
    log.info("  displacement : %d nodes", n_disp)
    log.info("  stress (σ_vm): %d nodes", n_stress)
    log.info("  strain (ε_vm): %d nodes", n_strain)
    log.info("  reaction (RF): %d nodes", n_forc)

    if mesh_n and mesh_n > 0:
        for name, count in (
            ("displacement", n_disp),
            ("stress",       n_stress),
            ("strain",       n_strain),
            ("reaction",     n_forc),
        ):
            missing = mesh_n - count
            if missing > 0:
                pct = 100.0 * missing / mesh_n
                log.warning(
                    "  INCOMPLETE: %-14s %d / %d nodes missing (%.1f%%).",
                    name, missing, mesh_n, pct)
            else:
                log.info("  OK: %-14s all %d nodes present.", name, mesh_n)


if __name__ == "__main__":
    args = parse_args()

    frd_path = Path(args.frd)
    if not frd_path.exists():
        log.error("FRD file not found: %s", frd_path)
        sys.exit(1)
    if frd_path.stat().st_size == 0:
        log.error("FRD file is empty (0 bytes): %s", frd_path)
        sys.exit(1)

    reader = FrdReader(args.frd)
    results = reader.parse()

    # Validate that we actually got results
    n_disp = len(results.get("displacement_x", {}).get("node_ids", []))
    n_stress = len(results.get("vonmises", {}).get("node_ids", []))

    if n_disp == 0 and n_stress == 0:
        log.error("FRD parser produced ZERO results (no displacement, no stress). "
                  "The .frd file may be binary, empty, or in an unexpected format. "
                  "File: %s (%d bytes)", frd_path, frd_path.stat().st_size)
        sys.exit(1)

    with open(args.output, "w") as f:
        json.dump(results, f, separators=(",", ":"))

    log.info("Parsed results: %d displacement nodes, %d stress nodes → %s",
             n_disp, n_stress, args.output)

    # FRD completeness report — runs after every solve, compares to mesh_data.json.
    _frd_completeness(results, Path(args.output))
