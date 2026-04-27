"""pytest tests for inp_writer.py"""
import sys
import os
import math
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))
from utils.inp_writer import InpWriter, torque_to_nodal_forces


MINIMAL_MODEL = {
    "mesh": {
        "nodes": [
            {"id": 1, "x": 0.0, "y": 0.0, "z": 0.0},
            {"id": 2, "x": 1.0, "y": 0.0, "z": 0.0},
            {"id": 3, "x": 0.0, "y": 1.0, "z": 0.0},
            {"id": 4, "x": 0.0, "y": 0.0, "z": 1.0},
        ],
        "elements": [
            {"id": 1, "type": "C3D4", "nodes": [1, 2, 3, 4]}
        ],
        "faces": [
            {"face_id": 1, "element_faces": [[1, 0]], "node_ids": [1, 2, 3]},
            {"face_id": 2, "element_faces": [[1, 3]], "node_ids": [1, 3, 4]},
        ]
    },
    "material": {
        "name": "Steel",
        "youngs_modulus": 2e11,
        "poissons_ratio": 0.3
    },
    "boundary_conditions": [
        {"type": "Fixed", "face_id": 1, "node_ids": [1, 2, 3]}
    ],
    "loads": [
        {"type": "Pressure", "face_id": 2, "magnitude": 1e6,
         "direction": None, "localized": None}
    ],
    "work_dir": "/tmp"
}


def test_write_produces_file(tmp_path):
    out = str(tmp_path / "model.inp")
    writer = InpWriter(MINIMAL_MODEL)
    writer.write(out)
    content = open(out).read()
    assert "*NODE" in content
    assert "*ELEMENT" in content
    assert "*MATERIAL" in content
    assert "*STATIC" in content
    assert "*BOUNDARY" in content
    assert "*DLOAD" in content
    assert "*END STEP" in content


def test_fixed_bc_has_dof_1_to_3(tmp_path):
    out = str(tmp_path / "model.inp")
    InpWriter(MINIMAL_MODEL).write(out)
    content = open(out).read()
    # CalculiX FIXED BC: NSET, first_dof, last_dof (zero displacement is implied)
    assert "BC_F1_FIXED, 1, 3" in content


def test_torque_to_nodal_forces_net_moment():
    """Net moment about Y axis should equal input torque."""
    import numpy as np
    nodes = {
        1: [1.0, 0.0, 0.0],
        2: [-1.0, 0.0, 0.0],
        3: [0.0, 0.0, 1.0],
        4: [0.0, 0.0, -1.0],
    }
    T = 100.0
    forces = torque_to_nodal_forces(nodes, [0, 0, 0], [0, 1, 0], T)
    # Sum of moments = sum of r × F about Y axis
    total_moment = 0.0
    for nid, f in forces.items():
        r = np.array(nodes[nid])
        fv = np.array(f)
        moment = np.cross(r, fv)
        total_moment += moment[1]  # Y component
    assert abs(total_moment - T) < 1e-8
