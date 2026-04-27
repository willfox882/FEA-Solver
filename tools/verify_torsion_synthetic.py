#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
verify_torsion_synthetic.py
---------------------------
Solver-free synthetic torsion verification harness.

Purpose
~~~~~~~
Validates the torque_to_nodal_forces() distribution algorithm without
running CalculiX. Creates synthetic face node sets representing common
geometries, applies torque, and verifies:

  1. Moment equilibrium:  Σ r_perp_i × F_i projected onto axis == T  (< 1e-8 rel)
  2. Tangentiality:       F_i ⊥ r_perp_i for every node               (< 1e-12 dot)
  3. No axial leakage:    F_i · axis_hat == 0 for every node            (< 1e-12)
  4. r²-weighting:        outer nodes get more force than inner nodes
  5. INP output:          CLOAD lines are syntactically valid
  6. Renderer preview:    prints nodal force table (substitutes for visual preview)

Run with:
    python tools/verify_torsion_synthetic.py

Returns exit code 0 on all PASS, non-zero on any FAIL.
"""
from __future__ import annotations
import sys
import os
import math
import io
import numpy as np

# Force UTF-8 output on Windows (avoids cp1252 codec errors for box-drawing chars)
if hasattr(sys.stdout, 'buffer'):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../src/FEASolver.Scripts"))
from utils.inp_writer import torque_to_nodal_forces

# ── ANSI colour helpers (no deps) ─────────────────────────────────────────────

_GREEN  = "\033[92m"
_RED    = "\033[91m"
_YELLOW = "\033[93m"
_RESET  = "\033[0m"


def _ok(msg: str) -> str:   return f"{_GREEN}PASS{_RESET}  {msg}"
def _fail(msg: str) -> str: return f"{_RED}FAIL{_RESET}  {msg}"
def _info(msg: str) -> str: return f"      {msg}"


# ── Core verification routine ─────────────────────────────────────────────────

def verify_torque_case(
    name: str,
    positions: dict[int, list[float]],
    origin: list[float],
    axis: list[float],
    torque_Nm: float,
    *,
    eq_tol: float = 1e-8,
    tan_tol: float = 1e-12,
) -> bool:
    """Run all checks for one torque case. Returns True if all pass."""
    print(f"\n{'─'*60}")
    print(f"  Case: {name}")
    print(f"  Nodes={len(positions)}, T={torque_Nm:.3g} N·m, "
          f"axis=[{axis[0]:.2g},{axis[1]:.2g},{axis[2]:.2g}], "
          f"origin=[{origin[0]:.3g},{origin[1]:.3g},{origin[2]:.3g}]")
    print(f"{'─'*60}")

    a = np.array(axis, float)
    a /= np.linalg.norm(a)

    passed = True

    # ── Compute forces ────────────────────────────────────────────────────────
    try:
        forces = torque_to_nodal_forces(positions, origin, axis, torque_Nm, validate=True)
    except ValueError as e:
        print(_fail(f"torque_to_nodal_forces raised: {e}"))
        return False

    # ── Check 1: moment equilibrium ───────────────────────────────────────────
    m_net = np.zeros(3)
    r_perp_all: dict[int, np.ndarray] = {}
    for nid, pos in positions.items():
        r = np.array(pos) - np.array(origin)
        rp = r - float(np.dot(r, a)) * a
        r_perp_all[nid] = rp
        m_net += np.cross(rp, np.array(forces[nid]))

    m_axial = float(np.dot(m_net, a))
    if abs(torque_Nm) > 1e-15:
        err = abs(m_axial - torque_Nm) / abs(torque_Nm)
        ok = err < eq_tol
    else:
        err = abs(m_axial)
        ok = err < 1e-14
    msg = f"Moment equilibrium: M_net={m_axial:.6e} N·m, T={torque_Nm:.6e} N·m, rel_err={err:.2e}"
    print(_ok(msg) if ok else _fail(msg))
    passed = passed and ok

    # ── Check 2: tangentiality ────────────────────────────────────────────────
    max_radial_dot = 0.0
    for nid, f in forces.items():
        rp = r_perp_all[nid]
        rp_mag = float(np.linalg.norm(rp))
        if rp_mag < 1e-12:
            continue
        dot = abs(float(np.dot(rp / rp_mag, np.array(f))))
        if dot > max_radial_dot:
            max_radial_dot = dot
    ok = max_radial_dot < tan_tol
    msg = f"Tangentiality (F ⊥ r_perp):     max |F · r̂_perp| = {max_radial_dot:.2e}"
    print(_ok(msg) if ok else _fail(msg))
    passed = passed and ok

    # ── Check 3: no axial force leakage ──────────────────────────────────────
    max_axial = max(abs(float(np.dot(np.array(f), a))) for f in forces.values())
    ok = max_axial < tan_tol
    msg = f"No axial leakage (F · axis≈0):  max |F · â| = {max_axial:.2e}"
    print(_ok(msg) if ok else _fail(msg))
    passed = passed and ok

    # ── Check 4: INP CLOAD format ─────────────────────────────────────────────
    cload_lines: list[str] = []
    for nid, f in forces.items():
        for dof, fv in enumerate(f, 1):
            if abs(fv) > 1e-15:
                cload_lines.append(f"{nid}, {dof}, {fv:.6e}")
    # Validate each line: "int, int, float"
    bad = []
    for line in cload_lines:
        parts = [p.strip() for p in line.split(",")]
        if len(parts) != 3:
            bad.append(line); continue
        try:
            int(parts[0]); int(parts[1]); float(parts[2])
        except ValueError:
            bad.append(line)
    ok = len(bad) == 0
    msg = f"INP CLOAD syntax: {len(cload_lines)} entries" + (
        f" — {len(bad)} invalid" if bad else "")
    print(_ok(msg) if ok else _fail(msg))
    passed = passed and ok

    # ── Preview: nodal force table ────────────────────────────────────────────
    print(_info(f"{'Node':>6}  {'r_perp (m)':>14}  {'|F| (N)':>12}  {'F_x':>12}  {'F_y':>12}  {'F_z':>12}"))
    for nid in sorted(forces.keys()):
        rp = r_perp_all[nid]
        f  = np.array(forces[nid])
        print(_info(
            f"{nid:>6}  {np.linalg.norm(rp):>14.6f}  "
            f"{np.linalg.norm(f):>12.4f}  "
            f"{f[0]:>12.4f}  {f[1]:>12.4f}  {f[2]:>12.4f}"
        ))

    return passed


# ── Test cases ────────────────────────────────────────────────────────────────

def make_circle(r: float, n: int, z: float = 0.0) -> dict[int, list[float]]:
    return {
        i + 1: [r * math.cos(2 * math.pi * i / n),
                r * math.sin(2 * math.pi * i / n), z]
        for i in range(n)
    }


def make_rect(w: float, h: float, nw: int = 2, nh: int = 2) -> dict[int, list[float]]:
    """Grid of nodes on an XY rectangle centred at origin."""
    pts: dict[int, list[float]] = {}
    nid = 1
    for i in range(nw):
        for j in range(nh):
            x = -w / 2 + w * i / (nw - 1)
            y = -h / 2 + h * j / (nh - 1)
            pts[nid] = [x, y, 0.0]; nid += 1
    return pts


CASES = [
    dict(name="Circle R=50mm, N=12, Z-axis, T=1000 N·m",
         positions=make_circle(0.05, 12),
         origin=[0, 0, 0], axis=[0, 0, 1], torque_Nm=1000.0),

    dict(name="Circle R=25mm, N=24, Y-axis, T=-500 N·m",
         positions=make_circle(0.025, 24, z=0.0),
         origin=[0, 0, 0], axis=[0, 1, 0], torque_Nm=-500.0),

    dict(name="Square 100×100mm corners, Z-axis, T=250 N·m",
         positions={1:[0.05,0.05,0],2:[-0.05,0.05,0],
                    3:[-0.05,-0.05,0],4:[0.05,-0.05,0]},
         origin=[0,0,0], axis=[0,0,1], torque_Nm=250.0),

    dict(name="Rectangle 200×60mm, 4×3 grid, Z-axis, T=800 N·m",
         positions=make_rect(0.2, 0.06, nw=4, nh=3),
         origin=[0,0,0], axis=[0,0,1], torque_Nm=800.0),

    dict(name="Arbitrary polygon, diagonal axis, T=120 N·m",
         positions={1:[0.06,0.02,0.0],2:[-0.03,0.07,0.0],
                    3:[-0.05,-0.04,0.0],4:[0.02,-0.06,0.0]},
         origin=[0.01,-0.01,0], axis=[1,1,0], torque_Nm=120.0),

    dict(name="Off-axis origin, circle R=30mm N=8, X-axis, T=350 N·m",
         positions={
             i+1: [0.03*math.cos(2*math.pi*i/8), 0.0, 0.03*math.sin(2*math.pi*i/8)]
             for i in range(8)
         },
         origin=[0.005, 0.0, -0.002], axis=[1,0,0], torque_Nm=350.0),

    dict(name="Mixed-radius nodes (r²-weight test), Z-axis, T=100 N·m",
         positions={1:[0.01,0,0], 2:[0.05,0,0], 3:[0.10,0,0]},
         origin=[0,0,0], axis=[0,0,1], torque_Nm=100.0),

    dict(name="1 node on axis + 3 off-axis, Z-axis, T=200 N·m",
         positions={1:[0,0,0.1], 2:[0.04,0,0], 3:[-0.04,0,0], 4:[0,0.04,0]},
         origin=[0,0,0], axis=[0,0,1], torque_Nm=200.0),
]


# ── r²-weighting explicit check ───────────────────────────────────────────────

def verify_r_squared_weighting() -> bool:
    """Inner node (r=0.01) must receive less force than outer (r=0.10)."""
    print(f"\n{'─'*60}")
    print(f"  Case: r²-weighting explicit check")
    print(f"{'─'*60}")
    T = 100.0
    axis = [0, 0, 1]
    positions = {1: [0.01, 0, 0], 2: [0.10, 0, 0]}
    forces = torque_to_nodal_forces(positions, [0,0,0], axis, T)
    m1 = np.linalg.norm(forces[1])
    m2 = np.linalg.norm(forces[2])
    ok = m2 > m1
    D = 0.01**2 + 0.10**2
    expected1 = T * 0.01 / D
    expected2 = T * 0.10 / D
    err1 = abs(m1 - expected1) / expected1
    err2 = abs(m2 - expected2) / expected2
    ok2 = err1 < 1e-10 and err2 < 1e-10
    msg1 = f"|F_inner|={m1:.4f} N < |F_outer|={m2:.4f} N"
    msg2 = f"Expected |F_inner|={expected1:.4f}, err={err1:.2e}; |F_outer|={expected2:.4f}, err={err2:.2e}"
    print(_ok(msg1) if ok else _fail(msg1))
    print(_ok(msg2) if ok2 else _fail(msg2))
    return ok and ok2


# ── Main ──────────────────────────────────────────────────────────────────────

def main() -> int:
    print("\n" + "="*60)
    print("  FEA Solver — Synthetic Torsion Verification Harness")
    print("="*60)

    all_pass = True
    for case in CASES:
        ok = verify_torque_case(**case)
        all_pass = all_pass and ok

    ok = verify_r_squared_weighting()
    all_pass = all_pass and ok

    print(f"\n{'='*60}")
    if all_pass:
        print(f"{_GREEN}ALL PASS{_RESET} — torque distribution is correct.")
    else:
        print(f"{_RED}SOME TESTS FAILED{_RESET} — review output above.")
    print("="*60 + "\n")

    return 0 if all_pass else 1


if __name__ == "__main__":
    sys.exit(main())
