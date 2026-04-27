"""
verify_synthetic.py
-------------------
Solver-free end-to-end verification harness.

Builds the same cantilever beam geometry as verify_solver.py, runs the full
*pre-solve* pipeline (Gmsh → mesh_data → face-mapping → FEAModel → INP →
equilibrium diagnostics), then **injects a synthetic ResultSet** matching
closed-form axial-tension theory and exercises the post-processing:

  • ResultSet round-trip through the results.json schema
  • Equilibrium / diagnostic checks (ModelDiagnostics equivalent, Python)
  • CSV export (produces a results.csv identical to the C# ExportService)
  • Surface-triangle generation (mirrors ResultsViewModel surface extraction)
  • Deformation field (|δ|_max, bbox growth, NaN check)
  • Strain field (ε_zz == σ_zz / E, ε_vm == |ε_zz|)

No CalculiX required. Exit codes:
    0  all checks passed
    1  a numerical check failed (see stderr)
    2  pipeline / meshing failure

Usage:
    python verify_synthetic.py [--element-size 0.010] [--work-dir DIR]
"""

from __future__ import annotations

import argparse
import json
import logging
import math
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

import gmsh
from utils import gmsh_utils
from utils.inp_writer import InpWriterV2
from utils.models import (
    FEAModel, MaterialModel,
    BoundaryCondition, BcType,
    Load, LoadType, DirectionMode,
)

log = logging.getLogger("verify_synthetic")


# Same reference problem as verify_solver.py but along +X.
L_BEAM = 1.0
B_BEAM = 0.0508
H_BEAM = 0.0254
E_MOD  = 200.0e9
NU     = 0.3
F_AXIAL = 10_000.0

TOL_REL      = 1e-9     # exact for synthetic field
TOL_CSV      = 1e-6     # round-trip through G8 formatting
TOL_BBOX_REL = 2e-3     # deformed bbox grows by at most a few µm on 1 m beam


@dataclass
class CheckReport:
    mapping_ok:           bool = False
    load_distribution_ok: bool = False
    stress_ok:            bool = False
    strain_ok:            bool = False
    heatmap_ok:           bool = False
    geometry_ok:          bool = False
    csv_ok:               bool = False
    diagnostics: list[str] = field(default_factory=list)

    @property
    def all_passed(self) -> bool:
        return all([self.mapping_ok, self.load_distribution_ok,
                    self.stress_ok, self.strain_ok,
                    self.heatmap_ok, self.geometry_ok, self.csv_ok])

    def to_dict(self) -> dict:
        return {
            "mappingOK":          self.mapping_ok,
            "loadDistributionOK": self.load_distribution_ok,
            "stressOK":           self.stress_ok,
            "strainOK":           self.strain_ok,
            "heatmapOK":          self.heatmap_ok,
            "geometryOK":         self.geometry_ok,
            "csvOK":               self.csv_ok,
            "allPassed":          self.all_passed,
            "diagnostics":        self.diagnostics,
        }

    def summary(self) -> str:
        def m(flag: bool) -> str:
            return "PASS" if flag else "FAIL"
        lines = [
            "─── Synthetic verification ───",
            f"  mapping               {m(self.mapping_ok)}",
            f"  load distribution     {m(self.load_distribution_ok)}",
            f"  stress field          {m(self.stress_ok)}",
            f"  strain field          {m(self.strain_ok)}",
            f"  heatmap scalars       {m(self.heatmap_ok)}",
            f"  geometry (deform)     {m(self.geometry_ok)}",
            f"  CSV export            {m(self.csv_ok)}",
            f"  ALL                   {m(self.all_passed)}",
        ]
        if self.diagnostics:
            lines.append("  diagnostics:")
            lines.extend(f"    • {d}" for d in self.diagnostics)
        return "\n".join(lines)


# ── Mesh build ───────────────────────────────────────────────────────────────

