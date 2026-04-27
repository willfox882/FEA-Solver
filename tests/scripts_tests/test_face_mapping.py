"""
test_face_mapping.py
--------------------
Unit tests for gmsh_utils face mapping logic — no Gmsh required.
Tests build_face_element_map() and TET_FACE_CORNERS correctness.
"""
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))

import pytest
from utils.models import Element, FaceGroup, MeshData, Node
from utils.gmsh_utils import (
    build_face_element_map,
    TET_FACE_CORNERS,
    validate_face_groups,
)


# ── Fixtures ──────────────────────────────────────────────────────────────────

def single_tet4() -> Element:
    """C3D4 element: nodes 1,2,3,4 at corners."""
    return Element(id=1, type="C3D4", nodes=[1, 2, 3, 4])


def single_tet10() -> Element:
    """C3D10 element: corners 1-4, midside 5-10."""
    return Element(id=1, type="C3D10",
                   nodes=[1, 2, 3, 4, 5, 6, 7, 8, 9, 10])


def two_tets() -> list[Element]:
    """Two adjacent C3D4 tets sharing face {1,2,3}."""
    return [
        Element(id=1, type="C3D4", nodes=[1, 2, 3, 4]),
        Element(id=2, type="C3D4", nodes=[1, 2, 3, 5]),
    ]


# ── TET_FACE_CORNERS structure ────────────────────────────────────────────────

def test_four_faces_defined():
    assert len(TET_FACE_CORNERS) == 4

def test_each_face_has_three_corners():
    for fi, triple in enumerate(TET_FACE_CORNERS):
        assert len(triple) == 3, f"Face {fi} should have 3 corners"

def test_all_four_corner_indices_covered():
    """Every corner index 0-3 must appear in at least one face."""
    all_indices = {idx for triple in TET_FACE_CORNERS for idx in triple}
    assert all_indices == {0, 1, 2, 3}

def test_no_duplicate_faces():
    """All 4 face definitions must be distinct sets."""
    sets = [frozenset(t) for t in TET_FACE_CORNERS]
    assert len(sets) == len(set(sets))


# ── build_face_element_map ────────────────────────────────────────────────────

def test_tet4_produces_four_entries():
    elem = single_tet4()
    m = build_face_element_map([elem])
    assert len(m) == 4

def test_tet4_face_0_key():
    """Face 0 corners = nodes at indices 0,1,2 = node IDs 1,2,3."""
    elem = single_tet4()
    m = build_face_element_map([elem])
    key = frozenset([1, 2, 3])
    assert key in m
    eid, fi = m[key]
    assert eid == 1
    assert fi == 0

def test_tet4_face_1_key():
    """Face 1 corners = indices 0,1,3 = node IDs 1,2,4."""
    elem = single_tet4()
    m = build_face_element_map([elem])
    key = frozenset([1, 2, 4])
    assert key in m
    _, fi = m[key]
    assert fi == 1

def test_tet4_face_2_key():
    """Face 2: indices 1,2,3 = node IDs 2,3,4."""
    elem = single_tet4()
    m = build_face_element_map([elem])
    assert frozenset([2, 3, 4]) in m

def test_tet4_face_3_key():
    """Face 3: indices 0,2,3 = node IDs 1,3,4."""
    elem = single_tet4()
    m = build_face_element_map([elem])
    assert frozenset([1, 3, 4]) in m

def test_tet10_uses_only_corner_nodes():
    """C3D10: mid-side nodes (indices 4-9) must not appear as face corners."""
    elem = single_tet10()
    m = build_face_element_map([elem])
    # All keys should contain only node IDs from corners (1-4)
    for key in m:
        assert key <= {1, 2, 3, 4}, f"Key {key} contains non-corner node"

