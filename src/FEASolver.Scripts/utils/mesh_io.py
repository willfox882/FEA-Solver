"""
mesh_io.py
----------
JSON serialization / deserialization for all FEA Solver data models.
Contract: the JSON schema here is the source of truth; C# models mirror it.

Public API:
  mesh_data_to_json(mesh: MeshData) -> str
  mesh_data_from_json(s: str) -> MeshData
  fea_model_to_json(model: FEAModel) -> str
  fea_model_from_json(s: str) -> FEAModel
  result_set_from_json(s: str) -> ResultSet
  write_mesh_data(mesh: MeshData, path: str) -> None
  read_mesh_data(path: str) -> MeshData
"""

from __future__ import annotations
import json
from typing import Any
from .models import (
    Node, Element, FaceGroup, MeshData,
    MaterialModel, BcType, BoundaryCondition,
    LoadType, LocationMode, DirectionMode, LocalizedLoadData, Load,
    ResultField, ResultSet, FEAModel,
)


# ── Serializers ───────────────────────────────────────────────────────────────

def _node_to_dict(n: Node) -> dict:
    return {"id": n.id, "x": n.x, "y": n.y, "z": n.z}

def _elem_to_dict(e: Element) -> dict:
    return {"id": e.id, "type": e.type, "nodes": e.nodes}

def _face_to_dict(f: FaceGroup) -> dict:
    return {"face_id": f.face_id,
            "element_faces": f.element_faces,
            "node_ids": f.node_ids}

def _mesh_to_dict(m: MeshData) -> dict:
    return {
        "nodes": [_node_to_dict(n) for n in m.nodes],
        "elements": [_elem_to_dict(e) for e in m.elements],
        "faces": [_face_to_dict(f) for f in m.faces],
    }

def _material_to_dict(mat: MaterialModel) -> dict:
    return {"name": mat.name,
            "youngs_modulus": mat.youngs_modulus,
            "poissons_ratio": mat.poissons_ratio}

def _bc_to_dict(bc: BoundaryCondition) -> dict:
    return {"type": bc.type.value,
            "face_id": bc.face_id,
            "node_ids": bc.node_ids}

def _localized_to_dict(loc: LocalizedLoadData) -> dict:
    return {k: v for k, v in {
        "mode": loc.mode.value,
        "xyz": loc.xyz,
        "uv": loc.uv,
        "force": loc.force,
        "axis_direction": loc.axis_direction,
        "magnitude": loc.magnitude,
        "resolved_node_id": loc.resolved_node_id,
    }.items() if v is not None}

def _load_to_dict(load: Load) -> dict:
    d: dict = {"type": load.type.value,
               "face_id": load.face_id,
               "magnitude": load.magnitude}
    if load.direction is not None:
        d["direction"] = load.direction
    if load.localized is not None:
        d["localized"] = _localized_to_dict(load.localized)
    if load.direction_mode is not DirectionMode.EXPLICIT:
        d["direction_mode"] = load.direction_mode.value
    return d


# ── Deserializers ─────────────────────────────────────────────────────────────

def _node_from_dict(d: dict) -> Node:
    return Node(id=d["id"], x=d["x"], y=d["y"], z=d["z"])

def _elem_from_dict(d: dict) -> Element:
    return Element(id=d["id"], type=d["type"], nodes=d["nodes"])

def _face_from_dict(d: dict) -> FaceGroup:
    return FaceGroup(
        face_id=d["face_id"],
        element_faces=d["element_faces"],
        node_ids=d["node_ids"],
    )

def _mesh_from_dict(d: dict) -> MeshData:
    return MeshData(
        nodes=[_node_from_dict(n) for n in d["nodes"]],
        elements=[_elem_from_dict(e) for e in d["elements"]],
        faces=[_face_from_dict(f) for f in d["faces"]],
    )

def _material_from_dict(d: dict) -> MaterialModel:
    return MaterialModel(
        name=d["name"],
        youngs_modulus=d["youngs_modulus"],
        poissons_ratio=d["poissons_ratio"],
    )

def _bc_from_dict(d: dict) -> BoundaryCondition:
    return BoundaryCondition(
        type=BcType(d["type"]),
        face_id=d["face_id"],
        node_ids=d.get("node_ids", []),
    )

def _localized_from_dict(d: dict) -> LocalizedLoadData:
    return LocalizedLoadData(
        mode=LocationMode(d["mode"]),
        xyz=d.get("xyz"),
        uv=d.get("uv"),
        force=d.get("force"),
        axis_direction=d.get("axis_direction"),
        magnitude=d.get("magnitude"),
        resolved_node_id=d.get("resolved_node_id"),
    )

def _load_from_dict(d: dict) -> Load:
    loc = _localized_from_dict(d["localized"]) if d.get("localized") else None
    dm_val = d.get("direction_mode")
    dm = DirectionMode(dm_val) if dm_val else DirectionMode.EXPLICIT
    return Load(
        type=LoadType(d["type"]),
        face_id=d["face_id"],
        magnitude=d.get("magnitude", 0.0),
        direction=d.get("direction"),
        localized=loc,
        direction_mode=dm,
    )

def _result_field_from_dict(d: dict | None) -> ResultField | None:
    if d is None:
        return None
    return ResultField(node_ids=d["node_ids"], values=d["values"])


# ── Public API ────────────────────────────────────────────────────────────────

def mesh_data_to_json(mesh: MeshData, indent: int | None = None) -> str:
    return json.dumps(_mesh_to_dict(mesh),
                      separators=(",", ":") if indent is None else None,
                      indent=indent)

def mesh_data_from_json(s: str) -> MeshData:
    return _mesh_from_dict(json.loads(s))

def fea_model_to_json(model: FEAModel, indent: int | None = None) -> str:
    d = {
        "mesh": _mesh_to_dict(model.mesh),
        "material": _material_to_dict(model.material),
        "boundary_conditions": [_bc_to_dict(bc) for bc in model.boundary_conditions],
        "loads": [_load_to_dict(ld) for ld in model.loads],
        "work_dir": model.work_dir,
    }
    return json.dumps(d,
                      separators=(",", ":") if indent is None else None,
                      indent=indent)

def fea_model_from_json(s: str) -> FEAModel:
    d = json.loads(s)
    return FEAModel(
        mesh=_mesh_from_dict(d["mesh"]),
        material=_material_from_dict(d["material"]),
        boundary_conditions=[_bc_from_dict(bc) for bc in d.get("boundary_conditions", [])],
        loads=[_load_from_dict(ld) for ld in d.get("loads", [])],
        work_dir=d.get("work_dir", ""),
    )

def result_set_from_json(s: str) -> ResultSet:
    d = json.loads(s)
    return ResultSet(
        step=d.get("step", 1),
        displacement_x=_result_field_from_dict(d.get("displacement_x")),
        displacement_y=_result_field_from_dict(d.get("displacement_y")),
        displacement_z=_result_field_from_dict(d.get("displacement_z")),
        displacement_mag=_result_field_from_dict(d.get("displacement_mag")),
        vonmises=_result_field_from_dict(d.get("vonmises")),
    )

def write_mesh_data(mesh: MeshData, path: str) -> None:
    with open(path, "w") as f:
        f.write(mesh_data_to_json(mesh))

def read_mesh_data(path: str) -> MeshData:
    with open(path) as f:
        return mesh_data_from_json(f.read())

def write_fea_model(model: FEAModel, path: str) -> None:
    with open(path, "w") as f:
        f.write(fea_model_to_json(model, indent=2))

def read_fea_model(path: str) -> FEAModel:
    with open(path) as f:
        return fea_model_from_json(f.read())