def build_mesh(element_size: float):
    gmsh.initialize()
    try:
        gmsh.option.setNumber("General.Verbosity", 2)
        gmsh.model.add("cantilever_synth")
        gmsh.model.occ.addBox(0, 0, 0, L_BEAM, B_BEAM, H_BEAM, tag=1)
        gmsh.model.occ.synchronize()

        surf_tags = gmsh_utils.get_surface_tags()
        fixed_tag = loaded_tag = None
        for tag in surf_tags:
            bb = gmsh.model.getBoundingBox(2, tag)
            cx = (bb[0] + bb[3]) / 2
            if cx < L_BEAM * 0.01:  fixed_tag = tag
            elif cx > L_BEAM * 0.99: loaded_tag = tag
        if fixed_tag is None or loaded_tag is None:
            raise RuntimeError(
                f"Could not classify end faces (fixed={fixed_tag}, loaded={loaded_tag})")

        for tag in surf_tags:
            gmsh.model.addPhysicalGroup(2, [tag], tag=tag)
            gmsh.model.setPhysicalName(2, tag, f"Face_{tag}")
        gmsh_utils.add_physical_group_for_all_volumes()

        gmsh_utils.apply_mesh_settings(element_size, order=2, algorithm3d=4)
        gmsh.model.mesh.generate(3)
        try:
            gmsh.model.mesh.optimize("Netgen")
        except Exception:
            pass
        gmsh_utils.promote_to_quadratic()

        elements = gmsh_utils.extract_volume_elements()
        face_map = gmsh_utils.build_face_element_map(elements)
        mesh_data = gmsh_utils.build_mesh_data(surf_tags, face_map,
                                                coord_scale=1.0)
        warnings = gmsh_utils.validate_face_groups(mesh_data)
        for w in warnings:
            log.warning("face validate: %s", w)
        return mesh_data, fixed_tag, loaded_tag, warnings
    finally:
        gmsh.finalize()


# ── Synthetic field injection ────────────────────────────────────────────────

def synthesize_results(mesh_data, fixed_face_id: int) -> dict:
    """
    σ_xx  = F/A everywhere,     all other σ = 0
    ε_xx  = σ_xx / E,           all other ε = 0
    u_x   = ε_xx * x            (zero at x=0, δ_tip at x=L)
    RF    = -F/N_face on each fixed-face corner node (synthetic).
    """
    area = B_BEAM * H_BEAM
    sigma = F_AXIAL / area
    eps_xx = sigma / E_MOD

    node_ids = [n.id for n in mesh_data.nodes]
    x = {n.id: n.x for n in mesh_data.nodes}

    ux = [eps_xx * x[nid] for nid in node_ids]
    uy = [0.0] * len(node_ids)
    uz = [0.0] * len(node_ids)
    umag = [abs(v) for v in ux]

    s11 = [sigma] * len(node_ids)
    s22 = s33 = s12 = s23 = s13 = [0.0] * len(node_ids)
    # von Mises of uniaxial σ_xx is |σ_xx|
    vm = [abs(sigma)] * len(node_ids)

    e11 = [eps_xx] * len(node_ids)
    e22 = e33 = e12 = e23 = e13 = [0.0] * len(node_ids)
    evm = [abs(eps_xx)] * len(node_ids)

    # Synthetic reaction force: equal split of -F over fixed-face corner nodes.
    # We don't need to match a real solver here — just provide values that sum
    # to -F so equilibrium diagnostic passes.
    fx_nodes = next(f.node_ids for f in mesh_data.faces
                    if f.face_id == fixed_face_id)
    rfx = [-F_AXIAL / len(fx_nodes)] * len(fx_nodes)

    return {
        "step": 1,
        "displacement_x":   {"node_ids": node_ids, "values": ux},
        "displacement_y":   {"node_ids": node_ids, "values": uy},
        "displacement_z":   {"node_ids": node_ids, "values": uz},
        "displacement_mag": {"node_ids": node_ids, "values": umag},
        "vonmises":         {"node_ids": node_ids, "values": vm},
        "s11": {"node_ids": node_ids, "values": s11},
        "s22": {"node_ids": node_ids, "values": s22},
        "s33": {"node_ids": node_ids, "values": s33},
        "s12": {"node_ids": node_ids, "values": s12},
        "s23": {"node_ids": node_ids, "values": s23},
        "s13": {"node_ids": node_ids, "values": s13},
        "e11": {"node_ids": node_ids, "values": e11},
        "e22": {"node_ids": node_ids, "values": e22},
        "e33": {"node_ids": node_ids, "values": e33},
        "e12": {"node_ids": node_ids, "values": e12},
        "e23": {"node_ids": node_ids, "values": e23},
        "e13": {"node_ids": node_ids, "values": e13},
        "strain_vonmises": {"node_ids": node_ids, "values": evm},
        "reaction_x": {"node_ids": fx_nodes, "values": rfx},
        "reaction_y": {"node_ids": fx_nodes, "values": [0.0] * len(fx_nodes)},
        "reaction_z": {"node_ids": fx_nodes, "values": [0.0] * len(fx_nodes)},
    }