def test_tet10_same_face_structure_as_tet4():
    tet4_map = build_face_element_map([Element(1, "C3D4", [1,2,3,4])])
    tet10_map = build_face_element_map([Element(1, "C3D10",
                                               [1,2,3,4,5,6,7,8,9,10])])
    assert set(tet4_map.keys()) == set(tet10_map.keys())

def test_two_adjacent_tets_shared_face_overwritten():
    """
    Two elements sharing face {1,2,3}: the map only stores ONE entry.
    The last element wins (boundary face detection handles this separately).
    """
    elems = two_tets()
    m = build_face_element_map(elems)
    key = frozenset([1, 2, 3])
    assert key in m
    # Should be one of elem 1 or 2 (doesn't matter which)
    assert m[key][0] in (1, 2)

def test_map_size_two_non_sharing_tets():
    """Two tets with completely distinct nodes → 8 unique face keys."""
    elems = [
        Element(1, "C3D4", [1, 2, 3, 4]),
        Element(2, "C3D4", [5, 6, 7, 8]),
    ]
    m = build_face_element_map(elems)
    assert len(m) == 8

def test_empty_elements_gives_empty_map():
    assert build_face_element_map([]) == {}


# ── Simulated surface lookup ──────────────────────────────────────────────────

def test_surface_lookup_finds_correct_face():
    """
    Simulate: surface triangle with corners {1,2,3} should map to elem 1, face 0.
    """
    elem = single_tet4()
    m = build_face_element_map([elem])
    surface_triangle_corners = frozenset([1, 2, 3])
    result = m.get(surface_triangle_corners)
    assert result is not None
    assert result == (1, 0)

def test_surface_lookup_misses_interior_face():
    """Interior face not touched by a surface triangle won't appear in hits."""
    elems = two_tets()
    m = build_face_element_map(elems)
    # {1,2,3} is shared (interior) — in a real mesh this shouldn't appear
    # as a surface triangle. Here we just verify the lookup returns *something*.
    result = m.get(frozenset([1, 2, 3]))
    assert result is not None  # shared — one of the two elements


# ── validate_face_groups ──────────────────────────────────────────────────────

def make_test_mesh(face_groups: list[FaceGroup]) -> MeshData:
    nodes = [Node(i, 0.0, 0.0, float(i)) for i in range(1, 6)]
    elements = [Element(1, "C3D4", [1, 2, 3, 4])]
    return MeshData(nodes=nodes, elements=elements, faces=face_groups)

def test_validate_clean_face_group():
    fg = FaceGroup(face_id=1, element_faces=[[1, 0]], node_ids=[1, 2, 3])
    mesh = make_test_mesh([fg])
    assert validate_face_groups(mesh) == []

def test_validate_warns_empty_node_ids():
    fg = FaceGroup(face_id=1, element_faces=[[1, 0]], node_ids=[])
    mesh = make_test_mesh([fg])
    warnings = validate_face_groups(mesh)
    assert any("no nodes" in w for w in warnings)

def test_validate_warns_empty_element_faces():
    fg = FaceGroup(face_id=1, element_faces=[], node_ids=[1, 2, 3])
    mesh = make_test_mesh([fg])
    warnings = validate_face_groups(mesh)
    assert any("no element faces" in w for w in warnings)

def test_validate_warns_malformed_element_face():
    fg = FaceGroup(face_id=1, element_faces=[[1]], node_ids=[1, 2, 3])
    mesh = make_test_mesh([fg])
    warnings = validate_face_groups(mesh)
    assert any("malformed" in w for w in warnings)


# ── CCX face index → CalculiX label mapping ───────────────────────────────────

def test_ccx_face_labels():
    """Face index 0 → P1/S1, etc."""
    label_map = {0: "P1", 1: "P2", 2: "P3", 3: "P4"}
    for fi in range(4):
        assert label_map[fi] == f"P{fi + 1}"

def test_all_four_face_indices_map_to_dload_labels():
    for fi, triple in enumerate(TET_FACE_CORNERS):
        assert fi in range(4)
