# Tutorial: Apply Torque to a Square Beam

This tutorial walks through applying a torsional load to a 100 mm × 100 mm × 400 mm
square steel bar and verifying the result.

## Geometry

```
         z
         │  ← free end (load face)
    ┌────┤
    │    │   400 mm long
    │    │   100 × 100 mm cross-section
    └────┤
         │  ← fixed end (BC face)
         └──── x
```

**Expected behaviour (St. Venant torsion):**
- Maximum shear stress at the mid-face edges.
- Near-zero shear stress at the cross-section corners.
- Linear twist angle along the length.

## Steps

### 1. Import the STEP File

File > Import STEP and select your `square_beam.step`.  
Set **STEP units** to `mm` and **Element size** to `10 mm`.  
Click **Mesh**.

### 2. Assign the Fixed BC

Click the fixed end face in the viewport.  
In Properties > Boundary Conditions, choose **Fixed**, then click **Assign BC to Selected Faces**.

### 3. Apply the Torque

Click the free end face (opposite end) in the viewport.  
In Properties > Loads:
1. Set Load Type = **Torque**
2. Enter magnitude = **500 N·m**
3. Set Axis = **GlobalX** (if the beam is along X)
4. Click **Assign Load to Selected Faces**

The Loads list should show `Torque on Face N: 500 N·m`.

### 4. Run the Solver

Analysis > Run Solver (or click the toolbar button).

The solver writes a *CLOAD section with per-node tangential forces; CalculiX solves
and writes the FRD file. The Results tab opens automatically.

### 5. Interpret the Results

**Von Mises stress (expected pattern):**
- For a square cross-section, shear stress peaks at the mid-edges and is lower
  at the corners. This is the well-known Prandtl solution for rectangular sections.
- If the pattern is uniform across the cross-section, the mesh is too coarse.

**Displacement:**
- The free end rotates about the X-axis. Displacement magnitude at the corners
  of the free face is larger than at the corners of mid-sections.

**Typical values for this geometry (steel, E = 200 GPa, ν = 0.3):**

| Quantity | Approximate Value |
|----------|-------------------|
| Max Von Mises | ~20–30 MPa at mid-edge |
| Twist at free end | ~0.01–0.02 rad (0.6–1.1°) |
| Max displacement | ~1–2 mm |

Exact values depend on mesh density and CalculiX quadratic elements (C3D10).

### 6. Verify Equilibrium

The torsion equilibrium check can be run via PhysicsVerificationService in code:

```csharp
var svc = new PhysicsVerificationService();
var result = svc.RunTorsionPhysicsCheck(
    results,
    mesh,
    appliedTorqueNm: 500.0,
    axisDirection: [1.0, 0.0, 0.0],
    constrainedFaceId: fixedFaceId);

Console.WriteLine($"Equilibrium ok: {result.EquilibriumOk}");
Console.WriteLine($"Torque error:   {result.TorqueEquilibriumError * 100:G3}%");
```

Expected: `EquilibriumOk = true`, error < 5%.

### 7. Export Results

File > Export Results CSV saves displacement and stress at every node.  
Column `vonmises` gives the Von Mises stress (Pa) at each node.

## Acceptance Criteria

| Check | Expected |
|-------|----------|
| INP CLOAD lines generated | Yes |
| Moment equilibrium error | < 1×10⁻⁶ (rel) |
| Solver converges | Yes (no singular pivot warning) |
| Von Mises pattern | Peaks at mid-edges, low at corners |
| Reaction torque | ≈ −500 N·m at fixed face (5% tolerance) |

## Common Problems

**"Torque magnitude is zero"** — enter a non-zero value in the Torque field before clicking Assign.

**"Face has only N node(s)"** — the face has fewer than 3 mesh nodes. Reduce element size so more nodes are on the target face.

**"All nodes on axis (D≈0)"** — you selected GlobalX axis but the beam's end face nodes all lie on the X-axis (i.e. the face is a line in the X direction). Choose a perpendicular axis or select FaceNormal.

**Singular stiffness / large pivot warning** — the BC does not constrain rotation about the torque axis. Use Fixed or Pinned on the reaction face, not just a single RollerX.

**Von Mises is uniform across cross-section** — mesh is too coarse. Reduce element size to at least L/20 in cross-section direction.
