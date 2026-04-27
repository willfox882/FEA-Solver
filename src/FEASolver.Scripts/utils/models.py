"""
models.py
---------
Canonical Python dataclasses mirroring FEASolver.Core.Models (C#).
All serialization goes through mesh_io.py.
Units: SI (m, Pa, N, N·m).
"""

from __future__ import annotations
from dataclasses import dataclass, field
from enum import Enum
from typing import Optional


# ── Geometry ──────────────────────────────────────────────────────────────────

@dataclass
class Node:
    id: int
    x: float
    y: float
    z: float

    def coords(self) -> tuple[float, float, float]:
        return (self.x, self.y, self.z)

    def dist_to(self, other: "Node") -> float:
        import math
        return math.sqrt(
            (self.x - other.x) ** 2 +
            (self.y - other.y) ** 2 +
            (self.z - other.z) ** 2
        )


@dataclass
class Element:
    id: int
    type: str          # "C3D10" or "C3D4"
    nodes: list[int]   # 1-based node IDs

    @property
    def n_nodes(self) -> int:
        return len(self.nodes)

    @property
    def is_quadratic(self) -> bool:
        return self.type == "C3D10"


@dataclass
class FaceGroup:
    face_id: int
    element_faces: list[list[int]]   # [[elem_id, face_index], ...]
    node_ids: list[int]              # deduplicated surface node IDs

    @property
    def n_nodes(self) -> int:
        return len(self.node_ids)

    @property
    def n_element_faces(self) -> int:
        return len(self.element_faces)


@dataclass
class BoundingBox:
    xmin: float; ymin: float; zmin: float
    xmax: float; ymax: float; zmax: float

    @property
    def size_x(self) -> float: return self.xmax - self.xmin
    @property
    def size_y(self) -> float: return self.ymax - self.ymin
    @property
    def size_z(self) -> float: return self.zmax - self.zmin

    @property
    def diagonal(self) -> float:
        import math
        return math.sqrt(self.size_x**2 + self.size_y**2 + self.size_z**2)

    @property
    def center(self) -> tuple[float, float, float]:
        return (
            (self.xmin + self.xmax) / 2,
            (self.ymin + self.ymax) / 2,
            (self.zmin + self.zmax) / 2,
        )

    def __str__(self) -> str:
        return (f"X:[{self.xmin*1000:.2f}, {self.xmax*1000:.2f}] "
                f"Y:[{self.ymin*1000:.2f}, {self.ymax*1000:.2f}] "
                f"Z:[{self.zmin*1000:.2f}, {self.zmax*1000:.2f}] mm")


@dataclass
class MeshData:
    nodes: list[Node]
    elements: list[Element]
    faces: list[FaceGroup]

    # Cached lookups — built lazily
    _node_by_id: dict[int, Node] = field(default_factory=dict, repr=False)
    _elem_by_id: dict[int, Element] = field(default_factory=dict, repr=False)
    _face_by_id: dict[int, FaceGroup] = field(default_factory=dict, repr=False)

    def build_lookups(self) -> None:
        self._node_by_id = {n.id: n for n in self.nodes}
        self._elem_by_id = {e.id: e for e in self.elements}
        self._face_by_id = {f.face_id: f for f in self.faces}

    def node(self, nid: int) -> Node:
        if not self._node_by_id:
            self.build_lookups()
        return self._node_by_id[nid]

    def element(self, eid: int) -> Element:
        if not self._elem_by_id:
            self.build_lookups()
        return self._elem_by_id[eid]

    def face(self, fid: int) -> FaceGroup:
        if not self._face_by_id:
            self.build_lookups()
        return self._face_by_id[fid]

    def bounding_box(self) -> BoundingBox:
        if not self.nodes:
            return BoundingBox(0, 0, 0, 0, 0, 0)
        xs = [n.x for n in self.nodes]
        ys = [n.y for n in self.nodes]
        zs = [n.z for n in self.nodes]
        return BoundingBox(
            min(xs), min(ys), min(zs),
            max(xs), max(ys), max(zs)
        )

    def stats(self) -> "MeshStats":
        by_type: dict[str, int] = {}
        for e in self.elements:
            by_type[e.type] = by_type.get(e.type, 0) + 1
        return MeshStats(
            n_nodes=len(self.nodes),
            n_elements=len(self.elements),
            n_faces=len(self.faces),
            elements_by_type=by_type,
            bbox=self.bounding_box(),
        )

    def nodes_on_face(self, face_id: int) -> list[Node]:
        fg = self.face(face_id)
        if not self._node_by_id:
            self.build_lookups()
        return [self._node_by_id[nid]
                for nid in fg.node_ids
                if nid in self._node_by_id]

    def nearest_node_on_face(self, face_id: int,
                              target_xyz: tuple[float, float, float]) -> Node:
        import math
        candidates = self.nodes_on_face(face_id)
        if not candidates:
            raise ValueError(f"No nodes on face {face_id}")
        tx, ty, tz = target_xyz
        return min(candidates,
                   key=lambda n: math.sqrt(
                       (n.x - tx)**2 + (n.y - ty)**2 + (n.z - tz)**2))


