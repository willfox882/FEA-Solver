"""
test_models.py
--------------
Tests for utils/models.py — no Gmsh required.
"""
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))

import math
import pytest
from utils.models import (
    Node, Element, FaceGroup, MeshData, BoundingBox, MeshStats,
    MaterialModel, BcType, BoundaryCondition,
    LoadType, LocationMode, LocalizedLoadData, Load,
    ResultField, ResultSet, FEAModel,
)


# ── Fixtures ──────────────────────────────────────────────────────────────────

def tet4_mesh() -> MeshData:
    """Minimal single-tet mesh (C3D4)."""
    nodes = [
        Node(1, 0.0, 0.0, 0.0),
        Node(2, 1.0, 0.0, 0.0),
        Node(3, 0.0, 1.0, 0.0),
        Node(4, 0.0, 0.0, 1.0),
    ]
    elements = [Element(1, "C3D4", [1, 2, 3, 4])]
    faces = [
        FaceGroup(1, [[1, 0]], [1, 2, 3]),   # base face
        FaceGroup(2, [[1, 3]], [1, 3, 4]),   # side face
    ]
    return MeshData(nodes=nodes, elements=elements, faces=faces)


# ── Node tests ────────────────────────────────────────────────────────────────

def test_node_coords():
    n = Node(1, 1.0, 2.0, 3.0)
    assert n.coords() == (1.0, 2.0, 3.0)

def test_node_dist():
    a = Node(1, 0.0, 0.0, 0.0)
    b = Node(2, 3.0, 4.0, 0.0)
    assert a.dist_to(b) == pytest.approx(5.0)


# ── Element tests ─────────────────────────────────────────────────────────────

def test_element_c3d10_quadratic():
    e = Element(1, "C3D10", list(range(1, 11)))
    assert e.is_quadratic
    assert e.n_nodes == 10

def test_element_c3d4_linear():
    e = Element(1, "C3D4", [1, 2, 3, 4])
    assert not e.is_quadratic
    assert e.n_nodes == 4


# ── MeshData tests ────────────────────────────────────────────────────────────

def test_mesh_bounding_box():
    mesh = tet4_mesh()
    bb = mesh.bounding_box()
    assert bb.xmin == pytest.approx(0.0)
    assert bb.xmax == pytest.approx(1.0)
    assert bb.ymax == pytest.approx(1.0)
    assert bb.zmax == pytest.approx(1.0)

def test_mesh_bounding_box_diagonal():
    mesh = tet4_mesh()
    bb = mesh.bounding_box()
    assert bb.diagonal == pytest.approx(math.sqrt(3.0))

def test_mesh_bounding_box_center():
    mesh = tet4_mesh()
    cx, cy, cz = mesh.bounding_box().center
    assert cx == pytest.approx(0.5)

def test_mesh_stats():
    mesh = tet4_mesh()
    stats = mesh.stats()
    assert stats.n_nodes == 4
    assert stats.n_elements == 1
    assert stats.n_faces == 2
    assert stats.elements_by_type == {"C3D4": 1}

def test_mesh_lookup_node():
    mesh = tet4_mesh()
    n = mesh.node(3)
    assert n.x == 0.0
    assert n.y == 1.0

def test_mesh_lookup_face():
    mesh = tet4_mesh()
    f = mesh.face(2)
    assert f.node_ids == [1, 3, 4]

def test_mesh_nodes_on_face():
    mesh = tet4_mesh()
    nodes = mesh.nodes_on_face(1)
    assert len(nodes) == 3
    assert all(isinstance(n, Node) for n in nodes)

def test_mesh_nearest_node_on_face_exact():
    mesh = tet4_mesh()
    # Target = exact position of node 2 (1.0, 0.0, 0.0)
    nearest = mesh.nearest_node_on_face(1, (1.0, 0.0, 0.0))
    assert nearest.id == 2

def test_mesh_nearest_node_on_face_approx():
    mesh = tet4_mesh()
    # Target close to node 3 (0.0, 1.0, 0.0)
    nearest = mesh.nearest_node_on_face(1, (0.1, 0.9, 0.0))
    assert nearest.id == 3


# ── Material tests ────────────────────────────────────────────────────────────

def test_material_steel_defaults():
    m = MaterialModel.steel()
    assert m.youngs_modulus == pytest.approx(200e9)
    assert m.poissons_ratio == pytest.approx(0.3)

