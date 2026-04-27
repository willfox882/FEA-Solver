"""
test_load_types.py
------------------
Physics checks on every surface-load path in InpWriterV2.

For each load we verify the quantity that equilibrium demands:
  • Pressure          → emits *DLOAD Pk lines, one per element-face
  • SurfaceTraction   → Σ CLOAD = traction × total_area × direction
  • DistributedForce  → Σ CLOAD = F_total × direction (independent of A)
  • Moment            → Σ (r × F) about the face centroid = applied torque
  • Torque            → same as Moment (alias)

Static geometry: no solver required.
"""
from __future__ import annotations

import math
import os
import sys
import numpy as np
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__),
                                 "../../src/FEASolver.Scripts"))

from utils.models import (
    Node, Element, FaceGroup, MeshData,
    FEAModel, MaterialModel,
    BoundaryCondition, BcType,
    Load, LoadType, LocalizedLoadData, LocationMode, DirectionMode,
)
from utils.inp_writer import InpWriterV2


# ── Shared fixtures ──────────────────────────────────────────────────────────

def _planar_quad_mesh_z0() -> MeshData:
    """
    Two C3D4 tets forming a 1×1 square face at z=0 (face_id=10). Apices are
    above the face so the outward normal of the face is -z.
    Face area = 1.0 m², face centroid = (0.5, 0.5, 0).
    """
    nodes = [
        Node(1, 0.0, 0.0, 0.0),
        Node(2, 1.0, 0.0, 0.0),
        Node(3, 1.0, 1.0, 0.0),
        Node(4, 0.0, 1.0, 0.0),
        Node(5, 0.5, 0.5, 1.0),   # apex
    ]
    # Two triangles covering the 1×1 square, each in a tet with apex=5.
    elements = [
        Element(1, "C3D4", [1, 2, 3, 5]),   # face 0 (0,1,2)=nodes 1,2,3
        Element(2, "C3D4", [1, 3, 4, 5]),   # face 0 (0,1,2)=nodes 1,3,4
    ]
    faces = [FaceGroup(
        face_id=10,
        element_faces=[[1, 0], [2, 0]],
        node_ids=[1, 2, 3, 4],
    )]
    return MeshData(nodes=nodes, elements=elements, faces=faces)


def _tilted_face_mesh(angle_deg: float) -> tuple[MeshData, np.ndarray]:
    """
    Square face tilted about the y-axis by `angle_deg` (rotation x→z).
    Returns (mesh, expected_outward_normal).
    Face area = 1 m², face_id = 20.

    Apex placed on the +Z side of the un-rotated face; after rotation about
    +y by θ, the outward normal should point in direction (-sin θ, 0, -cos θ)
    because the un-rotated normal (outward away from apex at +z) is -z.
    """
    th = math.radians(angle_deg)
    # Rotation about +y: (x,z) → (x cosθ + z sinθ, -x sinθ + z cosθ)
    def rot(p):
        x, y, z = p
        return (x * math.cos(th) + z * math.sin(th),
                y,
                -x * math.sin(th) + z * math.cos(th))

    base = [
        (0.0, 0.0, 0.0),
        (1.0, 0.0, 0.0),
        (1.0, 1.0, 0.0),
        (0.0, 1.0, 0.0),
        (0.5, 0.5, 1.0),    # apex
    ]
    rotated = [rot(p) for p in base]
    nodes = [Node(i + 1, *rotated[i]) for i in range(5)]
    elements = [
        Element(1, "C3D4", [1, 2, 3, 5]),
        Element(2, "C3D4", [1, 3, 4, 5]),
    ]
    faces = [FaceGroup(
        face_id=20,
        element_faces=[[1, 0], [2, 0]],
        node_ids=[1, 2, 3, 4],
    )]
    mesh = MeshData(nodes=nodes, elements=elements, faces=faces)
    # Un-rotated outward normal = (0,0,-1); apply rotation
    n_un = np.array([0.0, 0.0, -1.0])
    n_rot = np.array([n_un[0] * math.cos(th) + n_un[2] * math.sin(th),
                      0.0,
                      -n_un[0] * math.sin(th) + n_un[2] * math.cos(th)])
    return mesh, n_rot


def _model_with(mesh: MeshData, load: Load) -> FEAModel:
    # Fix face id uses the same face as load (not physical, just satisfies
    # the pre-write validator; we inspect CLOAD, not the solve).
    bc_face = mesh.faces[0].face_id
    return FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[BoundaryCondition(
            BcType.FIXED, bc_face, mesh.faces[0].node_ids)],
        loads=[load],
        work_dir="/tmp",
    )


