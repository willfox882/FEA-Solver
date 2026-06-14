# Architecture

Windows-native WPF desktop app for 3D linear-static structural FEA. It wraps
**CalculiX** (`ccx.exe`) and **Gmsh** (Python API) behind an MVVM UI:
STEP → mesh → assign BCs/loads → solve → visualise (stress/displacement
heatmaps + deformed mesh). Scope: 3D linear-static solids only (C3D4/C3D10
tets). SI units internally (m, Pa, N, N·m); the UI shows mm/MPa/GPa.

> `claude.mb` in the repo root is the full architecture source of truth;
> `progress.mb` is the live task/state tracker. This file is the orientation
> summary — read it first, then those for detail.

## Projects

- **`src/FEASolver.App`** (WinExe, `net8.0-windows`, WPF + CommunityToolkit.Mvvm,
  HelixToolkit.Wpf): ViewModels (Main/Viewport/Properties/Results/ModelTree/
  Config), Services (Pipeline, Meshing, Solver, Result, Config, MeshCache), Views.
- **`src/FEASolver.Core`** (`net8.0`, shared, referenced by App **and** Tests):
  Models (MeshData, BoundaryCondition, Load, MaterialModel, ResultModels),
  pure Services (Export, ToolValidator, Verification, ModelDiagnostics),
  Numerics (PrincipalStress).
- **`src/FEASolver.Scripts`** (Python 3.11+, gmsh + numpy): `mesh_step.py`,
  `write_inp.py`, `parse_frd.py`, `verify_*.py`, and `utils/` (gmsh_utils,
  inp_writer, frd_reader, models, mesh_io, mesh_cache).
- **Tests**: `tests/FEASolver.Tests` (xUnit, references Core) and
  `tests/scripts_tests` (pytest).

## Data flow

```
STEP → mesh_step.py → mesh_data.json → MeshData (C#)
     → user assigns BC/Load on faces → FEAModel JSON
     → write_inp.py → model.inp → ccx → model.frd
     → parse_frd.py → results.json → ResultSet (C#) → HelixToolkit colormap
```

C# ↔ Python is **file-based JSON IPC + subprocess** (no `shell=True`,
`UseShellExecute=false`, explicit exe paths).

## Key conventions

- 1-based node/element IDs (CalculiX). HelixToolkit Y-up vs CalculiX Z-up.
- Face IDs = Gmsh physical surface tags. CalculiX tet face order S1..S4 =
  corner indices `[0,1,2] [0,1,3] [1,2,3] [0,2,3]`.
- C3D10 consistent surface lumping: corners 0, midsides A/3 (not naïve 1/N).
- Gmsh TET10 → CCX C3D10 midside swap (slots 8↔9).
- Torque distributed as an r²-weighted nodal force couple:
  `F_i = (T/D)(a × r⊥_i)`, `D = Σ|r⊥_i|²`.
- SI internally; convert only at the UI boundary (no bbox unit-guessing).

## Tests / CI

`tests/scripts_tests` (pytest) + `tests/FEASolver.Tests` (xUnit). CI runs both
on `windows-latest` (`.github/workflows/ci.yml`): C# `dotnet build/test -c
Release` and Python `pytest` + a `verify_solver.py --no-solve` smoke. See
`ship_readiness.md` for current counts and the NX-Nastran gap analysis.