# ── Stats ─────────────────────────────────────────────────────────────────────

@dataclass
class MeshStats:
    n_nodes: int
    n_elements: int
    n_faces: int
    elements_by_type: dict[str, int]
    bbox: BoundingBox

    def __str__(self) -> str:
        types = ", ".join(f"{v} {k}" for k, v in self.elements_by_type.items())
        return (f"Nodes: {self.n_nodes}  Elements: {self.n_elements} ({types})  "
                f"Faces: {self.n_faces}  Bbox: {self.bbox}")


# ── Material ──────────────────────────────────────────────────────────────────

@dataclass
class MaterialModel:
    name: str
    youngs_modulus: float   # Pa
    poissons_ratio: float

    @classmethod
    def steel(cls) -> "MaterialModel":
        return cls("Steel", 200_000_000_000.0, 0.3)

    @classmethod
    def aluminium(cls) -> "MaterialModel":
        return cls("Aluminium", 70_000_000_000.0, 0.33)


# ── Boundary Conditions ───────────────────────────────────────────────────────

class BcType(str, Enum):
    FIXED = "Fixed"
    ROLLER_X = "RollerX"
    ROLLER_Y = "RollerY"
    ROLLER_Z = "RollerZ"
    PINNED = "Pinned"

    def constrained_dofs(self) -> list[int]:
        """Returns 1-based CalculiX DOF indices that are fixed to zero."""
        return {
            BcType.FIXED:   [1, 2, 3],
            BcType.PINNED:  [1, 2, 3],
            BcType.ROLLER_X: [1],
            BcType.ROLLER_Y: [2],
            BcType.ROLLER_Z: [3],
        }[self]


@dataclass
class BoundaryCondition:
    type: BcType
    face_id: int
    node_ids: list[int]   # resolved from face at assignment time


# ── Loads ─────────────────────────────────────────────────────────────────────

class LoadType(str, Enum):
    SURFACE_TRACTION = "SurfaceTraction"
    PRESSURE = "Pressure"
    POINT_LOAD = "PointLoad"
    MOMENT = "Moment"
    TORQUE = "Torque"
    # Total force (N) distributed across a face using consistent Galerkin
    # lumping. magnitude = |F|, direction = unit vector. INP writer computes
    # traction = F_vec / A_face and applies the same lumping as SURFACE_TRACTION
    # so Σ CLOAD equals F_vec exactly.
    DISTRIBUTED_FORCE = "DistributedForce"


class LocationMode(str, Enum):
    NEAREST_NODE = "NearestNode"
    PARAMETRIC = "Parametric"
    ABSOLUTE_XYZ = "AbsoluteXYZ"


