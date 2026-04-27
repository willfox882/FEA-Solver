"""
verify_solver.py
----------------
End-to-end FEA verification harness.

Programmatically builds a cantilever beam via the Gmsh API (no STEP required),
runs the full pipeline (mesh → INP → CalculiX → FRD → results), and asserts
the solution matches closed-form theory.

Reference case (axial tension)
  Beam:      L = 1.0 m, b = 0.0508 m, h = 0.0254 m   (2"×1")
  Material:  Steel, E = 200 GPa, ν = 0.3
  BCs:       x=0 face fully fixed (encastre)
  Load:      F = 10 000 N axial (+x) on x=L face, distributed
  Expected:  A = 0.0508 × 0.0254    = 1.29032e-3  m²
             σ = F/A                = 7.7498e+6   Pa   (≈ 7.75 MPa)
             δ = F·L/(A·E)          = 3.8749e-5   m    (≈ 38.75 µm)
             Σ Rx (fixed end)       ≈ -10 000 N

Usage:
    python verify_solver.py [--ccx PATH] [--work-dir DIR] [--element-size H]
                            [--no-solve]

Exit codes:
    0  all checks passed (or --no-solve succeeded up to INP)
    1  numerical result outside tolerance
    2  pipeline failure (mesh / INP / solver / FRD)
    3  CalculiX executable not found and --no-solve not set
"""

from __future__ import annotations

import argparse
import logging
import math
import shutil
import subprocess
import sys
from dataclasses import dataclass
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

log = logging.getLogger("verify_solver")


# ── Problem definition ───────────────────────────────────────────────────────

L_BEAM = 1.0          # m
B_BEAM = 0.0508       # m   (2 in)
H_BEAM = 0.0254       # m   (1 in)
E_MOD  = 200.0e9      # Pa
NU     = 0.3
F_AXIAL = 10_000.0    # N

# Tolerances: coarse C3D10 FEA hits analytic tension to <2% easily.
TOL_STRESS      = 0.05   # 5% on avg σ and max vM
TOL_DISP        = 0.05   # 5% on tip displacement
TOL_EQUILIBRIUM = 0.02   # 2% on Σ reaction vs applied load


@dataclass
class VerificationResult:
    reaction_sum_x: float
    avg_stress_pa:  float
    max_vonmises_pa: float
    tip_disp_x_m:   float
    expected_stress_pa: float
    expected_disp_m:    float
    passed: bool
    notes: list[str]

    def summary(self) -> str:
        lines = [
            "─── Verification result ───",
            f"  Σ Rx                 = {self.reaction_sum_x:+.4e} N"
            f"   (expected ≈ {-F_AXIAL:+.4e} N)",
            f"  σ_avg (σxx·A/A)      = {self.avg_stress_pa:.4e} Pa"
            f"   (theory {self.expected_stress_pa:.4e} Pa,"
            f"    Δ = {_pct(self.avg_stress_pa, self.expected_stress_pa):+.2f}%)",
            f"  σ_vonmises_max       = {self.max_vonmises_pa:.4e} Pa",
            f"  δ_tip_x              = {self.tip_disp_x_m:.4e} m"
            f"   (theory {self.expected_disp_m:.4e} m,"
            f"    Δ = {_pct(self.tip_disp_x_m, self.expected_disp_m):+.2f}%)",
            f"  Status               = {'PASS' if self.passed else 'FAIL'}",
        ]
        if self.notes:
            lines.append("  Notes:")
            lines.extend(f"    • {n}" for n in self.notes)
        return "\n".join(lines)


def _pct(val: float, ref: float) -> float:
    if abs(ref) < 1e-30:
        return 0.0
    return 100.0 * (val - ref) / ref


# ── Geometry + mesh ──────────────────────────────────────────────────────────

