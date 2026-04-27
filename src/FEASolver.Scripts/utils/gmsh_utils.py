"""
gmsh_utils.py
-------------
Gmsh API helpers. All public functions assume gmsh is initialized.

Key algorithm: build_face_element_map()
  Maps frozenset(3 corner node IDs) -> (element_id, ccx_face_index 0-3)
  Used to assign volume element faces to CAD surface groups in O(1) per triangle.

CalculiX face numbering for C3D4 / C3D10 (shared corner structure, 0-based):
  Face 0 (S1/P1): corner indices [0,1,2]
  Face 1 (S2/P2): corner indices [0,1,3]
  Face 2 (S3/P3): corner indices [1,2,3]
  Face 3 (S4/P4): corner indices [0,2,3]

For C3D10: corner nodes are at positions 0-3 in the Gmsh node list.
  Mid-side nodes occupy positions 4-9 and are not used for face identification.
"""

from __future__ import annotations
import numpy as np
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from .models import Node, Element, FaceGroup, MeshData

# Corner node indices (0-based) for each tet face, same for C3D4 and C3D10
TET_FACE_CORNERS: list[tuple[int, int, int]] = [
    (0, 1, 2),   # Face 0 → CalculiX S1 / P1
    (0, 1, 3),   # Face 1 → CalculiX S2 / P2
    (1, 2, 3),   # Face 2 → CalculiX S3 / P3
    (0, 2, 3),   # Face 3 → CalculiX S4 / P4
]

# Gmsh element type codes
GMSH_TET4  = 4
GMSH_TET10 = 11
GMSH_TRI3  = 2
GMSH_TRI6  = 9

CCX_TYPE_NAME = {GMSH_TET4: "C3D4", GMSH_TET10: "C3D10"}
NODES_PER_ELEM = {GMSH_TET4: 4, GMSH_TET10: 10, GMSH_TRI3: 3, GMSH_TRI6: 6}


# ── Core face mapping ─────────────────────────────────────────────────────────

def build_face_element_map(
    elements: list["Element"],
) -> dict[frozenset, tuple[int, int]]:
    """
    Pre-compute: corner-node frozenset → (element_id, face_index 0-3).

    For each volume element, register all 4 faces by their 3 corner nodes.
    Lookup is O(1) per surface triangle during face group construction.
    """
    lookup: dict[frozenset, tuple[int, int]] = {}
    for e in elements:
        if len(e.nodes) < 4:
            continue  # malformed element — skip rather than IndexError
        for fi, (a, b, c) in enumerate(TET_FACE_CORNERS):
            try:
                key = frozenset([e.nodes[a], e.nodes[b], e.nodes[c]])
            except IndexError:
                continue
            lookup[key] = (e.id, fi)
    return lookup


def get_surface_element_faces(
    surf_tag: int,
    face_elem_map: dict[frozenset, tuple[int, int]],
) -> tuple[list[list[int]], set[int]]:
    """
    For a Gmsh surface (physical group tag), return:
      - element_faces: [[elem_id, face_idx], ...] for all surface triangles
      - node_ids: set of all node IDs on this surface

    Uses face_elem_map for O(1) element face lookup.
    Handles both tri3 (order-1) and tri6 (order-2) surface elements.
    """
    import gmsh
    node_set: set[int] = set()
    elem_faces: list[list[int]] = []
    seen_elem_faces: set[tuple[int, int]] = set()  # deduplicate

    for tri_type, nn in [(GMSH_TRI3, 3), (GMSH_TRI6, 6)]:
        try:
            tri_tags, tri_nodes_flat = gmsh.model.mesh.getElementsByType(
                tri_type, tag=surf_tag)
        except Exception:
            continue
        if len(tri_tags) == 0:
            continue

        n_tris = len(tri_tags)
        tri_nodes_arr = np.array(tri_nodes_flat, dtype=int).reshape(n_tris, nn)

        for i in range(n_tris):
            row = tri_nodes_arr[i]
            # All nodes on surface (corner + midside for tri6)
            node_set.update(int(n) for n in row)

            # Only use corner nodes (first 3) for element face lookup
            corners = frozenset(int(row[j]) for j in range(3))
            hit = face_elem_map.get(corners)
            if hit is not None:
                eid, fi = hit
                if (eid, fi) not in seen_elem_faces:
                    seen_elem_faces.add((eid, fi))
                    elem_faces.append([eid, fi])

    return elem_faces, node_set