def test_material_aluminium():
    m = MaterialModel.aluminium()
    assert m.youngs_modulus == pytest.approx(70e9)


# ── BcType tests ──────────────────────────────────────────────────────────────

def test_bc_fixed_dofs():
    assert BcType.FIXED.constrained_dofs() == [1, 2, 3]

def test_bc_roller_x_dof():
    assert BcType.ROLLER_X.constrained_dofs() == [1]


# ── FEAModel validation ───────────────────────────────────────────────────────

def test_fea_model_valid():
    mesh = tet4_mesh()
    model = FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[
            BoundaryCondition(BcType.FIXED, face_id=1, node_ids=[1, 2, 3])
        ],
        loads=[
            Load(LoadType.PRESSURE, face_id=2, magnitude=1e6)
        ],
        work_dir="/tmp",
    )
    errors = model.validate()
    assert errors == []

def test_fea_model_no_bcs_error():
    mesh = tet4_mesh()
    model = FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[],
        loads=[Load(LoadType.PRESSURE, face_id=2, magnitude=1e6)],
        work_dir="/tmp",
    )
    errors = model.validate()
    assert any("boundary" in e.lower() for e in errors)

def test_fea_model_invalid_poisson():
    mesh = tet4_mesh()
    model = FEAModel(
        mesh=mesh,
        material=MaterialModel("Bad", 200e9, 0.6),
        boundary_conditions=[BoundaryCondition(BcType.FIXED, 1, [1,2,3])],
        loads=[Load(LoadType.PRESSURE, 2, 1e6)],
        work_dir="/tmp",
    )
    errors = model.validate()
    assert any("poisson" in e.lower() for e in errors)

@pytest.mark.parametrize("nu", [0.0, -0.3, -0.99, 0.49])
def test_fea_model_poisson_valid_range(nu):
    """v=0, auxetic (v<0), and near-incompressible are all physically valid."""
    mesh = tet4_mesh()
    model = FEAModel(
        mesh=mesh,
        material=MaterialModel("Test", 200e9, nu),
        boundary_conditions=[BoundaryCondition(BcType.FIXED, 1, [1, 2, 3])],
        loads=[Load(LoadType.PRESSURE, 2, 1e6)],
        work_dir="/tmp",
    )
    assert not any("poisson" in e.lower() for e in model.validate())

@pytest.mark.parametrize("nu", [0.5, 0.6, -1.0, -1.5])
def test_fea_model_poisson_out_of_range(nu):
    """v>=0.5 (incompressible/invalid) and v<=-1 (degenerate) are rejected."""
    mesh = tet4_mesh()
    model = FEAModel(
        mesh=mesh,
        material=MaterialModel("Bad", 200e9, nu),
        boundary_conditions=[BoundaryCondition(BcType.FIXED, 1, [1, 2, 3])],
        loads=[Load(LoadType.PRESSURE, 2, 1e6)],
        work_dir="/tmp",
    )
    assert any("poisson" in e.lower() for e in model.validate())

def test_fea_model_invalid_face_id():
    mesh = tet4_mesh()
    model = FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[BoundaryCondition(BcType.FIXED, face_id=99, node_ids=[])],
        loads=[Load(LoadType.PRESSURE, face_id=2, magnitude=1e6)],
        work_dir="/tmp",
    )
    errors = model.validate()
    assert any("99" in e for e in errors)


# ── ResultField tests ─────────────────────────────────────────────────────────

def test_result_field_at_node():
    rf = ResultField(node_ids=[1, 2, 3], values=[10.0, 20.0, 30.0])
    assert rf.at_node(2) == pytest.approx(20.0)
    assert rf.at_node(99) is None

def test_result_field_min_max():
    rf = ResultField(node_ids=[1, 2, 3], values=[5.0, 15.0, 3.0])
    assert rf.min == pytest.approx(3.0)
    assert rf.max == pytest.approx(15.0)

def test_result_set_max_displacement():
    rs = ResultSet(
        step=1,
        displacement_mag=ResultField([1, 2], [0.001, 0.002]),
        vonmises=ResultField([1, 2], [1e6, 2e6]),
    )
    assert rs.max_displacement_mm() == pytest.approx(2.0)
    assert rs.max_vonmises_mpa() == pytest.approx(2.0)
