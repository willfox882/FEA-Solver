# Applying Torque

Torque is a moment load that rotates a face about a chosen axis. In FEA Solver,
torque is distributed as equivalent nodal forces so CalculiX can solve it with
standard *CLOAD entries — no special coupling elements are needed.

## When to Use Torque

Use the **Torque** load type when:
- You want to twist a shaft, boss, or beam end about a specific axis.
- Your boundary condition constrains one face and you apply rotation to the opposite face.
- You want to verify torsional stiffness or shear-stress distribution.

## Applying Torque — Step by Step

1. **Import and mesh** your STEP file as usual.
2. **Select the load face** by clicking it in the 3D viewport.
3. In the **Properties** panel, choose **Torque** from the Load Type drop-down.
4. Enter the **torque magnitude** in N·m (positive = right-hand rule about the chosen axis).
5. Choose the **axis**:
   - `GlobalX` / `GlobalY` / `GlobalZ` — one of the three global Cartesian axes.
   - `FaceNormal` — the outward normal of the selected face (useful for end-cap twisting).
   - `Custom` — enter an arbitrary [X, Y, Z] direction; it is normalised at Apply time.
6. Click **Assign Load to Selected Faces**.

The load summary in the Loads list shows:  
`Torque · Face N · magnitude N·m`

## Axis Selection Guide

| Mode | When to Use |
|------|-------------|
| GlobalZ | Shaft aligned with Z, twist the top face |
| GlobalX | Beam along X, twist the end face |
| FaceNormal | Circular end-cap — axis = face outward normal |
| Custom | Oblique axis, e.g. helical gear mounting |

## Algorithm

The backend converts torque T (N·m) into per-node tangential forces using the
**r²-weighted** formula:

```
F_i = (T / D) · (â × r_perp_i)

where  r_perp_i = projection of (node_i − origin) perpendicular to axis â
       D        = Σ_j |r_perp_j|²
```

This ensures:
- `Σ r_perp_i × F_i = T · â` exactly (analytically, not approximately).
- Outer nodes (larger `r_perp`) receive proportionally more force, matching the
  shear-stress distribution in St. Venant torsion theory.
- Nodes on the axis (`r_perp ≈ 0`) receive zero force; the torque is carried
  entirely by off-axis nodes.

### Validation

After computing forces, the INP writer verifies:

```
|M_net − T| / |T| < 1×10⁻⁶
```

If this check fails (e.g. all nodes are on the axis), the write is rejected and
an error is shown. Fix by choosing a different axis or applying to a different face.

## Boundary Condition Requirement

Torque must be balanced by a reaction moment at the fixed face. For torsion to
be physically meaningful:
- The fixed face BC must constrain at least the **tangential** DOFs (Fixed or Pinned).
- If only a partial BC is used (e.g. RollerX), the model may be under-constrained
  and CalculiX will report large pivot warnings or singular stiffness.

## Interpretation of Results

After solving, check:
- **Von Mises** stress: should show a roughly uniform distribution in the cross-section
  for a prismatic shaft.
- **Displacement magnitude**: the free end should show larger displacement than
  the fixed end, increasing linearly along the shaft for circular sections.
- The **torsion physics check** in PhysicsVerificationService verifies that the
  sum of reaction moments at the fixed face equals the applied torque within 5%.

## Limitations

- Torque is implemented as direct nodal forces — it does not use RBE2/MPC coupling.
  This means the load is applied at the discrete mesh nodes, so mesh refinement
  affects local stress near the loaded face.
- Remote moment (applying torque at a reference point far from the loaded face) is
  not yet implemented as a rigid coupling; for those cases, apply torque directly
  to the face closest to the reference point and refine if needed.
- Very coarse meshes on the loaded face can produce non-uniform shear stress
  concentrations at the loaded end — this is a mesh-density artefact, not a
  solver error.
