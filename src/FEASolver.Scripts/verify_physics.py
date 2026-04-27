"""
verify_physics.py
-----------------
Minimal ccx-based physics verifier for the canonical axial cantilever.

Beam:      L = 1.0 m, b = 2 in, h = 1 in
Material:  Steel, E = 200 GPa, ν = 0.3
Load:      F = 10 000 N axial (+x) on the x=L face
BC:        x=0 face fully fixed

Prints, alongside the analytical expected values:
  • Σ Rx on fixed face            vs  -F          =  -10 000 N
  • Avg σxx near the fixed end    vs   F/A        =   7.7498 MPa
  • Tip δx on loaded face         vs   F·L/(A·E)  =  38.7496 µm

"Near the fixed end" = nodes with x ≤ NEAR_FIXED_FRAC · L (default 10%),
which avoids the stress concentration that sits right at the encastre.
That gives a clean F/A reading for a sanity check without the usual
encastre-singularity noise.

Usage:
    python verify_physics.py [--ccx PATH] [--work-dir DIR]
                             [--element-size H] [--near-fixed-frac 0.1]
"""

from __future__ import annotations

import argparse
import logging
import math
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

import gmsh
from utils import gmsh_utils
from utils.inp_writer import InpWriterV2
from utils.frd_reader import FrdReader
from utils.models import (
    FEAModel, MaterialModel,
    BoundaryCondition, BcType,
    Load, LoadType,
)

log = logging.getLogger("verify_physics")

L_BEAM = 1.0
B_BEAM = 0.0508
H_BEAM = 0.0254
E_MOD  = 200.0e9
NU     = 0.3
F_AXIAL = 10_000.0


def build_mesh(element_size: float):
    gmsh.initialize()
    try:
        gmsh.option.setNumber("General.Verbosity", 2)
        gmsh.model.add("verify_physics")
        gmsh.model.occ.addBox(0, 0, 0, L_BEAM, B_BEAM, H_BEAM, tag=1)
        gmsh.model.occ.synchronize()

        surf_tags = gmsh_utils.get_surface_tags()
        fixed_tag = loaded_tag = None
        for tag in surf_tags:
            bb = gmsh.model.getBoundingBox(2, tag)
            cx = (bb[0] + bb[3]) / 2
            if cx < L_BEAM * 0.01:    fixed_tag = tag
            elif cx > L_BEAM * 0.99:  loaded_tag = tag
        if fixed_tag is None or loaded_tag is None:
            raise RuntimeError("Could not classify end faces")

        for tag in surf_tags:
            gmsh.model.addPhysicalGroup(2, [tag], tag=tag)
            gmsh.model.setPhysicalName(2, tag, f"Face_{tag}")
        gmsh_utils.add_physical_group_for_all_volumes()

        gmsh_utils.apply_mesh_settings(element_size, order=2, algorithm3d=4)
        gmsh.model.mesh.generate(3)
        try: gmsh.model.mesh.optimize("Netgen")
        except Exception: pass
        gmsh_utils.promote_to_quadratic()

        elements = gmsh_utils.extract_volume_elements()
        face_map = gmsh_utils.build_face_element_map(elements)
        mesh_data = gmsh_utils.build_mesh_data(surf_tags, face_map, coord_scale=1.0)
        return mesh_data, fixed_tag, loaded_tag
    finally:
        gmsh.finalize()


def run_ccx(ccx_exe: str, work_dir: Path) -> Path:
    inp = work_dir / "model.inp"
    proc = subprocess.run(
        [ccx_exe, "-i", "model"], cwd=str(work_dir),
        capture_output=True, text=True, check=False)
    (work_dir / "ccx.stdout.log").write_text(proc.stdout or "")
    (work_dir / "ccx.stderr.log").write_text(proc.stderr or "")
    if proc.returncode != 0:
        tail = "\n".join((proc.stdout or "").splitlines()[-40:])
        raise RuntimeError(f"CalculiX failed (exit {proc.returncode}):\n{tail}")
    frd = work_dir / "model.frd"
    if not frd.exists():
        raise FileNotFoundError(frd)
    return frd


def _pct(v, ref):
    return 0.0 if abs(ref) < 1e-30 else 100.0 * (v - ref) / ref


