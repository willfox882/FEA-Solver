"""
test_pipeline.py
----------------
End-to-end pipeline tests without CalculiX.
- FEAModel → InpWriterV2 → .inp (structural check)
- Mock .frd → FrdReader → ResultSet structure check
- JSON round-trip: FEAModel serialize/deserialize
"""
import sys, os, json, math
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))

from utils.models import (
    Node, Element, FaceGroup, MeshData,
    FEAModel, MaterialModel, BoundaryCondition, BcType,
    Load, LoadType,
)
from utils.mesh_io import fea_model_to_json, fea_model_from_json
from utils.inp_writer import InpWriterV2
from utils.frd_reader import FrdReader


# ── Minimal synthetic mesh (no Gmsh) ─────────────────────────────────────────

def _make_tet_mesh() -> MeshData:
    """Single C3D4 tetrahedron with 2 face groups."""
    nodes = [
        Node(1, 0.0, 0.0, 0.0),
        Node(2, 1.0, 0.0, 0.0),
        Node(3, 0.0, 1.0, 0.0),
        Node(4, 0.0, 0.0, 1.0),
    ]
    elements = [Element(1, "C3D4", [1, 2, 3, 4])]
    faces = [
        FaceGroup(face_id=1, element_faces=[[1, 0]], node_ids=[1, 2, 3]),
        FaceGroup(face_id=2, element_faces=[[1, 3]], node_ids=[1, 3, 4]),
    ]
    return MeshData(nodes=nodes, elements=elements, faces=faces)


@pytest.fixture
def tet_model(tmp_path):
    mesh = _make_tet_mesh()
    return FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[
            BoundaryCondition(BcType.FIXED, face_id=1, node_ids=[1, 2, 3]),
        ],
        loads=[
            Load(LoadType.PRESSURE, face_id=2, magnitude=1e6),
        ],
        work_dir=str(tmp_path),
    )


# ── INP generation ─────────────────────────────────────────────────────────────

@pytest.fixture
def inp_path(tet_model, tmp_path):
    path = str(tmp_path / "model.inp")
    InpWriterV2(tet_model).write(path)
    return path


def test_inp_file_created(inp_path):
    assert os.path.exists(inp_path)


def test_inp_required_sections(inp_path):
    content = open(inp_path).read()
    for section in ["*HEADING", "*NODE", "*ELEMENT", "*MATERIAL",
                    "*ELASTIC", "*SOLID SECTION", "*STEP", "*STATIC",
                    "*NSET", "*BOUNDARY", "*DLOAD", "*END STEP"]:
        assert section in content, f"Missing section: {section}"


def test_inp_fixed_bc_writes_all_three_dofs(inp_path):
    content = open(inp_path).read()
    # CalculiX FIXED BC: NSET, first_dof, last_dof (zero displacement is implied)
    assert "FIXED, 1, 3" in content


def test_inp_pressure_written(inp_path):
    content = open(inp_path).read()
    assert "1.000000e+06" in content


def test_inp_node_count(inp_path):
    lines = open(inp_path).readlines()
    in_node = False
    count = 0
    for ln in lines:
        stripped = ln.strip()
        if stripped == "*NODE, NSET=NALL":
            in_node = True
            continue
        if in_node:
            if stripped.startswith("*") or not stripped:
                in_node = False
                continue
            count += 1
    assert count == 4


def test_inp_element_count(inp_path):
    lines = open(inp_path).readlines()
    in_elem = False
    count = 0
    for ln in lines:
        if ln.strip().startswith("*ELEMENT"):
            in_elem = True
            continue
        if in_elem:
            if ln.strip().startswith("*") or not ln.strip():
                in_elem = False
                continue
            count += 1
    assert count == 1


# ── FEAModel JSON round-trip ───────────────────────────────────────────────────

def test_fea_model_json_roundtrip(tet_model):
    serialized = fea_model_to_json(tet_model)
    restored = fea_model_from_json(serialized)
    assert len(restored.mesh.nodes) == len(tet_model.mesh.nodes)
    assert len(restored.mesh.elements) == len(tet_model.mesh.elements)
    assert len(restored.mesh.faces) == len(tet_model.mesh.faces)
    assert len(restored.boundary_conditions) == len(tet_model.boundary_conditions)
    assert len(restored.loads) == len(tet_model.loads)


def test_fea_model_roundtrip_bc_type(tet_model):
    restored = fea_model_from_json(fea_model_to_json(tet_model))
    assert restored.boundary_conditions[0].type == BcType.FIXED