def _parse_cload(inp_text: str) -> list[tuple[int, int, float]]:
    out = []
    in_block = False
    for line in inp_text.splitlines():
        s = line.strip()
        if s.startswith("*CLOAD"):
            in_block = True
            continue
        if in_block and s.startswith("*"):
            break
        if in_block and s:
            parts = [p.strip() for p in s.split(",")]
            try:
                out.append((int(parts[0]), int(parts[1]), float(parts[2])))
            except (ValueError, IndexError):
                pass
    return out


def _parse_dload(inp_text: str) -> list[tuple[int, str, float]]:
    out = []
    in_block = False
    for line in inp_text.splitlines():
        s = line.strip()
        if s.startswith("*DLOAD"):
            in_block = True
            continue
        if in_block and s.startswith("*"):
            break
        if in_block and s:
            parts = [p.strip() for p in s.split(",")]
            try:
                out.append((int(parts[0]), parts[1], float(parts[2])))
            except (ValueError, IndexError):
                pass
    return out


def _cload_vec(entries) -> np.ndarray:
    F = np.zeros(3)
    for nid, dof, v in entries:
        F[dof - 1] += v
    return F


# ── Pressure ────────────────────────────────────────────────────────────────

def test_pressure_emits_dload_per_element_face(tmp_path):
    mesh = _planar_quad_mesh_z0()
    load = Load(type=LoadType.PRESSURE, face_id=10, magnitude=5.0e5,
                direction=None)
    inp = tmp_path / "p.inp"
    InpWriterV2(_model_with(mesh, load)).write(str(inp))
    dload = _parse_dload(inp.read_text())
    # Two element-faces → two *DLOAD lines with P1 label
    assert len(dload) == 2
    assert {eid for eid, _, _ in dload} == {1, 2}
    assert all(lbl == "P1" for _, lbl, _ in dload)
    assert all(v == pytest.approx(5.0e5) for _, _, v in dload)


# ── SurfaceTraction (explicit direction) ────────────────────────────────────

def test_surface_traction_sum_matches_traction_times_area(tmp_path):
    mesh = _planar_quad_mesh_z0()
    traction_pa = 1.0e4
    load = Load(type=LoadType.SURFACE_TRACTION, face_id=10,
                magnitude=traction_pa, direction=[0.0, 0.0, 1.0])
    inp = tmp_path / "t.inp"
    InpWriterV2(_model_with(mesh, load)).write(str(inp))
    F = _cload_vec(_parse_cload(inp.read_text()))
    expected = np.array([0.0, 0.0, traction_pa * 1.0])   # A=1 m²
    assert np.allclose(F, expected, atol=1e-6)


# ── DistributedForce (total force) ──────────────────────────────────────────

def test_distributed_force_sum_matches_total(tmp_path):
    mesh = _planar_quad_mesh_z0()
    load = Load(type=LoadType.DISTRIBUTED_FORCE, face_id=10,
                magnitude=2500.0, direction=[1.0, 0.0, 0.0])
    inp = tmp_path / "d.inp"
    InpWriterV2(_model_with(mesh, load)).write(str(inp))
    F = _cload_vec(_parse_cload(inp.read_text()))
    assert np.allclose(F, np.array([2500.0, 0.0, 0.0]), atol=1e-6)


# ── Normal-outward direction resolution ─────────────────────────────────────

def test_normal_outward_resolves_for_flat_face(tmp_path):
    """Flat z=0 face, apex at +z → outward = -z. DistributedForce with
    NormalOutward should sum to (0, 0, -F)."""
    mesh = _planar_quad_mesh_z0()
    F_total = 1000.0
    load = Load(type=LoadType.DISTRIBUTED_FORCE, face_id=10,
                magnitude=F_total, direction=None,
                direction_mode=DirectionMode.NORMAL_OUTWARD)
    inp = tmp_path / "n.inp"
    InpWriterV2(_model_with(mesh, load)).write(str(inp))
    F = _cload_vec(_parse_cload(inp.read_text()))
    assert np.allclose(F, np.array([0.0, 0.0, -F_total]), atol=1e-6)


def test_normal_inward_is_negated_outward(tmp_path):
    mesh = _planar_quad_mesh_z0()
    F_total = 1000.0
    load_in = Load(type=LoadType.DISTRIBUTED_FORCE, face_id=10,
                    magnitude=F_total, direction=None,
                    direction_mode=DirectionMode.NORMAL_INWARD)
    inp = tmp_path / "ni.inp"
    InpWriterV2(_model_with(mesh, load_in)).write(str(inp))
    F = _cload_vec(_parse_cload(inp.read_text()))
    assert np.allclose(F, np.array([0.0, 0.0, +F_total]), atol=1e-6)