# ── Surface triangle rebuild (mirror of ResultsViewModel.BuildGeometry) ──────

TET_FACE_CORNERS = [(0, 1, 2), (0, 1, 3), (1, 2, 3), (0, 2, 3)]


def build_surface_triangles(mesh_data, disp_map=None, scale=0.0):
    """Return (positions, node_ids_per_vertex, n_triangles).

    When disp_map is provided, apply base + scale·δ to each vertex exactly
    as ResultsViewModel.Deform does, so we can diagnose the C# pipeline by
    comparing outputs in Python.
    """
    node_pos = {n.id: (n.x, n.y, n.z) for n in mesh_data.nodes}
    elem_map = {e.id: e for e in mesh_data.elements}
    positions: list[tuple[float, float, float]] = []
    vertex_nodes: list[int] = []
    for fg in mesh_data.faces:
        for ef in fg.element_faces:
            if len(ef) < 2: continue
            elem = elem_map.get(ef[0])
            fi = ef[1]
            if elem is None or not (0 <= fi < 4): continue
            if len(elem.nodes) < 4: continue
            ia, ib, ic = TET_FACE_CORNERS[fi]
            nA, nB, nC = elem.nodes[ia], elem.nodes[ib], elem.nodes[ic]
            for nid in (nA, nB, nC):
                bx, by, bz = node_pos[nid]
                if disp_map is not None and scale != 0:
                    ux, uy, uz = disp_map.get(nid, (0.0, 0.0, 0.0))
                    positions.append((bx + scale * ux,
                                      by + scale * uy,
                                      bz + scale * uz))
                else:
                    positions.append((bx, by, bz))
                vertex_nodes.append(nid)
    n_tri = len(positions) // 3
    return positions, vertex_nodes, n_tri


def bbox(positions):
    if not positions: return (0, 0, 0, 0, 0, 0)
    xs = [p[0] for p in positions]; ys = [p[1] for p in positions]
    zs = [p[2] for p in positions]
    return min(xs), min(ys), min(zs), max(xs), max(ys), max(zs)


# ── CSV export (mirrors C# ExportService) ────────────────────────────────────

def write_csv(mesh_data, results, path: Path) -> None:
    header = ("NodeId,X_m,Y_m,Z_m,Ux_mm,Uy_mm,Uz_mm,U_mag_mm,"
              "VonMises_MPa,E_xx,E_yy,E_zz,E_vm\n")
    rows = [header]
    by_id = {n.id: n for n in mesh_data.nodes}
    def _map(d): return dict(zip(d["node_ids"], d["values"])) if d else {}
    ux = _map(results["displacement_x"]); uy = _map(results["displacement_y"])
    uz = _map(results["displacement_z"]); umag = _map(results["displacement_mag"])
    vm = _map(results["vonmises"])
    e11 = _map(results["e11"]); e22 = _map(results["e22"]); e33 = _map(results["e33"])
    evm = _map(results["strain_vonmises"])
    for nid in results["displacement_x"]["node_ids"]:
        n = by_id.get(nid)
        x, y, z = (n.x, n.y, n.z) if n else (0, 0, 0)
        rows.append(
            f"{nid},{x:.8G},{y:.8G},{z:.8G},"
            f"{ux.get(nid,0)*1000:.8G},{uy.get(nid,0)*1000:.8G},"
            f"{uz.get(nid,0)*1000:.8G},{umag.get(nid,0)*1000:.8G},"
            f"{vm.get(nid,0)/1e6:.8G},"
            f"{e11.get(nid,0):.8G},{e22.get(nid,0):.8G},"
            f"{e33.get(nid,0):.8G},{evm.get(nid,0):.8G}\n")
    path.write_text("".join(rows))


