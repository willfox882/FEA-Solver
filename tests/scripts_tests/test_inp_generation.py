"""
test_inp_generation.py
----------------------
Integration: Gmsh box mesh → InpWriterV2 → verify .inp structure.
No CalculiX required.
Skipped if gmsh not installed.
"""
import sys, os
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))

gmsh = pytest.importorskip("gmsh", reason="gmsh not installed")

from utils.models import (
    FEAModel, MaterialModel, BoundaryCondition, BcType, Load, LoadType,
)
from utils.gmsh_utils import (
    add_physical_groups_for_all_surfaces,
    add_physical_group_for_all_volumes,
    apply_mesh_settings,
    extract_volume_elements,
    build_face_element_map,
    build_mesh_data,
)
from utils.inp_writer import InpWriterV2


# ── Fixtures ───────────────────────────────────────────────────────────────────

@pytest.fixture(scope="module")
def box_model(tmp_path_factory):
    """FEAModel built from a Gmsh box: face 1 Fixed, face 2 Pressure 1 MPa."""
    gmsh.initialize()
    gmsh.option.setNumber("General.Verbosity", 0)
    try:
        gmsh.model.add("inp_box")
        gmsh.model.occ.addBox(0, 0, 0, 0.01, 0.01, 0.04)
        gmsh.model.occ.synchronize()
        surf_tags = add_physical_groups_for_all_surfaces()
        add_physical_group_for_all_volumes()
        apply_mesh_settings(element_size=0.005, order=1, algorithm3d=4)
        gmsh.model.mesh.generate(3)
        elements = extract_volume_elements()
        face_map = build_face_element_map(elements)
        mesh = build_mesh_data(surf_tags, face_map)
    finally:
        gmsh.finalize()

    # Pick first two face IDs
    face_ids = [f.face_id for f in mesh.faces]
    bc_face  = face_ids[0]
    load_face = face_ids[1]

    mesh.build_lookups()
    bc_node_ids = mesh.face(bc_face).node_ids

    model = FEAModel(
        mesh=mesh,
        material=MaterialModel.steel(),
        boundary_conditions=[
            BoundaryCondition(type=BcType.FIXED, face_id=bc_face,
                              node_ids=list(bc_node_ids)),
        ],
        loads=[
            Load(type=LoadType.PRESSURE, face_id=load_face, magnitude=1e6),
        ],
        work_dir=str(tmp_path_factory.mktemp("inp_gen")),
    )
    return model


@pytest.fixture(scope="module")
def inp_content(box_model, tmp_path_factory):
    path = str(tmp_path_factory.mktemp("inp_out") / "model.inp")
    InpWriterV2(box_model).write(path)
    return open(path).read()


# ── Required sections ──────────────────────────────────────────────────────────

def test_inp_has_heading(inp_content):
    assert "*HEADING" in inp_content

def test_inp_has_node_section(inp_content):
    assert "*NODE" in inp_content

def test_inp_has_element_section(inp_content):
    assert "*ELEMENT" in inp_content

def test_inp_has_c3d4_type(inp_content):
    assert "C3D4" in inp_content

def test_inp_has_material(inp_content):
    assert "*MATERIAL" in inp_content
    assert "*ELASTIC" in inp_content

def test_inp_has_solid_section(inp_content):
    assert "*SOLID SECTION" in inp_content

def test_inp_has_step(inp_content):
    assert "*STEP" in inp_content
    assert "*STATIC" in inp_content

def test_inp_has_boundary(inp_content):
    assert "*BOUNDARY" in inp_content

def test_inp_fixed_bc_dof_1_to_3(inp_content):
    # CalculiX FIXED BC: NSET, first_dof, last_dof (zero displacement is implied)
    assert "FIXED, 1, 3" in inp_content

def test_inp_has_dload(inp_content):
    assert "*DLOAD" in inp_content

def test_inp_pressure_magnitude(inp_content):
    assert "1.000000e+06" in inp_content

def test_inp_has_node_file(inp_content):
    assert "*NODE FILE" in inp_content

def test_inp_has_el_file(inp_content):
    assert "*EL FILE" in inp_content

def test_inp_ends_with_end_step(inp_content):
    assert "*END STEP" in inp_content

def test_inp_nset_defined(inp_content):
    assert "*NSET" in inp_content

def test_inp_node_ids_are_numeric(inp_content):
    """First data line after *NODE should start with an integer node ID."""
    lines = inp_content.splitlines()
    for i, ln in enumerate(lines):
        if ln.strip() == "*NODE, NSET=NALL":
            first_data = lines[i + 1]
            node_id = int(first_data.split(",")[0].strip())
            assert node_id >= 1
            break

def test_inp_element_ids_are_numeric(inp_content):
    lines = inp_content.splitlines()
    for i, ln in enumerate(lines):
        if ln.strip().startswith("*ELEMENT"):
            first_data = lines[i + 1]
            elem_id = int(first_data.split(",")[0].strip())
            assert elem_id >= 1
            break

def test_inp_material_name_is_steel(inp_content):
    assert "NAME=STEEL" in inp_content

def test_inp_youngs_modulus_in_elastic(inp_content):
    # Steel E = 2e11 Pa
    assert "2.000000e+11" in inp_content

def test_inp_poissons_ratio(inp_content):
    assert "0.3" in inp_content