@pytest.mark.parametrize("angle_deg", [0.0, 30.0, 45.0, -30.0, 60.0, 90.0])
def test_normal_outward_on_tilted_face(angle_deg, tmp_path):
    """Auto-normal must follow arbitrary-angle geometry, no user unit vec."""
    mesh, n_expected = _tilted_face_mesh(angle_deg)
    F_total = 1000.0
    load = Load(type=LoadType.DISTRIBUTED_FORCE, face_id=20,
                magnitude=F_total, direction=None,
                direction_mode=DirectionMode.NORMAL_OUTWARD)
    inp = tmp_path / f"tilt_{angle_deg}.inp"
    InpWriterV2(_model_with(mesh, load)).write(str(inp))
    F = _cload_vec(_parse_cload(inp.read_text()))
    expected = F_total * n_expected
    assert np.allclose(F, expected, atol=1e-4), \
        f"angle={angle_deg}: F={F}  expected={expected}"


# ── Moment / Torque: Σ(r × F) = applied T, Σ F = 0 ─────────────────────────

def _cload_by_node(inp_text: str) -> dict[int, np.ndarray]:
    by_n: dict[int, np.ndarray] = {}
    for nid, dof, v in _parse_cload(inp_text):
        by_n.setdefault(nid, np.zeros(3))[dof - 1] += v
    return by_n


def test_moment_nodal_forces_sum_to_zero_and_produce_torque(tmp_path):
    """
    Moment about +z on face 10 (z=0 square): nodal force couple should:
      • Σ F ≈ 0  (pure torque, no net force)
      • Σ (r_i − r_c) × F_i ≈ (0, 0, T) about the face centroid.
    """
    mesh = _planar_quad_mesh_z0()
    T = 500.0
    load = Load(type=LoadType.MOMENT, face_id=10, magnitude=T,
                direction=None,
                localized=LocalizedLoadData(
                    mode=LocationMode.ABSOLUTE_XYZ,
                    xyz=[0.5, 0.5, 0.0],
                    axis_direction=[0.0, 0.0, 1.0],
                    magnitude=T))
    inp = tmp_path / "m.inp"
    InpWriterV2(_model_with(mesh, load)).write(str(inp))
    forces = _cload_by_node(inp.read_text())
    node_pos = {n.id: np.array([n.x, n.y, n.z]) for n in mesh.nodes}
    F_sum = sum(forces.values(), start=np.zeros(3))
    # Σ F must vanish for a pure couple
    assert np.allclose(F_sum, np.zeros(3), atol=1e-6)
    # Σ τ about centroid must equal applied torque
    centroid = np.array([0.5, 0.5, 0.0])
    tau = np.zeros(3)
    for nid, F in forces.items():
        r = node_pos[nid] - centroid
        tau += np.cross(r, F)
    assert np.allclose(tau, np.array([0.0, 0.0, T]), rtol=1e-3, atol=1e-3), \
        f"torque sum = {tau}  expected {[0,0,T]}"


def test_torque_alias_behaves_identically_to_moment(tmp_path):
    mesh = _planar_quad_mesh_z0()
    T = 250.0
    localized = LocalizedLoadData(mode=LocationMode.ABSOLUTE_XYZ,
                                    xyz=[0.5, 0.5, 0.0],
                                    axis_direction=[0.0, 0.0, 1.0],
                                    magnitude=T)
    m = Load(LoadType.MOMENT, 10, T, None, localized)
    t = Load(LoadType.TORQUE, 10, T, None, localized)
    InpWriterV2(_model_with(mesh, m)).write(str(tmp_path / "m2.inp"))
    InpWriterV2(_model_with(mesh, t)).write(str(tmp_path / "t2.inp"))
    m_cload = _cload_by_node((tmp_path / "m2.inp").read_text())
    t_cload = _cload_by_node((tmp_path / "t2.inp").read_text())
    assert set(m_cload.keys()) == set(t_cload.keys())
    for k in m_cload:
        assert np.allclose(m_cload[k], t_cload[k], atol=1e-6)


def test_inp_is_pure_ascii(tmp_path):
    """Regression: ccx exit 201 was triggered by a stray non-ASCII byte
    (em-dash at offset 21 in *HEADING). Ensure every INP byte is < 128."""
    mesh = _planar_quad_mesh_z0()
    load = Load(LoadType.SURFACE_TRACTION, 10, 1000.0, [0.0, 0.0, -1.0], None,
                direction_mode=DirectionMode.EXPLICIT)
    out = tmp_path / "model.inp"
    InpWriterV2(_model_with(mesh, load)).write(str(out))
    data = out.read_bytes()
    bad = [(i, b) for i, b in enumerate(data) if b > 127]
    assert not bad, f"non-ASCII bytes in INP: {bad[:5]}"