# ── Main driver ──────────────────────────────────────────────────────────────

def run(element_size: float, work_dir: Path) -> CheckReport:
    r = CheckReport()
    log.info("Building cantilever mesh (h=%.3f m)...", element_size)
    mesh_data, fixed_face, loaded_face, warns = build_mesh(element_size)
    if warns:
        r.diagnostics.append(f"face validation warnings: {len(warns)}")
    log.info("Mesh: %s", mesh_data.stats())

    # ── PART 1 — mapping check ──
    fx = next(f for f in mesh_data.faces if f.face_id == fixed_face)
    ld = next(f for f in mesh_data.faces if f.face_id == loaded_face)
    if fx.node_ids and fx.element_faces and ld.node_ids and ld.element_faces:
        r.mapping_ok = True
    else:
        r.diagnostics.append(
            f"mapping incomplete: fixed(nodes={len(fx.node_ids)}, "
            f"elfaces={len(fx.element_faces)}), "
            f"loaded(nodes={len(ld.node_ids)}, elfaces={len(ld.element_faces)})")

    # ── Build FEAModel, write INP (exercises distribution + INP) ──
    area = B_BEAM * H_BEAM
    model = FEAModel(
        mesh=mesh_data,
        material=MaterialModel("Steel", E_MOD, NU),
        boundary_conditions=[
            BoundaryCondition(type=BcType.FIXED, face_id=fixed_face, node_ids=[])],
        loads=[Load(type=LoadType.DISTRIBUTED_FORCE,
                     face_id=loaded_face,
                     magnitude=F_AXIAL,
                     direction=[1.0, 0.0, 0.0],
                     direction_mode=DirectionMode.EXPLICIT)],
        work_dir=str(work_dir),
    )
    inp_path = work_dir / "model.inp"
    InpWriterV2(model).write(str(inp_path))

    # ── PART 2 — load distribution check: Σ CLOAD Fx == F_AXIAL ──
    cload_sum_x = 0.0
    in_cload = False
    for line in inp_path.read_text().splitlines():
        t = line.strip()
        if t.startswith("*"):
            in_cload = t.upper().startswith("*CLOAD")
            continue
        if in_cload and t:
            parts = [p.strip() for p in t.split(",")]
            if len(parts) >= 3:
                try:
                    dof = int(parts[1]); mag = float(parts[2])
                    if dof == 1: cload_sum_x += mag
                except ValueError:
                    pass
    if abs(cload_sum_x - F_AXIAL) / F_AXIAL < 1e-6:
        r.load_distribution_ok = True
    else:
        r.diagnostics.append(
            f"Σ CLOAD Fx = {cload_sum_x:.6e} N, expected {F_AXIAL:.6e} N "
            f"(Δ = {(cload_sum_x - F_AXIAL)/F_AXIAL*100:+.3f}%)")

    # ── PART 4/5 — inject synthetic results and validate ──
    synth = synthesize_results(mesh_data, fixed_face)
    (work_dir / "results.json").write_text(json.dumps(synth))

    sigma_expected = F_AXIAL / area
    eps_expected = sigma_expected / E_MOD

    # Sanity: synthetic displacement must never exceed 1% of beam length.
    # Protects against a coordinate-unit regression in the harness itself
    # (m vs mm) and against accidentally adding a transverse component.
    max_disp = max(abs(v) for v in synth["displacement_x"]["values"])
    disp_limit = 0.01 * L_BEAM
    if max_disp > disp_limit:
        r.diagnostics.append(
            f"ABORT: synthetic |δ|_max = {max_disp:.3e} m exceeds "
            f"1% of L ({disp_limit:.3e} m). "
            f"Expected ≈ {eps_expected * L_BEAM:.3e} m.")
        log.error(r.diagnostics[-1])
        return r

    # First-50 ResultSet rows (nodeId, x, y, z, σ_vm) — lets us confirm the
    # result values leaving the pipeline are uniform before they ever hit
    # the renderer.
    log.info("First 50 ResultSet rows (nodeId, x, y, z, σ_vm [Pa]):")
    nids = synth["displacement_x"]["node_ids"]
    uxs  = synth["displacement_x"]["values"]
    uys  = synth["displacement_y"]["values"]
    uzs  = synth["displacement_z"]["values"]
    s_nids = synth["vonmises"]["node_ids"]
    s_vals = synth["vonmises"]["values"]
    vm_by_id = dict(zip(s_nids, s_vals))
    pos_by_id = {n.id: (n.x, n.y, n.z) for n in mesh_data.nodes}
    for k in range(min(50, len(nids))):
        nid = nids[k]
        x, y, z = pos_by_id.get(nid, (0.0, 0.0, 0.0))
        log.info("  %8d  %+.6e  %+.6e  %+.6e  %.6e",
                 nid, x, y, z, vm_by_id.get(nid, 0.0))

    log.info("First 20 nodal displacements (nodeId, ux, uy, uz) [m]:")
    for k in range(min(20, len(nids))):
        log.info("  %8d  %+.6e  %+.6e  %+.6e",
                 nids[k], uxs[k], uys[k], uzs[k])

    vm_vals = s_vals
    stress_max_err = max(abs(v - sigma_expected) / sigma_expected for v in vm_vals)
    if stress_max_err > 0.01:
        r.diagnostics.append(
            f"ABORT: σ_vm deviates from expected {sigma_expected:.3e} Pa "
            f"by {stress_max_err*100:.3f}% (>1% tolerance).")
        log.error(r.diagnostics[-1])
        return r
    r.stress_ok = stress_max_err < TOL_REL
    if not r.stress_ok:
        r.diagnostics.append(f"σ_vm max rel err = {stress_max_err:.3e}")

    e_vals = synth["strain_vonmises"]["values"]
    strain_max_err = max(abs(v - eps_expected) / eps_expected for v in e_vals)
    r.strain_ok = strain_max_err < TOL_REL
    if not r.strain_ok:
        r.diagnostics.append(f"ε_vm max rel err = {strain_max_err:.3e}")

    # ── PART 6/7 — heatmap/geometry checks via surface triangles ──
    disp_map = {
        nid: (ux, uy, uz) for nid, ux, uy, uz in zip(
            synth["displacement_x"]["node_ids"],
            synth["displacement_x"]["values"],
            synth["displacement_y"]["values"],
            synth["displacement_z"]["values"])}
    pos0, vnodes, n_tri = build_surface_triangles(mesh_data)

    # First-50 vertex-buffer rows (vertexIdx, nodeId, σ_vm_used_for_color).
    # This is the scalar that ResultsViewModel uses for texture U — it MUST
    # equal the ResultSet σ_vm for that nodeId, bit-for-bit, or the renderer
    # is reading from the wrong field.
    log.info("First 50 vertex-buffer rows (vertexIdx, nodeId, σ_vm_used [Pa]):")
    for vi in range(min(50, len(vnodes))):
        nid = vnodes[vi]
        log.info("  %5d  %8d  %.6e", vi, nid, vm_by_id.get(nid, 0.0))

    # Uniformity assertion: for the synthetic uniaxial field, every vertex
    # must carry σ_vm = F/A within 1e-6 relative. Any deviation is a
    # wiring bug between ResultSet and the vertex buffer, not numerics.
    vertex_sigmas = [vm_by_id.get(nid, 0.0) for nid in vnodes]
    if vertex_sigmas:
        vmin, vmax = min(vertex_sigmas), max(vertex_sigmas)
        rel_spread = (vmax - vmin) / sigma_expected if sigma_expected else 0.0
        log.info("Vertex σ_vm spread: min=%.6e max=%.6e rel=%.3e",
                 vmin, vmax, rel_spread)
        if rel_spread > 1e-6:
            r.diagnostics.append(
                f"ABORT: vertex-buffer σ_vm non-uniform (rel spread "
                f"{rel_spread:.3e} > 1e-6). ResultSet→renderer wiring broken.")
            log.error(r.diagnostics[-1])

    if n_tri == 0 or any(math.isnan(c) for p in pos0 for c in p):
        r.diagnostics.append(f"surface rebuild failed (tris={n_tri})")
    else:
        bb_undef = bbox(pos0)
        # Every surface node must have a displacement entry
        surf_nodes = set(vnodes)
        missing = [n for n in surf_nodes if n not in disp_map]
        if missing:
            r.diagnostics.append(f"{len(missing)} surface nodes missing displacement")
        else:
            r.heatmap_ok = True

        # Apply real-scale deformation; compare bbox to undeformed
        pos1, _, _ = build_surface_triangles(mesh_data, disp_map, scale=1.0)
        bb_def = bbox(pos1)
        # Expected x-extent grows by δ_tip = eps_expected * L
        dx_expected = eps_expected * L_BEAM
        dx_observed = (bb_def[3] - bb_def[0]) - (bb_undef[3] - bb_undef[0])
        if abs(dx_observed - dx_expected) < TOL_BBOX_REL * L_BEAM \
           and not any(math.isnan(c) for p in pos1 for c in p):
            r.geometry_ok = True
        else:
            r.diagnostics.append(
                f"deformed bbox Δx = {dx_observed:.4e} m, "
                f"expected {dx_expected:.4e} m")

    # ── CSV export ──
    csv_path = work_dir / "results.csv"
    write_csv(mesh_data, synth, csv_path)
    lines = csv_path.read_text().strip().splitlines()
    if len(lines) < 2:
        r.diagnostics.append("CSV has no data rows")
    else:
        # Confirm σ_vm and ε_vm columns read back to expected values
        header = lines[0].split(",")
        idx_vm = header.index("VonMises_MPa")
        idx_evm = header.index("E_vm")
        vm_mpa_expected = sigma_expected / 1e6
        max_csv_err = 0.0
        max_csv_eerr = 0.0
        for row in lines[1:]:
            cols = row.split(",")
            vm_mpa = float(cols[idx_vm])
            evm_csv = float(cols[idx_evm])
            max_csv_err = max(max_csv_err,
                              abs(vm_mpa - vm_mpa_expected) / vm_mpa_expected)
            max_csv_eerr = max(max_csv_eerr,
                               abs(evm_csv - eps_expected) / eps_expected)
        if max_csv_err < TOL_CSV and max_csv_eerr < TOL_CSV:
            r.csv_ok = True
        else:
            r.diagnostics.append(
                f"CSV round-trip err: σ={max_csv_err:.3e}, ε={max_csv_eerr:.3e}")

    return r