# ── Node / element extraction ─────────────────────────────────────────────────

def extract_nodes(coord_scale: float = 1.0) -> list["Node"]:
    """Extract all nodes from current Gmsh model.

    coord_scale: multiply every coordinate by this factor.
      Use 0.001 when the Gmsh model is in mm and you want SI meters output.
    """
    import gmsh
    from .models import Node

    node_tags, node_coords, _ = gmsh.model.mesh.getNodes()
    coords = np.array(node_coords).reshape(-1, 3)
    return [
        Node(id=int(nid),
             x=float(coords[i, 0]) * coord_scale,
             y=float(coords[i, 1]) * coord_scale,
             z=float(coords[i, 2]) * coord_scale)
        for i, nid in enumerate(node_tags)
    ]


def extract_volume_elements() -> list["Element"]:
    """Extract all tetrahedral volume elements from current Gmsh model."""
    import gmsh
    from .models import Element

    elements: list[Element] = []
    for gmsh_type, ccx_type in CCX_TYPE_NAME.items():
        try:
            elem_tags, elem_nodes_flat = gmsh.model.mesh.getElementsByType(gmsh_type)
        except Exception:
            continue
        if len(elem_tags) == 0:
            continue

        npel = NODES_PER_ELEM[gmsh_type]
        n_elems = len(elem_tags)
        nodes_arr = np.array(elem_nodes_flat, dtype=int).reshape(n_elems, npel)

        for i, eid in enumerate(elem_tags):
            nids = [int(n) for n in nodes_arr[i]]
            # Gmsh TET10 midside ordering differs from CCX/Abaqus C3D10 at
            # slots 8 and 9. Gmsh: 8=mid(2,3), 9=mid(1,3). CCX C3D10 expects
            # 8=mid(1,3), 9=mid(2,3). Without this swap ccx reports
            # "nonpositive jacobian determinant in element ...". Verified
            # empirically against gmsh.model.mesh.getElementsByType(11).
            if ccx_type == "C3D10":
                nids[8], nids[9] = nids[9], nids[8]
            elements.append(Element(
                id=int(eid),
                type=ccx_type,
                nodes=nids,
            ))

    return elements


def build_mesh_data(
    surface_tags: list[int],
    face_elem_map: dict[frozenset, tuple[int, int]],
    coord_scale: float = 1.0,
) -> "MeshData":
    """
    Build complete MeshData from current Gmsh model state.
    Call after mesh generation + synchronize.

    coord_scale: passed through to extract_nodes (use 0.001 for mm→m).
    """
    from .models import FaceGroup, MeshData

    nodes = extract_nodes(coord_scale=coord_scale)
    elements = extract_volume_elements()

    faces: list[FaceGroup] = []
    for surf_tag in surface_tags:
        elem_faces, node_set = get_surface_element_faces(surf_tag, face_elem_map)
        faces.append(FaceGroup(
            face_id=surf_tag,
            element_faces=elem_faces,
            node_ids=sorted(node_set),
        ))

    return MeshData(nodes=nodes, elements=elements, faces=faces)


# ── Geometry helpers ──────────────────────────────────────────────────────────

def get_surface_tags() -> list[int]:
    import gmsh
    return [tag for _, tag in gmsh.model.getEntities(dim=2)]


def get_volume_tags() -> list[int]:
    import gmsh
    return [tag for _, tag in gmsh.model.getEntities(dim=3)]