def main() -> int:
    logging.basicConfig(level=logging.INFO,
        format="%(levelname)s %(name)s: %(message)s", stream=sys.stderr)
    ap = argparse.ArgumentParser()
    ap.add_argument("--ccx", default=None)
    ap.add_argument("--work-dir", default=None)
    ap.add_argument("--element-size", type=float, default=0.010)
    ap.add_argument("--near-fixed-frac", type=float, default=0.10,
                    help="x/L cutoff defining 'near fixed end' (default 0.10)")
    args = ap.parse_args()

    work_dir = Path(args.work_dir or "verify_physics_work").resolve()
    if work_dir.exists(): shutil.rmtree(work_dir)
    work_dir.mkdir(parents=True)

    log.info("Building mesh (h=%.3f m)...", args.element_size)
    mesh_data, fixed_face, loaded_face = build_mesh(args.element_size)
    log.info("Mesh: %s", mesh_data.stats())

    area = B_BEAM * H_BEAM
    traction_pa = F_AXIAL / area
    model = FEAModel(
        mesh=mesh_data,
        material=MaterialModel("Steel", E_MOD, NU),
        boundary_conditions=[
            BoundaryCondition(type=BcType.FIXED, face_id=fixed_face, node_ids=[])],
        loads=[Load(type=LoadType.SURFACE_TRACTION, face_id=loaded_face,
                    magnitude=traction_pa, direction=[1.0, 0.0, 0.0])],
        work_dir=str(work_dir),
    )
    inp_path = work_dir / "model.inp"
    InpWriterV2(model).write(str(inp_path))

    ccx_exe = args.ccx or shutil.which("ccx") or shutil.which("ccx.exe")
    if not ccx_exe:
        log.error("CalculiX not found. Supply --ccx PATH.")
        return 3

    try:
        frd_path = run_ccx(ccx_exe, work_dir)
        results = FrdReader(str(frd_path)).parse()
    except Exception as exc:
        log.exception("Solver / FRD failure: %s", exc)
        return 2

    node_by_id = {n.id: n for n in mesh_data.nodes}
    fixed_nodes = set(next(
        f.node_ids for f in mesh_data.faces if f.face_id == fixed_face))
    loaded_nodes = set(next(
        f.node_ids for f in mesh_data.faces if f.face_id == loaded_face))

    # Σ Rx on fixed-face nodes
    rf_nids = results["reaction_x"]["node_ids"]
    rf_x    = results["reaction_x"]["values"]
    sum_rx = sum(rv for nid, rv in zip(rf_nids, rf_x) if nid in fixed_nodes)

    # Avg σxx over nodes with x ≤ frac·L, but > 0 to skip the encastre
    # singularity layer. Physical answer is exactly F/A in this strip.
    cutoff_lo = 0.02 * L_BEAM
    cutoff_hi = args.near_fixed_frac * L_BEAM
    s_nids = results["s11"]["node_ids"]
    s_vals = results["s11"]["values"]
    near_sxx = [sv for nid, sv in zip(s_nids, s_vals)
                if (n := node_by_id.get(nid)) is not None
                and cutoff_lo <= n.x <= cutoff_hi]
    avg_sxx = sum(near_sxx) / len(near_sxx) if near_sxx else float("nan")

    # Tip δx: avg Ux on loaded face
    u_nids = results["displacement_x"]["node_ids"]
    u_vals = results["displacement_x"]["values"]
    tip_list = [uv for nid, uv in zip(u_nids, u_vals) if nid in loaded_nodes]
    tip_ux = sum(tip_list) / len(tip_list) if tip_list else float("nan")

    expected_stress = F_AXIAL / area
    expected_disp   = F_AXIAL * L_BEAM / (area * E_MOD)

    print("─── Physics verification (cantilever, axial) ───")
    print(f"  Σ Rx (fixed)        = {sum_rx:+.4e} N       "
          f"expected {-F_AXIAL:+.4e} N   "
          f"Δ = {_pct(sum_rx, -F_AXIAL):+.2f}%")
    print(f"  avg σxx near x/L ≤ {args.near_fixed_frac:.2f}  "
          f"= {avg_sxx:.4e} Pa   expected {expected_stress:.4e} Pa   "
          f"Δ = {_pct(avg_sxx, expected_stress):+.2f}%   "
          f"(n={len(near_sxx)})")
    print(f"  tip δx (loaded face)= {tip_ux:.4e} m        "
          f"expected {expected_disp:.4e} m   "
          f"Δ = {_pct(tip_ux, expected_disp):+.2f}%")

    ok = (not math.isnan(sum_rx)
          and abs(sum_rx + F_AXIAL) / F_AXIAL < 0.02
          and not math.isnan(avg_sxx)
          and abs(avg_sxx - expected_stress) / expected_stress < 0.05
          and not math.isnan(tip_ux)
          and abs(tip_ux - expected_disp) / expected_disp < 0.05)
    print(f"  Status              = {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
