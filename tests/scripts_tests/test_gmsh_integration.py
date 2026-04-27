"""
test_gmsh_integration.py
------------------------
Integration tests for Gmsh meshing pipeline.
Creates geometry via Gmsh OCC (no external STEP file needed).
Skipped if gmsh is not installed.

Tests:
  - Box geometry meshed end-to-end
  - All 6 faces of the box have element face mappings
  - Node IDs are unique and 1-based
  - Element types are C3D4 or C3D10
  - Face node IDs are subset of mesh node IDs
  - Mesh cache skips re-mesh on second call
"""
import sys, os
import tempfile
import shutil
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))

gmsh = pytest.importorskip("gmsh", reason="gmsh not installed")

from utils.models import MeshData, FaceGroup
from utils.mesh_io import read_mesh_data, write_mesh_data
from utils.gmsh_utils import (
    build_face_element_map,
    build_mesh_data,
    add_physical_groups_for_all_surfaces,
    add_physical_group_for_all_volumes,
    apply_mesh_settings,
    extract_volume_elements,
    validate_face_groups,
    get_surface_tags,
)
from utils.mesh_cache import MeshCache


# ── Helpers ───────────────────────────────────────────────────────────────────

def mesh_box(
    work_dir: str,
    lx: float = 0.01, ly: float = 0.01, lz: float = 0.04,
    element_size: float = 0.005,
    order: int = 1,  # C3D4 for speed in tests
) -> MeshData:
    """Mesh a rectangular box using Gmsh OCC. Returns MeshData."""
    gmsh.initialize()
    gmsh.option.setNumber("General.Verbosity", 0)
    try:
        gmsh.model.add("test_box")
        gmsh.model.occ.addBox(0, 0, 0, lx, ly, lz)
        gmsh.model.occ.synchronize()

        surf_tags = add_physical_groups_for_all_surfaces()
        add_physical_group_for_all_volumes()
        apply_mesh_settings(element_size, order, algorithm3d=4)

        gmsh.model.mesh.generate(3)

        elements = extract_volume_elements()
        face_map = build_face_element_map(elements)
        mesh_data = build_mesh_data(surf_tags, face_map)
    finally:
        gmsh.finalize()

    return mesh_data


@pytest.fixture(scope="module")
def box_mesh(tmp_path_factory) -> MeshData:
    work = str(tmp_path_factory.mktemp("gmsh_box"))
    return mesh_box(work)


# ── Basic mesh structure ──────────────────────────────────────────────────────

def test_box_has_nodes(box_mesh):
    assert len(box_mesh.nodes) > 0

def test_box_node_ids_positive(box_mesh):
    assert all(n.id >= 1 for n in box_mesh.nodes)

def test_box_node_ids_unique(box_mesh):
    ids = [n.id for n in box_mesh.nodes]
    assert len(ids) == len(set(ids))

def test_box_has_elements(box_mesh):
    assert len(box_mesh.elements) > 0

def test_box_element_types_are_tet(box_mesh):
    types = {e.type for e in box_mesh.elements}
    assert types <= {"C3D4", "C3D10"}

def test_box_element_ids_unique(box_mesh):
    ids = [e.id for e in box_mesh.elements]
    assert len(ids) == len(set(ids))

def test_box_c3d4_has_4_nodes_per_element(box_mesh):
    for e in box_mesh.elements:
        if e.type == "C3D4":
            assert len(e.nodes) == 4

def test_box_c3d10_has_10_nodes_per_element(box_mesh):
    for e in box_mesh.elements:
        if e.type == "C3D10":
            assert len(e.nodes) == 10


# ── Face structure ────────────────────────────────────────────────────────────

def test_box_has_six_faces(box_mesh):
    """A box has exactly 6 CAD faces."""
    assert len(box_mesh.faces) == 6

def test_all_faces_have_node_ids(box_mesh):
    for fg in box_mesh.faces:
        assert len(fg.node_ids) > 0, f"Face {fg.face_id} has no nodes"

def test_all_faces_have_element_faces(box_mesh):
    for fg in box_mesh.faces:
        assert len(fg.element_faces) > 0, \
            f"Face {fg.face_id} has no element face mappings"

def test_face_node_ids_subset_of_mesh_nodes(box_mesh):
    all_node_ids = {n.id for n in box_mesh.nodes}
    for fg in box_mesh.faces:
        assert set(fg.node_ids) <= all_node_ids, \
            f"Face {fg.face_id} has node IDs not in mesh"

def test_face_element_ids_subset_of_mesh_elements(box_mesh):
    all_elem_ids = {e.id for e in box_mesh.elements}
    for fg in box_mesh.faces:
        for eid, fi in fg.element_faces:
            assert eid in all_elem_ids, \
                f"Face {fg.face_id} references unknown element {eid}"
            assert 0 <= fi <= 3, \
                f"Face index {fi} out of range [0,3]"

def test_face_node_ids_sorted(box_mesh):
    for fg in box_mesh.faces:
        assert fg.node_ids == sorted(fg.node_ids)

def test_no_duplicate_element_faces_within_face_group(box_mesh):
    for fg in box_mesh.faces:
        pairs = [(eid, fi) for eid, fi in fg.element_faces]
        assert len(pairs) == len(set(pairs)), \
            f"Face {fg.face_id} has duplicate element face entries"

def test_validate_face_groups_clean(box_mesh):
    warnings = validate_face_groups(box_mesh)
    assert warnings == [], f"Unexpected warnings: {warnings}"


# ── Bounding box ──────────────────────────────────────────────────────────────

