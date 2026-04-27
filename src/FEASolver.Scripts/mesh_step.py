"""
mesh_step.py
------------
Import a STEP file, mesh with Gmsh, output mesh_data.json.

Output files (in --output-dir):
  mesh.msh       — Gmsh v2 ASCII (for debugging / re-import)
  mesh_data.json — nodes, elements, face groups (consumed by C# app)

Usage:
  python mesh_step.py --step model.step --output-dir ./work
                      [--element-size 0.01] [--order 2] [--algorithm 4]
                      [--no-optimize]
"""

from __future__ import annotations
import argparse
import math
import sys
import logging
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

import gmsh
from utils.mesh_io import write_mesh_data
from utils.mesh_cache import MeshCache
from utils import gmsh_utils

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s",
                    stream=sys.stderr)
log = logging.getLogger(__name__)


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser()
    p.add_argument("--step",         required=True)
    p.add_argument("--output-dir",   required=True)
    p.add_argument("--element-size", type=float, default=5.0,
                   help="Element size in --input-units (default: 5).")
    p.add_argument("--order",        type=int,   default=2, choices=[1, 2])
    p.add_argument("--algorithm",    type=int,   default=4)
    p.add_argument("--no-optimize",  action="store_true")
    p.add_argument("--no-cache",     action="store_true",
                   help="Force re-mesh even if cached result exists")
    p.add_argument("--input-units",  choices=["mm", "m"], default="mm",
                   help="Length unit of the STEP file. Coordinates are converted "
                        "to SI metres before export. Default: mm.")
    return p.parse_args()


def mesh_step(
    step_path: str,
    output_dir: str,
    element_size: float,
    order: int,
    algorithm: int,
    optimize: bool = True,
    use_cache: bool = True,
    input_units: str = "mm",
) -> None:
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)
    mesh_json = out / "mesh_data.json"

    # ── Cache check ───────────────────────────────────────────────────────────
    # Include input_units in the cache key so a unit change forces a rebuild.
    cache = MeshCache(out)
    cache_key = cache.make_key(step_path, element_size, order, algorithm,
                                extra=f"u={input_units}")
    if use_cache and cache.is_valid(cache_key) and mesh_json.exists():
        log.info("Cache hit — skipping re-mesh")
        return

    # ── Gmsh meshing ──────────────────────────────────────────────────────────
    gmsh.initialize()
    gmsh.option.setNumber("General.Verbosity", 2)

    try:
        gmsh.model.add("model")

        # Import STEP
        log.info(f"Importing STEP: {step_path}")
        shapes = gmsh.model.occ.importShapes(step_path)
        gmsh.model.occ.synchronize()

        n_surf = len(gmsh_utils.get_surface_tags())
        n_vol  = len(gmsh_utils.get_volume_tags())
        log.info(f"Geometry: {n_surf} surfaces, {n_vol} volumes")
        if n_vol == 0:
            raise RuntimeError(
                "STEP file has no solid volumes. "
                "Ensure the geometry is a closed solid, not a surface model.")

        # ── Unit handling — EXPLICIT, NOT HEURISTIC ───────────────────────────
        # Gmsh delivers coordinates in the unit of the source STEP file.
        # The CAD operator declares this unit via --input-units; we then
        # convert to SI metres so the downstream solver pipeline is always
        # in (m, Pa, N).
        #
        # Why explicit and not auto-detected: a heuristic on bounding-box
        # diagonal silently produces ~10^6 stress errors when it guesses wrong
        # (e.g. a metre-unit STEP whose bbox happens to exceed 1, or a tiny
        # mm-unit STEP whose bbox is below 1). We force the caller to declare.
        bb = gmsh.model.getBoundingBox(-1, -1)
        dims = [bb[3] - bb[0], bb[4] - bb[1], bb[5] - bb[2]]
        bb_diag = math.sqrt(sum(d * d for d in dims))

        if input_units == "mm":
            coord_scale = 0.001
        elif input_units == "m":
            coord_scale = 1.0
        else:
            raise ValueError(f"Unknown --input-units: {input_units!r} "
                             f"(expected 'mm' or 'm').")
        log.info(f"Model bbox diagonal = {bb_diag:.4f} {input_units}. "
                 f"coord_scale = {coord_scale} (→ metres).")

        # ── Auto-size guard ───────────────────────────────────────────────────
        # Detect unit mismatch: if element_size < 1/500 of the smallest model
        # dimension the mesh would never finish (e.g. 0.01 mm on a 127 mm beam).
        # element_size is always in Gmsh's native units (same as the STEP file).
        min_dim = min(d for d in dims if d > 0)
        if element_size < min_dim / 500:
            auto_size = min_dim / 10.0
            log.warning(
                f"Element size {element_size} is far too small for model extent "
                f"(smallest dim = {min_dim:.4f}). Auto-adjusted to {auto_size:.4f}."
            )
            element_size = auto_size
        log.info(f"Model extents: {dims[0]:.4f} x {dims[1]:.4f} x {dims[2]:.4f}, "
                 f"using element_size={element_size:.4f}")

        # Physical groups
        surf_tags = gmsh_utils.add_physical_groups_for_all_surfaces()
        gmsh_utils.add_physical_group_for_all_volumes()

        # Mesh settings
        gmsh_utils.apply_mesh_settings(element_size, order, algorithm)

        # Generate
        log.info(f"Meshing (order={order}, size={element_size}, algo={algorithm})...")
        gmsh.model.mesh.generate(3)
        if optimize:
            try:
                gmsh.model.mesh.optimize("Netgen")
            except Exception as exc:
                log.warning(f"Netgen optimizer unavailable ({exc}); skipping.")
        # Netgen optimiser can drop a quadratic mesh back to linear, so run
        # the promotion after optimisation.
        if order >= 2:
            gmsh_utils.promote_to_quadratic()

        # Export .msh
        msh_path = out / "mesh.msh"
        gmsh.option.setNumber("Mesh.MshFileVersion", 2.2)
        gmsh.write(str(msh_path))
        log.info(f"Wrote: {msh_path}")

        # Extract nodes + elements
        elements = gmsh_utils.extract_volume_elements()
        if not elements:
            raise RuntimeError("Meshing produced no tetrahedral elements.")
        log.info(f"Volume elements: {len(elements)}")

        # Build O(1) face lookup then map surface faces
        face_map = gmsh_utils.build_face_element_map(elements)
        mesh_data = gmsh_utils.build_mesh_data(surf_tags, face_map, coord_scale=coord_scale)

        # Validate
        warnings = gmsh_utils.validate_face_groups(mesh_data)
        for w in warnings:
            log.warning(f"Face mapping: {w}")

        stats = mesh_data.stats()
        log.info(str(stats))

        mapped = sum(1 for f in mesh_data.faces if f.element_faces)
        log.info(f"Face groups: {len(mesh_data.faces)} total, "
                 f"{mapped} with element face mappings")

    finally:
        gmsh.finalize()

    # ── Write output ──────────────────────────────────────────────────────────
    write_mesh_data(mesh_data, str(mesh_json))
    log.info(f"Wrote: {mesh_json}")

    cache.save(cache_key)


if __name__ == "__main__":
    args = parse_args()
    mesh_step(
        step_path=args.step,
        output_dir=args.output_dir,
        element_size=args.element_size,
        order=args.order,
        algorithm=args.algorithm,
        optimize=not args.no_optimize,
        use_cache=not args.no_cache,
        input_units=args.input_units,
    )