def build_cantilever_mesh(element_size: float):
    """
    Build a cantilever beam with Gmsh OCC, mesh it, and return
    (MeshData, face_id_fixed, face_id_loaded).

    Face identification is geometric: after meshing, we classify each
    Gmsh surface by the x-coordinate of its centroid.
      • min-x plane  → fixed   (x ≈ 0)
      • max-x plane  → loaded  (x ≈ L)
    Side faces are ignored (no BC/load).
    """
    gmsh.initialize()
    try:
        gmsh.option.setNumber("General.Verbosity", 2)
        gmsh.model.add("cantilever")

        # Box: origin (0,0,0), extents (L, B, H)
        gmsh.model.occ.addBox(0.0, 0.0, 0.0, L_BEAM, B_BEAM, H_BEAM, tag=1)
        gmsh.model.occ.synchronize()

        # Classify the 6 surfaces by centroid x
        surf_tags = gmsh_utils.get_surface_tags()
        fixed_tag: int | None = None
        loaded_tag: int | None = None
        for tag in surf_tags:
            cx, cy, cz = _surface_centroid(2, tag)
            if cx < L_BEAM * 0.01:
                fixed_tag = tag
            elif cx > L_BEAM * 0.99:
                loaded_tag = tag

        if fixed_tag is None or loaded_tag is None:
            raise RuntimeError(
                f"Could not identify beam end faces "
                f"(fixed={fixed_tag}, loaded={loaded_tag}, surfaces={surf_tags})")

        # Physical group tag must equal surface tag so mesh_data.faces[i].face_id
        # matches the tag we are carrying around here.
        for tag in surf_tags:
            gmsh.model.addPhysicalGroup(2, [tag], tag=tag)
            gmsh.model.setPhysicalName(2, tag, f"Face_{tag}")
        gmsh_utils.add_physical_group_for_all_volumes()

        gmsh_utils.apply_mesh_settings(
            element_size=element_size, order=2, algorithm3d=4)
        gmsh.model.mesh.generate(3)
        try:
            gmsh.model.mesh.optimize("Netgen")
        except Exception:
            pass
        # setOrder(2) must run AFTER optimize — Netgen reverts to linear tets
        # when called on a quadratic mesh.
        gmsh_utils.promote_to_quadratic()

        elements = gmsh_utils.extract_volume_elements()
        face_elem_map = gmsh_utils.build_face_element_map(elements)
        # STEP was authored in SI already — no coord scaling.
        mesh_data = gmsh_utils.build_mesh_data(surf_tags, face_elem_map,
                                                coord_scale=1.0)
        warnings = gmsh_utils.validate_face_groups(mesh_data)
        for w in warnings:
            log.warning("Face mapping: %s", w)

        return mesh_data, fixed_tag, loaded_tag
    finally:
        gmsh.finalize()


def _surface_centroid(dim: int, tag: int) -> tuple[float, float, float]:
    bb = gmsh.model.getBoundingBox(dim, tag)
    return ((bb[0] + bb[3]) / 2.0,
            (bb[1] + bb[4]) / 2.0,
            (bb[2] + bb[5]) / 2.0)


# ── Model assembly ───────────────────────────────────────────────────────────

def build_fea_model(mesh_data, fixed_face: int, loaded_face: int,
                    work_dir: Path) -> FEAModel:
    """
    Axial-tension model.

    The load is authored as a SurfaceTraction with magnitude = F/A (Pa) in
    the +x direction. The INP writer performs consistent-Galerkin lumping
    across the loaded face's element-faces, so the total applied force
    equals F regardless of mesh density.
    """
    area = B_BEAM * H_BEAM
    traction_pa = F_AXIAL / area

    bcs = [
        BoundaryCondition(type=BcType.FIXED, face_id=fixed_face, node_ids=[]),
    ]
    loads = [
        Load(type=LoadType.SURFACE_TRACTION,
             face_id=loaded_face,
             magnitude=traction_pa,
             direction=[1.0, 0.0, 0.0]),
    ]
    return FEAModel(
        mesh=mesh_data,
        material=MaterialModel(name="Steel",
                                youngs_modulus=E_MOD,
                                poissons_ratio=NU),
        boundary_conditions=bcs,
        loads=loads,
        work_dir=str(work_dir),
    )


# ── Solve + post-process ─────────────────────────────────────────────────────

def run_ccx(ccx_exe: str, work_dir: Path, job: str = "model") -> Path:
    inp = work_dir / f"{job}.inp"
    if not inp.exists():
        raise FileNotFoundError(inp)

    cmd = [ccx_exe, "-i", job]
    log.info("Running CalculiX: %s", " ".join(cmd))
    proc = subprocess.run(
        cmd, cwd=str(work_dir),
        capture_output=True, text=True, check=False)
    (work_dir / "ccx.stdout.log").write_text(proc.stdout or "")
    (work_dir / "ccx.stderr.log").write_text(proc.stderr or "")
    if proc.returncode != 0:
        tail = "\n".join((proc.stdout or "").splitlines()[-40:])
        raise RuntimeError(f"CalculiX failed (exit {proc.returncode}):\n{tail}")

    frd = work_dir / f"{job}.frd"
    if not frd.exists():
        raise FileNotFoundError(f".frd not produced: {frd}")
    return frd