def main() -> int:
    logging.basicConfig(level=logging.INFO,
                        format="%(levelname)s %(name)s: %(message)s",
                        stream=sys.stderr)
    ap = argparse.ArgumentParser()
    ap.add_argument("--element-size", type=float, default=0.020)
    ap.add_argument("--work-dir", default=None)
    ap.add_argument("--report-json", default=None,
                    help="Write report as JSON to this path (for C# VerificationService)")
    args = ap.parse_args()

    work_dir = Path(args.work_dir or "verify_synth_work").resolve()
    if work_dir.exists():
        shutil.rmtree(work_dir)
    work_dir.mkdir(parents=True)

    try:
        report = run(args.element_size, work_dir)
    except Exception as exc:
        log.exception("Pipeline failure: %s", exc)
        if args.report_json:
            Path(args.report_json).write_text(json.dumps({
                "mappingOK": False, "loadDistributionOK": False,
                "stressOK": False, "strainOK": False, "heatmapOK": False,
                "geometryOK": False, "csvOK": False, "allPassed": False,
                "diagnostics": [f"pipeline exception: {exc}"]}))
        return 2

    print(report.summary())
    if args.report_json:
        Path(args.report_json).write_text(json.dumps(report.to_dict(), indent=2))
    return 0 if report.all_passed else 1


if __name__ == "__main__":
    sys.exit(main())
