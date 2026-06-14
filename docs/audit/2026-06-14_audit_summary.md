# FEA Solver — Audit Summary (2026-06-14)

**Mode:** read-only audit. No code changed. **Findings:** 0 critical · 2 high · 4 medium · 5 low.

## Environment & test status
- **Python suite: 164/164 pass** (`pytest tests/scripts_tests`, 3.8 s).
- **C# suite: 0 run — build fails** (`dotnet test` → `FactAttribute` not found). See HIGH-2.
- CalculiX binary not installed → end-to-end solve and the full `verify_solver.py` path were **not** executed; INP-generation and parsing logic were audited statically and via the Python tests.
- Stack drift: running Python 3.13 / numpy 2.2.6 / gmsh 4.15.2 vs documented 3.11 / 1.26.4 / 4.12.2.

## Top 5 issues

**HIGH-1 · Principal-stress solver is wrong (ResultsViewModel.cs:458 `ComputePrincipal`).**
The closed-form eigenvalue solution is mis-derived. I ported the exact C# logic to Python and compared to `numpy.linalg.eigvalsh`: for σ=(100,0,0) it returns **[-33.3, 66.7]** instead of **[0, 100]**; for a sheared tensor it returns **[8.86, 95.23]** instead of **[21.44, 107.80]**. Only diagonal tensors with three *distinct* eigenvalues come out right, which hides the bug. Max/Min Principal stress shown to users is silently incorrect whenever shear is present. A canonical normalized-deviatoric trig algorithm reproduces numpy exactly and is the recommended replacement.

**HIGH-2 · The entire C# test project doesn't compile.** `ExportServiceTests.cs` is missing `using Xunit;` (the other three test files have it), so `[Fact]` won't resolve and `FEASolver.Tests` fails to build — meaning **none** of the C# tests have been running. One-line fix; the real lesson is there is no CI to catch it.

**MEDIUM-3 · Equivalent-strain formula off by √(3/2) on normal terms (frd_reader.py:283 `_strain_vonmises`).** Uses `(1/3)Σdiff²` where the von Mises equivalent strain needs `(2/9)Σdiff²`. It returns 1.225·ε for incompressible uniaxial strain — contradicting the function's *own* docstring (which claims `=|ε_axial|`). Strain-vM is a secondary output; displacement and stress-vM are unaffected. Untested.

**MEDIUM-4 · Latent subprocess deadlock (PipelineService.cs `WriteInpAsync`).** `RedirectStandardOutput=true` but only stderr is drained; a full stdout pipe would hang the solve. Low probability today, but `MeshingService` already shows the correct both-streams pattern to copy.

**MEDIUM-5 · No CI / no linters / no lockfiles.** No `.github` workflows, no ruff/mypy/black or C# analyzers, no pinned-lock reproducibility. CI would have caught HIGH-2 on commit.

(Plus MEDIUM dependency drift and five LOW items — Poisson-ratio over-strict validation, Windows-only FRD exponent regex, missing static-analysis config, doc/test-count drift, unquoted ccx job-name arg. Full detail in the JSON.)

## What's solid (verified, not just assumed)
- Torque → nodal-force distribution: `Σ(r×F)·a = T` holds analytically.
- C3D10 consistent surface lumping (corners 0, midsides A/3) is the correct 6-node-triangle result.
- Gmsh→CalculiX TET10 midside swap (8↔9) is consistent with the INP writer's face-midside map.
- Explicit mm/m→SI handling (no fragile bbox heuristic); clean subprocess/security hygiene.

## Prioritized improvement plan (impact × effort)

| # | Task | Effort | Risk | Acceptance |
|---|------|--------|------|------------|
| 1 | Fix `ComputePrincipal` (HIGH-1) + add C# eigenvalue tests vs reference values | 2 h | low | New tests match numpy refs for diagonal/shear/hydrostatic/degenerate tensors |
| 2 | Fix C# test build (HIGH-2): add `using Xunit;` / GlobalUsings | 0.5 h | low | `dotnet test` builds and runs all suites green |
| 3 | Fix `_strain_vonmises` (MED-3) + pytest invariant | 1 h | low | Incompressible-uniaxial → \|ε\|; matches numpy deviatoric ref |
| 4 | Drain stdout+stderr concurrently in `WriteInpAsync` (MED-4) | 0.5 h | low | Both streams read via `Task.WhenAll` before `WaitForExit` |
| 5 | Add GitHub Actions CI (MED-5): dotnet build/test + pytest + ruff | 3 h | low | CI green on PR; fails on a deliberately broken test |
| 6 | Pin/lock deps + reconcile requirements.txt vs claude.mb (MED-6) | 1.5 h | low | Lock/constraints file; OSV scan job in CI |
| 7 | Relax Poisson validation to −1<ν<0.5 (LOW-7) | 0.5 h | low | ν=0 accepted; tests cover bounds |
| 8 | Relax FRD exponent regex + 2-digit fixture test (LOW-8) | 1 h | low | Parser handles 2- and 3-digit exponents |
| 9 | Add ruff + mypy + .editorconfig/analyzers (LOW-9) | 2 h | med | Clean run wired into CI |
| 10 | Add more `verify_solver.py` benchmarks (bending, torsion, pressure vessel) | 4 h | med | Each within tolerance vs analytical |
| 11 | Doc/test-count sync (LOW-10) | 0.5 h | low | Counts auto-derived or corrected |

**Recommended order:** 2 → 1 → 3 → 4 (correctness + make tests real), then 5 → 6 (gates), then the rest.

---
Artifacts: `docs/audit/2026-06-14_audit_summary.json` (machine-readable) and this file. **No code modified.** Awaiting `proceed` to begin Task 2 (or your chosen first task).