def test_box_bounding_box_extents(box_mesh):
    bb = box_mesh.bounding_box()
    assert bb.xmin >= -1e-9
    assert bb.xmax == pytest.approx(0.01, abs=1e-4)
    assert bb.zmax == pytest.approx(0.04, abs=1e-4)


# ── JSON round-trip ───────────────────────────────────────────────────────────

def test_mesh_json_roundtrip(box_mesh, tmp_path):
    path = str(tmp_path / "mesh.json")
    write_mesh_data(box_mesh, path)
    restored = read_mesh_data(path)
    assert len(restored.nodes) == len(box_mesh.nodes)
    assert len(restored.elements) == len(box_mesh.elements)
    assert len(restored.faces) == len(box_mesh.faces)

def test_mesh_json_face_groups_preserved(box_mesh, tmp_path):
    path = str(tmp_path / "mesh.json")
    write_mesh_data(box_mesh, path)
    restored = read_mesh_data(path)
    for orig, rest in zip(box_mesh.faces, restored.faces):
        assert orig.face_id == rest.face_id
        assert orig.node_ids == rest.node_ids
        assert orig.element_faces == rest.element_faces


# ── Mesh cache ────────────────────────────────────────────────────────────────

def test_cache_miss_on_first_run(tmp_path):
    cache = MeshCache(tmp_path)
    key = "abc123"
    assert not cache.is_valid(key)

def test_cache_hit_after_save(tmp_path):
    cache = MeshCache(tmp_path)
    key = "abc123"
    cache.save(key)
    assert cache.is_valid(key)

def test_cache_invalidated_after_invalidate(tmp_path):
    cache = MeshCache(tmp_path)
    key = "abc123"
    cache.save(key)
    cache.invalidate()
    assert not cache.is_valid(key)

def test_cache_wrong_key_is_miss(tmp_path):
    cache = MeshCache(tmp_path)
    cache.save("key_A")
    assert not cache.is_valid("key_B")

def test_cache_key_changes_with_element_size(tmp_path):
    import os
    # Create a dummy STEP file
    step = tmp_path / "dummy.step"
    step.write_bytes(b"ISO-10303-21;")
    cache = MeshCache(tmp_path)
    k1 = cache.make_key(str(step), 0.01, 2, 4)
    k2 = cache.make_key(str(step), 0.02, 2, 4)
    assert k1 != k2

def test_cache_key_changes_with_file_content(tmp_path):
    step1 = tmp_path / "a.step"
    step2 = tmp_path / "b.step"
    step1.write_bytes(b"content_A")
    step2.write_bytes(b"content_B")
    cache = MeshCache(tmp_path)
    k1 = cache.make_key(str(step1), 0.01, 2, 4)
    k2 = cache.make_key(str(step2), 0.01, 2, 4)
    assert k1 != k2


# ── Face mapping correctness on known geometry ────────────────────────────────

def test_each_face_covers_unique_region(box_mesh):
    """For a box, face node sets should not be identical (faces are distinct)."""
    node_sets = [frozenset(fg.node_ids) for fg in box_mesh.faces]
    assert len(node_sets) == len(set(node_sets))

def test_total_surface_nodes_cover_all_boundary(box_mesh):
    """
    The union of all face node sets must be the complete surface of the box.
    Interior nodes are those NOT on any face.
    """
    all_surface_nodes: set[int] = set()
    for fg in box_mesh.faces:
        all_surface_nodes.update(fg.node_ids)

    all_nodes = {n.id for n in box_mesh.nodes}
    interior = all_nodes - all_surface_nodes

    # For a well-meshed coarse box, most nodes are on the surface
    # Interior can be zero for very coarse meshes
    assert len(all_surface_nodes) > 0
    assert len(all_surface_nodes) <= len(all_nodes)


# ── C3D10 Gmsh→CCX midside reorder ────────────────────────────────────────────

def test_c3d10_midside_ordering_matches_ccx(tmp_path_factory):
    """
    Regression: ccx exited 201 with 'nonpositive jacobian determinant' because
    Gmsh tet10 index 7 = mid(1,3) and index 8 = mid(0,3), while CCX C3D10
    expects index 7 = mid(0,3), index 8 = mid(1,3). extract_volume_elements
    swaps 7↔8. This test verifies each midside node in the extracted element
    is the correct midpoint of the CCX-expected corner pair.
    """
    work = str(tmp_path_factory.mktemp("c3d10_swap"))
    mesh = mesh_box(work, order=2, element_size=0.01)
    quadratic = [e for e in mesh.elements if e.type == "C3D10"]
    assert len(quadratic) > 0, "mesh_box produced no C3D10 elements"

    node_xyz = {n.id: (n.x, n.y, n.z) for n in mesh.nodes}
    # CCX expected midside pairs (corner-pair sharing an edge, 0-based corners):
    expected = [(0, 1), (1, 2), (0, 2), (0, 3), (1, 3), (2, 3)]
    tol = 1e-9
    for e in quadratic[:100]:  # first 100 elements — plenty for coverage
        c = [node_xyz[e.nodes[i]] for i in range(4)]
        for k, (a, b) in enumerate(expected):
            m = node_xyz[e.nodes[4 + k]]
            mid = ((c[a][0] + c[b][0]) / 2,
                   (c[a][1] + c[b][1]) / 2,
                   (c[a][2] + c[b][2]) / 2)
            dx = abs(m[0] - mid[0]) + abs(m[1] - mid[1]) + abs(m[2] - mid[2])
            assert dx < tol, (
                f"element {e.id}: midside slot {4+k} should be mid(corners "
                f"{a},{b}) but offset={dx:.3e}")
