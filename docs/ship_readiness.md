# FEA Solver — Ship-Readiness & NX-Nastran Gap Analysis

**Status (2026-06-14):** 182 Python + 37 C# tests passing (219 total); CI on
`windows-latest` runs both suites per push/PR. Linear-static 3D solid pipeline
(STEP → Gmsh C3D10 → CalculiX → FRD → heatmap) is feature-complete and
physically verified by static-INP and end-to-end harnesses.

---

## What ships now

| Area                        | State        | Notes                                          |
|-----------------------------|--------------|------------------------------------------------|
| Geometry import             | ✅           | STEP via Gmsh OCC; mm/m unit declaration       |
| Meshing                     | ✅           | Tet4/Tet10, Delaunay, Netgen optimise, mesh cache |
| Face selection              | ✅           | Stable CAD-face → Gmsh physical group mapping  |
| BCs                         | ✅ (partial) | Fixed, Pinned, Roller-X/Y/Z (zero-displacement only) |
| Loads                       | ✅           | Pressure, SurfaceTraction, DistributedForce, PointLoad, Moment, Torque |
| Auto face-normal direction  | ✅           | `DirectionMode.NormalOutward / NormalInward` per element-face |
| Consistent lumping          | ✅           | C3D4 corner lumping; C3D10 midside lumping (not naïve 1/N) |
| INP pre-write validation    | ✅           | Empty NSET/ELSET/SURFACE → ValueError before ccx |
| Solver integration          | ✅           | Subprocess, cancel, stderr capture, OUTPUT=3D ASCII FRD |
| Results parse               | ✅           | U, Cauchy tensor (S11…S13), vM, reaction forces (FORC) |
| Results visualisation       | ✅           | vM, DispMag, Disp-X/Y/Z, Max/Min Principal, deformed mesh |
| Verification harness        | ✅           | `verify_solver.py` cantilever tension            |
| Per-load-type physics tests | ✅           | 13 tests verify INP output matches equilibrium   |
| Tilted-face normal          | ✅           | Parametrised 0/30/45/-30/60/90° tests pass       |

---

## NX-Nastran gaps — prioritised

### Must-have for a credible v1 (still missing)

1. **Gravity / body force** — `*DLOAD EALL, GRAV, g, dx, dy, dz`.
2. **Enforced nonzero displacement BC** — trivial: allow magnitude ≠ 0 on
   `*BOUNDARY` rows.
3. **Modal analysis (natural frequencies)** — `*FREQUENCY, NSET=...`.
4. **Strain & strain-energy output** — add `E` to `*EL FILE`, parse ETO block.
5. **More benchmark cases** in `verify_solver.py`: cantilever bending,
   thin-walled torsion, internally pressurised cylinder.

### Nice-to-have (v2)

- Multi-load-case / load combinations (`*LOAD CASE`).
- Thermal loads (`*TEMPERATURE`, `*EXPANSION`).
- Nonlinear geometry (`*NLGEOM`).
- Hex-dominant meshing; mesh quality metrics (aspect ratio, skew).
- Multi-material assemblies (per-ELSET material, bonded contact).

### Out of scope for this program

- Element zoo beyond 3D solids: shells, beams, rigids, plates,
  composite layups, bolt connectors.
- Nonlinear material: plasticity, hyperelasticity, creep.
- Transient & harmonic dynamics, buckling, contact.
- Full CAE ecosystem: PDM, assembly simplification, submodelling.

These are the features that separate "Linear static 3D solid FEA app" from
"industrial CAE suite". Shipping them without a dedicated team producing a
verification matrix per element type would be irresponsible — defer unless
the scope is deliberately expanded.

---

## Running the verification harness

```bash
# Smoke test — INP generation only (no ccx required)
python src/FEASolver.Scripts/verify_solver.py --no-solve

# Full end-to-end with numerical checks
python src/FEASolver.Scripts/verify_solver.py --ccx path/to/ccx.exe
```

Exit codes: 0 pass / 1 numeric deviation / 2 pipeline failure /
3 ccx not found.