def get_model_bounding_box() -> tuple[float, float, float, float, float, float]:
    """Returns (xmin, ymin, zmin, xmax, ymax, zmax)."""
    import gmsh
    return gmsh.model.getBoundingBox(-1, -1)


def add_physical_groups_for_all_surfaces() -> list[int]:
    """
    Add a physical group (tag = surface tag) for each OCC surface.
    Returns list of surface tags.
    """
    import gmsh
    surf_tags = get_surface_tags()
    for tag in surf_tags:
        gmsh.model.addPhysicalGroup(2, [tag], tag=tag)
        gmsh.model.setPhysicalName(2, tag, f"Face_{tag}")
    return surf_tags


def add_physical_group_for_all_volumes() -> None:
    import gmsh
    vol_tags = get_volume_tags()
    if vol_tags:
        gmsh.model.addPhysicalGroup(3, vol_tags, tag=1)
        gmsh.model.setPhysicalName(3, 1, "Volume")


def apply_mesh_settings(
    element_size: float,
    order: int,
    algorithm3d: int,
) -> None:
    """Configure Gmsh meshing options.

    NOTE: `Mesh.ElementOrder=2` alone is not always sufficient to promote tets
    to TET10 — some Gmsh builds need a post-generate `setOrder(2)` call.
    Callers that require quadratic elements should invoke
    :func:`promote_to_quadratic` after `mesh.generate(3)`.
    """
    import gmsh
    gmsh.option.setNumber("Mesh.CharacteristicLengthMax", element_size)
    gmsh.option.setNumber("Mesh.CharacteristicLengthMin", element_size * 0.1)
    gmsh.option.setNumber("Mesh.Algorithm3D", algorithm3d)
    gmsh.option.setNumber("Mesh.ElementOrder", order)
    gmsh.option.setNumber("Mesh.SecondOrderLinear", 0)
    gmsh.option.setNumber("Mesh.OptimizeNetgen", 1)
    gmsh.option.setNumber("Mesh.SecondOrderIncomplete", 0)


def promote_to_quadratic() -> None:
    """Upgrade the current mesh to 2nd order (TET4→TET10, TRI3→TRI6).

    Idempotent: calling it on an already-quadratic mesh is a no-op. Some Gmsh
    builds ignore `Mesh.ElementOrder=2` when meshing via algorithm 4; calling
    this explicitly guarantees C3D10 output.
    """
    import gmsh
    try:
        gmsh.model.mesh.setOrder(2)
    except Exception:
        pass


# ── Face mapping validation ───────────────────────────────────────────────────

def validate_face_groups(mesh: "MeshData") -> list[str]:
    """
    Sanity checks on face groups after meshing.
    Returns list of warning strings (empty = clean).
    Also strips malformed element_face entries in place so downstream code
    can assume each entry is [elem_id, face_idx].
    """
    warnings: list[str] = []
    elem_ids = {e.id for e in mesh.elements}
    for fg in mesh.faces:
        if not fg.node_ids:
            warnings.append(f"Face {fg.face_id}: no nodes found.")
        if not fg.element_faces:
            warnings.append(
                f"Face {fg.face_id}: no element faces mapped. "
                f"Loads/BCs on this face will have no effect.")
        # Drop malformed / stale entries
        cleaned: list[list[int]] = []
        for ef in fg.element_faces:
            if len(ef) != 2:
                warnings.append(
                    f"Face {fg.face_id}: malformed element_face entry {ef} (dropped).")
                continue
            eid, fi = int(ef[0]), int(ef[1])
            if eid not in elem_ids:
                warnings.append(
                    f"Face {fg.face_id}: element {eid} not in mesh (dropped).")
                continue
            if not (0 <= fi <= 3):
                warnings.append(
                    f"Face {fg.face_id}: face index {fi} out of range (dropped).")
                continue
            cleaned.append([eid, fi])
        fg.element_faces = cleaned
    return warnings