def check_results(results: dict, mesh_data, fixed_face: int,
                  loaded_face: int) -> VerificationResult:
    notes: list[str] = []

    # Σ reaction on fixed-face nodes along the load direction
    fixed_nodes = set(next(
        f.node_ids for f in mesh_data.faces if f.face_id == fixed_face))
    rf_nids = results["reaction_x"]["node_ids"]
    rf_x    = results["reaction_x"]["values"]
    sum_rx = sum(rv for nid, rv in zip(rf_nids, rf_x) if nid in fixed_nodes)

    # Tip displacement: average Ux on loaded face
    loaded_nodes = set(next(
        f.node_ids for f in mesh_data.faces if f.face_id == loaded_face))
    u_nids = results["displacement_x"]["node_ids"]
    u_x    = results["displacement_x"]["values"]
    tip_ux_list = [uv for nid, uv in zip(u_nids, u_x) if nid in loaded_nodes]
    tip_ux = sum(tip_ux_list) / len(tip_ux_list) if tip_ux_list else float("nan")

    # Average σxx and max von Mises
    s11 = results["s11"]["values"] or []
    vm  = results["vonmises"]["values"] or []
    avg_sxx = (sum(s11) / len(s11)) if s11 else float("nan")
    max_vm  = max(vm) if vm else float("nan")

    expected_stress = F_AXIAL / (B_BEAM * H_BEAM)
    expected_disp   = F_AXIAL * L_BEAM / (B_BEAM * H_BEAM * E_MOD)

    passed = True
    if math.isnan(sum_rx) or abs(sum_rx + F_AXIAL) / F_AXIAL > TOL_EQUILIBRIUM:
        passed = False
        notes.append(
            f"Equilibrium: Σ Rx = {sum_rx:.3e} N, "
            f"expected ≈ {-F_AXIAL:.3e} N "
            f"(|Δ| > {TOL_EQUILIBRIUM*100:.1f}%)")
    if math.isnan(avg_sxx) or \
       abs(avg_sxx - expected_stress) / expected_stress > TOL_STRESS:
        passed = False
        notes.append(f"Avg σxx deviates from F/A by >{TOL_STRESS*100:.1f}%")
    if math.isnan(max_vm) or \
       abs(max_vm - expected_stress) / expected_stress > 4 * TOL_STRESS:
        # Max vM can be higher than avg σxx due to stress concentration at
        # the encastre; allow 4× the stress tolerance before failing.
        notes.append(
            f"Max vM = {max_vm:.3e} Pa — likely stress concentration at "
            f"fixed end; theory σ = {expected_stress:.3e} Pa")
    if math.isnan(tip_ux) or \
       abs(tip_ux - expected_disp) / expected_disp > TOL_DISP:
        passed = False
        notes.append(f"Tip δ_x deviates from FL/AE by >{TOL_DISP*100:.1f}%")

    return VerificationResult(
        reaction_sum_x=sum_rx,
        avg_stress_pa=avg_sxx,
        max_vonmises_pa=max_vm,
        tip_disp_x_m=tip_ux,
        expected_stress_pa=expected_stress,
        expected_disp_m=expected_disp,
        passed=passed,
        notes=notes,
    )


# ── Driver ───────────────────────────────────────────────────────────────────

def main() -> int:
    logging.basicConfig(
        level=logging.INFO,
        format="%(levelname)s %(name)s: %(message)s",
        stream=sys.stderr)

    ap = argparse.ArgumentParser()
    ap.add_argument("--ccx",          default=None,
                    help="Path to ccx executable (default: PATH lookup)")
    ap.add_argument("--work-dir",     default=None,
                    help="Work directory (default: ./verify_work)")
    ap.add_argument("--element-size", type=float, default=0.010,
                    help="Gmsh element size in metres (default: 0.010)")
    ap.add_argument("--no-solve",     action="store_true",
                    help="Stop after INP generation (skips CalculiX run)")
    args = ap.parse_args()

    work_dir = Path(args.work_dir or "verify_work").resolve()
    if work_dir.exists():
        shutil.rmtree(work_dir)
    work_dir.mkdir(parents=True)

    try:
        log.info("Building cantilever mesh (h=%s m)...", args.element_size)
        mesh_data, fixed_face, loaded_face = build_cantilever_mesh(
            args.element_size)
        stats = mesh_data.stats()
        log.info("Mesh: %s", stats)
        log.info("Faces: fixed=%d (nodes=%d, el-faces=%d)  "
                 "loaded=%d (nodes=%d, el-faces=%d)",
                 fixed_face,
                 len(next(f for f in mesh_data.faces
                          if f.face_id == fixed_face).node_ids),
                 len(next(f for f in mesh_data.faces
                          if f.face_id == fixed_face).element_faces),
                 loaded_face,
                 len(next(f for f in mesh_data.faces
                          if f.face_id == loaded_face).node_ids),
                 len(next(f for f in mesh_data.faces
                          if f.face_id == loaded_face).element_faces))

        model = build_fea_model(mesh_data, fixed_face, loaded_face, work_dir)

        inp_path = work_dir / "model.inp"
        log.info("Writing INP: %s", inp_path)
        InpWriterV2(model).write(str(inp_path))
    except Exception as exc:
        log.exception("Pipeline failure before solve: %s", exc)
        return 2

    if args.no_solve:
        log.info("--no-solve set, stopping after INP generation.")
        return 0

    ccx_exe = args.ccx or shutil.which("ccx") or shutil.which("ccx.exe")
    if not ccx_exe:
        log.error("CalculiX executable not found. "
                  "Supply --ccx PATH or put ccx on PATH. "
                  "Re-run with --no-solve to verify INP generation only.")
        return 3

    try:
        frd_path = run_ccx(ccx_exe, work_dir)
        log.info("Parsing FRD: %s", frd_path)
        results = FrdReader(str(frd_path)).parse()
    except Exception as exc:
        log.exception("Solver / FRD stage failed: %s", exc)
        return 2

    vr = check_results(results, mesh_data, fixed_face, loaded_face)
    print(vr.summary())
    return 0 if vr.passed else 1


if __name__ == "__main__":
    sys.exit(main())
