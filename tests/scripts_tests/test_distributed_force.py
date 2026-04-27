"""
test_distributed_force.py
-------------------------
Verify the DistributedForce load type:
  • INP writer emits *CLOAD entries summing to F_total on the given face.
  • Works with C3D4 (corner lumping) and C3D10 (midside lumping).
  • Respects arbitrary force direction.
  • Empty element-face list → no CLOAD entries.
"""
from __future__ import annotations
import math
import os
import sys
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__),
                                "../../src/FEASolver.Scripts"))

from utils.models import (
    Node, Element, FaceGroup, MeshData,
    FEAModel, MaterialModel,
    BoundaryCondition, BcType,
    Load, LoadType,
)
from utils.inp_writer import InpWriterV2


# ── Helpers ──────────────────────────────────────────────────────────────────

def _two_tet_mesh_c3d4() -> MeshData:
    """
    Two C3D4 tets sharing face 0 (nodes 1-2-3) to give a face_id=99 with
    two element-faces, testing accumulation. Nodes are on the z=0 plane
    for simple area computation. Total face area = 0.5 (m²).
    """
    nodes = [
        Node(1, 0.0, 0.0, 0.0),
        Node(2, 1.0, 0.0, 0.0),
        Node(3, 0.0, 1.0, 0.0),
        Node(4, 0.1, 0.1, 1.0),    # apex above (element 1)
        Node(5, 0.1, 0.1, -1.0),   # apex below (element 2)
    ]
    elements = [
        Element(1, "C3D4", [1, 2, 3, 4]),   # face 0 (0,1,2) → nodes 1,2,3
        Element(2, "C3D4", [1, 2, 3, 5]),   # face 0 (0,1,2) → nodes 1,2,3
    ]
    faces = [FaceGroup(
        face_id=99,
        element_faces=[[1, 0], [2, 0]],
        node_ids=[1, 2, 3],
    )]
    return MeshData(nodes=nodes, elements=elements, faces=faces)


def _c3d10_face_mesh() -> MeshData:
    """
    Single C3D10 with face 0 (corners 0,1,2; midsides 4,5,6) on z=0.
    Face area = 0.5 (m²).
    Node numbering follows Abaqus/CCX convention:
      corners:  1 2 3 4
      midsides: 5(1-2) 6(2-3) 7(3-1) 8(1-4) 9(2-4) 10(3-4)
    In zero-based Python list indices 0-9, that's:
      [c0, c1, c2, c3, m01, m12, m20, m03, m13, m23]
    """
    nodes = [
        Node( 1, 0.0, 0.0, 0.0),   # c0
        Node( 2, 1.0, 0.0, 0.0),   # c1
        Node( 3, 0.0, 1.0, 0.0),   # c2
        Node( 4, 0.0, 0.0, 1.0),   # c3
        Node( 5, 0.5, 0.0, 0.0),   # m01
        Node( 6, 0.5, 0.5, 0.0),   # m12
        Node( 7, 0.0, 0.5, 0.0),   # m20
        Node( 8, 0.0, 0.0, 0.5),   # m03
        Node( 9, 0.5, 0.0, 0.5),   # m13
        Node(10, 0.0, 0.5, 0.5),   # m23
    ]
    elements = [Element(1, "C3D10", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10])]
    faces = [FaceGroup(
        face_id=77,
        element_faces=[[1, 0]],
        # Face 0 corners + its 3 midsides
        node_ids=[1, 2, 3, 5, 6, 7],
    )]
    return MeshData(nodes=nodes, elements=elements, faces=faces)


def _model(mesh: MeshData, load: Load) -> FEAModel:
    return FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[BoundaryCondition(BcType.FIXED,
                                                face_id=mesh.faces[0].face_id,
                                                node_ids=mesh.faces[0].node_ids)],
        loads=[load],
        work_dir="/tmp",
    )


def _cload_vec_sum(inp_text: str) -> tuple[float, float, float]:
    """Parse *CLOAD block and sum contributions per DOF."""
    fx = fy = fz = 0.0
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
            if len(parts) >= 3:
                try:
                    dof = int(parts[1])
                    val = float(parts[2])
                except ValueError:
                    continue
                if dof == 1:   fx += val
                elif dof == 2: fy += val
                elif dof == 3: fz += val
    return fx, fy, fz


