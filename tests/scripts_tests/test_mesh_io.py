"""
test_mesh_io.py
---------------
Round-trip serialization tests for mesh_io.py — no Gmsh required.
"""
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))

import json
import pytest
from utils.models import (
    Node, Element, FaceGroup, MeshData, MaterialModel,
    BcType, BoundaryCondition, LoadType, Load,
    LocationMode, LocalizedLoadData, FEAModel,
    ResultField, ResultSet,
)
from utils.mesh_io import (
    mesh_data_to_json, mesh_data_from_json,
    fea_model_to_json, fea_model_from_json,
    result_set_from_json,
    write_mesh_data, read_mesh_data,
)


def make_mesh() -> MeshData:
    return MeshData(
        nodes=[Node(1, 0.0, 0.0, 0.0), Node(2, 1.0, 0.0, 0.0),
               Node(3, 0.0, 1.0, 0.0), Node(4, 0.0, 0.0, 1.0)],
        elements=[Element(1, "C3D4", [1, 2, 3, 4])],
        faces=[FaceGroup(1, [[1, 0]], [1, 2, 3]),
               FaceGroup(2, [[1, 3]], [1, 3, 4])],
    )


def make_model(mesh: MeshData) -> FEAModel:
    return FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[BoundaryCondition(BcType.FIXED, 1, [1, 2, 3])],
        loads=[Load(LoadType.PRESSURE, 2, 5000.0)],
        work_dir="/tmp/test",
    )


# ── MeshData round-trip ───────────────────────────────────────────────────────

def test_mesh_json_schema_keys():
    mesh = make_mesh()
    d = json.loads(mesh_data_to_json(mesh))
    assert set(d.keys()) == {"nodes", "elements", "faces"}
    assert set(d["nodes"][0].keys()) == {"id", "x", "y", "z"}
    assert set(d["elements"][0].keys()) == {"id", "type", "nodes"}
    assert set(d["faces"][0].keys()) == {"face_id", "element_faces", "node_ids"}

def test_mesh_roundtrip_node_count():
    mesh = make_mesh()
    restored = mesh_data_from_json(mesh_data_to_json(mesh))
    assert len(restored.nodes) == 4
    assert len(restored.elements) == 1
    assert len(restored.faces) == 2

def test_mesh_roundtrip_node_values():
    mesh = make_mesh()
    restored = mesh_data_from_json(mesh_data_to_json(mesh))
    n = restored.nodes[1]
    assert n.id == 2
    assert n.x == pytest.approx(1.0)

def test_mesh_roundtrip_face_element_faces():
    mesh = make_mesh()
    restored = mesh_data_from_json(mesh_data_to_json(mesh))
    assert restored.faces[0].element_faces == [[1, 0]]

def test_mesh_file_roundtrip(tmp_path):
    mesh = make_mesh()
    path = str(tmp_path / "mesh.json")
    write_mesh_data(mesh, path)
    restored = read_mesh_data(path)
    assert len(restored.nodes) == 4

def test_mesh_file_is_compact_json(tmp_path):
    mesh = make_mesh()
    path = str(tmp_path / "mesh.json")
    write_mesh_data(mesh, path)
    raw = open(path).read()
    assert "\n" not in raw   # compact (no indent)


# ── FEAModel round-trip ───────────────────────────────────────────────────────

def test_fea_model_schema_keys():
    model = make_model(make_mesh())
    d = json.loads(fea_model_to_json(model))
    assert set(d.keys()) >= {"mesh", "material", "boundary_conditions", "loads", "work_dir"}

def test_fea_model_material_roundtrip():
    model = make_model(make_mesh())
    restored = fea_model_from_json(fea_model_to_json(model))
    assert restored.material.youngs_modulus == pytest.approx(200e9)
    assert restored.material.name == "Steel"

def test_fea_model_bc_type_roundtrip():
    model = make_model(make_mesh())
    restored = fea_model_from_json(fea_model_to_json(model))
    assert restored.boundary_conditions[0].type == BcType.FIXED
    assert restored.boundary_conditions[0].face_id == 1

def test_fea_model_load_type_roundtrip():
    model = make_model(make_mesh())
    restored = fea_model_from_json(fea_model_to_json(model))
    assert restored.loads[0].type == LoadType.PRESSURE
    assert restored.loads[0].magnitude == pytest.approx(5000.0)

def test_fea_model_point_load_roundtrip():
    mesh = make_mesh()
    model = FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[BoundaryCondition(BcType.FIXED, 1, [1, 2, 3])],
        loads=[Load(
            LoadType.POINT_LOAD, 2, 0.0,
            localized=LocalizedLoadData(
                mode=LocationMode.ABSOLUTE_XYZ,
                xyz=[0.0, 0.5, 0.5],
                force=[0.0, 0.0, -1000.0],
            )
        )],
        work_dir="/tmp",
    )
    restored = fea_model_from_json(fea_model_to_json(model))
    loc = restored.loads[0].localized
    assert loc is not None
    assert loc.mode == LocationMode.ABSOLUTE_XYZ
    assert loc.force == pytest.approx([0.0, 0.0, -1000.0])

def test_fea_model_mesh_preserved():
    model = make_model(make_mesh())
    restored = fea_model_from_json(fea_model_to_json(model))
    assert len(restored.mesh.nodes) == 4
    assert len(restored.mesh.faces) == 2


# ── ResultSet round-trip ──────────────────────────────────────────────────────

RESULTS_JSON = json.dumps({
    "step": 1,
    "displacement_x": {"node_ids": [1, 2], "values": [0.0, 1e-4]},
    "displacement_y": {"node_ids": [1, 2], "values": [0.0, 0.0]},
    "displacement_z": {"node_ids": [1, 2], "values": [-5e-5, -1e-4]},
    "displacement_mag": {"node_ids": [1, 2], "values": [5e-5, 1.41e-4]},
    "vonmises": {"node_ids": [1, 2], "values": [0.0, 2e6]},
})

def test_result_set_roundtrip():
    rs = result_set_from_json(RESULTS_JSON)
    assert rs.step == 1
    assert rs.vonmises is not None
    assert rs.vonmises.at_node(2) == pytest.approx(2e6)

def test_result_set_max_vonmises():
    rs = result_set_from_json(RESULTS_JSON)
    assert rs.max_vonmises_mpa() == pytest.approx(2.0)

def test_result_set_max_displacement_mm():
    rs = result_set_from_json(RESULTS_JSON)
    assert rs.max_displacement_mm() == pytest.approx(0.141, rel=1e-2)
