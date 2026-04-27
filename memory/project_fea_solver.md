---
name: FEA Solver Project
description: Windows WPF app wrapping CalculiX + Gmsh for 3D linear static FEA
type: project
---

Windows-native WPF desktop FEA solver app. Wraps CalculiX (ccx.exe) and Gmsh (Python API).
Scope: 3D linear static structural analysis only.
Source of truth: claude.mb in repo root.

**Why:** User wants a local, interactive FEA workflow without cloud dependency or full CAE suite.
**How to apply:** All architecture decisions reference claude.mb. Stack: WPF/.NET 8 + HelixToolkit + Python 3.11 + Gmsh + CalculiX. File-based IPC (JSON + subprocess). SI units throughout.