class DirectionMode(str, Enum):
    """How the surface-load direction vector is interpreted.

    EXPLICIT        — use Load.direction verbatim (unit vector expected).
    NORMAL_OUTWARD  — ignore Load.direction; use per-element-face outward
                      normal (away from the 4th tet vertex). Matches NX
                      "Force on face" default.
    NORMAL_INWARD   — negated outward normal.
    """
    EXPLICIT = "Explicit"
    NORMAL_OUTWARD = "NormalOutward"
    NORMAL_INWARD = "NormalInward"


@dataclass
class LocalizedLoadData:
    mode: LocationMode
    xyz: Optional[list[float]] = None          # target point (m)
    uv: Optional[list[float]] = None           # parametric coords [0-1]
    force: Optional[list[float]] = None        # [Fx, Fy, Fz] N
    axis_direction: Optional[list[float]] = None  # unit vector
    magnitude: Optional[float] = None         # N·m
    resolved_node_id: Optional[int] = None


@dataclass
class Load:
    type: LoadType
    face_id: int
    magnitude: float                           # Pa or N
    direction: Optional[list[float]] = None   # unit vector (global, EXPLICIT)
    localized: Optional[LocalizedLoadData] = None
    direction_mode: DirectionMode = DirectionMode.EXPLICIT


# ── Results ───────────────────────────────────────────────────────────────────

@dataclass
class ResultField:
    node_ids: list[int]
    values: list[float]

    def at_node(self, node_id: int) -> Optional[float]:
        try:
            idx = self.node_ids.index(node_id)
            return self.values[idx]
        except ValueError:
            return None

    @property
    def min(self) -> float: return min(self.values) if self.values else 0.0
    @property
    def max(self) -> float: return max(self.values) if self.values else 0.0


@dataclass
class ResultSet:
    step: int
    displacement_x: Optional[ResultField] = None
    displacement_y: Optional[ResultField] = None
    displacement_z: Optional[ResultField] = None
    displacement_mag: Optional[ResultField] = None
    vonmises: Optional[ResultField] = None
    # Strain tensor (tensor components; shears already γ/2). Optional.
    e11: Optional[ResultField] = None
    e22: Optional[ResultField] = None
    e33: Optional[ResultField] = None
    e12: Optional[ResultField] = None
    e23: Optional[ResultField] = None
    e13: Optional[ResultField] = None
    strain_vonmises: Optional[ResultField] = None

    def max_displacement_mm(self) -> float:
        if self.displacement_mag:
            return self.displacement_mag.max * 1000.0
        return 0.0

    def max_vonmises_mpa(self) -> float:
        if self.vonmises:
            return self.vonmises.max / 1e6
        return 0.0


# ── Full FEA model ────────────────────────────────────────────────────────────

@dataclass
class FEAModel:
    mesh: MeshData
    material: MaterialModel
    boundary_conditions: list[BoundaryCondition]
    loads: list[Load]
    work_dir: str

    def validate(self) -> list[str]:
        """Returns list of validation errors (empty = valid)."""
        errors: list[str] = []
        if not self.mesh.nodes:
            errors.append("Mesh has no nodes.")
        if not self.mesh.elements:
            errors.append("Mesh has no elements.")
        if not self.boundary_conditions:
            errors.append("No boundary conditions defined.")
        if not self.loads:
            errors.append("No loads defined.")
        if self.material.youngs_modulus <= 0:
            errors.append("Young's modulus must be > 0.")
        if not (0 < self.material.poissons_ratio < 0.5):
            errors.append("Poisson's ratio must be in (0, 0.5).")
        # Check all BC face IDs exist
        face_ids = {f.face_id for f in self.mesh.faces}
        for bc in self.boundary_conditions:
            if bc.face_id not in face_ids:
                errors.append(f"BC face_id {bc.face_id} not in mesh faces.")
        for load in self.loads:
            if load.face_id not in face_ids:
                errors.append(f"Load face_id {load.face_id} not in mesh faces.")
        return errors