# ── Tests ────────────────────────────────────────────────────────────────────

def test_distributed_force_c3d4_sum_equals_total(tmp_path):
    """Two C3D4 tets sharing a face, +z unit direction, 1000 N total."""
    mesh = _two_tet_mesh_c3d4()
    # Careful: BC face = same face as load would make the load cancel via
    # the fixed DOF — use a different NSET-only fix. Here we fix the shared
    # face to simplify, but load still applies in INP CLOAD accounting.
    mesh.faces.append(FaceGroup(
        face_id=99,
        element_faces=[[1, 0], [2, 0]],
        node_ids=[1, 2, 3]))
    load = Load(type=LoadType.DISTRIBUTED_FORCE,
                face_id=99,
                magnitude=1000.0,
                direction=[0.0, 0.0, 1.0])
    inp_path = tmp_path / "c3d4.inp"
    InpWriterV2(_model(mesh, load)).write(str(inp_path))
    text = inp_path.read_text()

    fx, fy, fz = _cload_vec_sum(text)
    assert abs(fx) < 1e-6
    assert abs(fy) < 1e-6
    # Two element-faces both contribute — writer dedupes to single face area A=0.5
    # but each element-face contributes traction·A_ef independently. With shared
    # corners the per-node accumulations simply double. So total along +z = 2000.
    # (The writer does NOT deduplicate shared faces — this matches CalculiX
    # semantics where both element-faces exist on the boundary.)
    # Corrected expectation: DISTRIBUTED_FORCE uses Σ element-face area = 2×0.5=1.0.
    # Traction = 1000/1.0 = 1000 Pa, lumping per face = 1000·0.5/3 = 166.67 to each
    # of 3 corners, from two faces → 1000 N total along +z.
    assert fz == pytest.approx(1000.0, rel=1e-6)


def test_distributed_force_c3d10_sum_equals_total(tmp_path):
    """Single C3D10, 5000 N at 45° in xz, total face area = 0.5."""
    mesh = _c3d10_face_mesh()
    load = Load(type=LoadType.DISTRIBUTED_FORCE,
                face_id=77,
                magnitude=5000.0,
                direction=[1.0, 0.0, 1.0])  # not normalized; writer normalizes
    inp_path = tmp_path / "c3d10.inp"
    InpWriterV2(_model(mesh, load)).write(str(inp_path))
    text = inp_path.read_text()

    fx, fy, fz = _cload_vec_sum(text)
    ux = 1.0 / math.sqrt(2.0)
    uz = 1.0 / math.sqrt(2.0)
    assert fx == pytest.approx(5000.0 * ux, rel=1e-6)
    assert abs(fy) < 1e-6
    assert fz == pytest.approx(5000.0 * uz, rel=1e-6)


def test_distributed_force_empty_face_rejected(tmp_path):
    """No element-faces → validation must raise ValueError before write."""
    mesh = MeshData(
        nodes=[Node(1, 0, 0, 0), Node(2, 1, 0, 0)],
        elements=[Element(1, "C3D4", [1, 2, 1, 2])],  # degenerate — doesn't matter
        faces=[FaceGroup(face_id=1, element_faces=[], node_ids=[1, 2])],
    )
    load = Load(type=LoadType.DISTRIBUTED_FORCE, face_id=1,
                magnitude=100.0, direction=[1.0, 0.0, 0.0])
    bad_model = FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[BoundaryCondition(BcType.FIXED, 1, [1, 2])],
        loads=[load],
        work_dir="/tmp",
    )
    with pytest.raises(ValueError, match="element-faces"):
        InpWriterV2(bad_model).write(str(tmp_path / "bad.inp"))


def test_distributed_force_zero_magnitude_is_noop(tmp_path):
    mesh = _c3d10_face_mesh()
    load = Load(type=LoadType.DISTRIBUTED_FORCE, face_id=77,
                magnitude=0.0, direction=[0.0, 0.0, 1.0])
    inp_path = tmp_path / "zero.inp"
    InpWriterV2(_model(mesh, load)).write(str(inp_path))
    text = inp_path.read_text()
    # No CLOAD block (or empty)
    fx, fy, fz = _cload_vec_sum(text)
    assert fx == 0.0 and fy == 0.0 and fz == 0.0