def test_fea_model_roundtrip_bc_node_ids(tet_model):
    restored = fea_model_from_json(fea_model_to_json(tet_model))
    assert restored.boundary_conditions[0].node_ids == [1, 2, 3]


def test_fea_model_roundtrip_load_type(tet_model):
    restored = fea_model_from_json(fea_model_to_json(tet_model))
    assert restored.loads[0].type == LoadType.PRESSURE


def test_fea_model_roundtrip_material(tet_model):
    restored = fea_model_from_json(fea_model_to_json(tet_model))
    assert restored.material.youngs_modulus == pytest.approx(2e11)
    assert restored.material.poissons_ratio == pytest.approx(0.3)


def test_fea_model_validate_clean(tet_model):
    errors = tet_model.validate()
    assert errors == []


# ── FrdReader with mock .frd ───────────────────────────────────────────────────

MOCK_FRD = """\
    1CSET        DISP           4    1PSTEP         1           1
 -4  DISP        4    1
 -5  D1          1    2    1    0
 -5  D2          1    2    2    0
 -5  D3          1    2    3    0
 -5  ALL         1    2    0    0    1    5
 -1         1  0.00000E+00  1.00000E-03 -5.00000E-04
 -1         2  0.00000E+00  2.00000E-03 -1.00000E-03
 -1         3  0.00000E+00  3.00000E-03 -1.50000E-03
 -1         4  0.00000E+00  4.00000E-03 -2.00000E-03
 -3
 -4  STRESS      6    1
 -5  S11         1    4    1    1
 -5  S22         1    4    2    2
 -5  S33         1    4    3    3
 -5  S12         1    4    4    12
 -5  S23         1    4    5    23
 -5  S13         1    4    6    13
 -1         1  1.00000E+06  5.00000E+05  2.00000E+05  1.00000E+05  0.00000E+00  0.00000E+00
 -1         2  1.20000E+06  6.00000E+05  2.40000E+05  1.20000E+05  0.00000E+00  0.00000E+00
 -1         3  9.00000E+05  4.50000E+05  1.80000E+05  9.00000E+04  0.00000E+00  0.00000E+00
 -1         4  8.00000E+05  4.00000E+05  1.60000E+05  8.00000E+04  0.00000E+00  0.00000E+00
 -3
 9999
"""


@pytest.fixture
def mock_frd(tmp_path):
    path = tmp_path / "model.frd"
    path.write_text(MOCK_FRD)
    return str(path)


def test_frd_reader_parses_displacements(mock_frd):
    result = FrdReader(mock_frd).parse()
    assert len(result["displacement_x"]["values"]) == 4
    assert len(result["displacement_y"]["values"]) == 4
    assert len(result["displacement_z"]["values"]) == 4


def test_frd_reader_disp_values(mock_frd):
    result = FrdReader(mock_frd).parse()
    assert result["displacement_y"]["values"][0] == pytest.approx(1e-3)
    assert result["displacement_z"]["values"][0] == pytest.approx(-5e-4)


def test_frd_reader_node_ids(mock_frd):
    result = FrdReader(mock_frd).parse()
    assert result["displacement_x"]["node_ids"] == [1, 2, 3, 4]


def test_frd_reader_displacement_mag(mock_frd):
    result = FrdReader(mock_frd).parse()
    mags = result["displacement_mag"]["values"]
    assert len(mags) == 4
    # Node 1: sqrt(0 + 1e-3^2 + 5e-4^2)
    expected = math.sqrt(0 + 1e-3**2 + 5e-4**2)
    assert mags[0] == pytest.approx(expected, rel=1e-5)


def test_frd_reader_vonmises(mock_frd):
    result = FrdReader(mock_frd).parse()
    vm = result["vonmises"]["values"]
    assert len(vm) == 4
    assert all(v > 0 for v in vm)


def test_frd_reader_vonmises_node1(mock_frd):
    """Hand-calc von Mises for node 1 stress state."""
    import math
    s11, s22, s33 = 1e6, 5e5, 2e5
    s12, s23, s13 = 1e5, 0.0, 0.0
    expected = math.sqrt(0.5 * (
        (s11 - s22)**2 + (s22 - s33)**2 + (s33 - s11)**2 +
        6 * (s12**2 + s23**2 + s13**2)
    ))
    result = FrdReader(mock_frd).parse()
    assert result["vonmises"]["values"][0] == pytest.approx(expected, rel=1e-4)
