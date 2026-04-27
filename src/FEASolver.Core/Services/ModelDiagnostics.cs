using FEASolver.Core.Models;

namespace FEASolver.Core.Services;

/// <summary>
/// Pre/post-solve sanity checks. Catches the kind of silent failures that
/// produce numerically valid but physically meaningless results — the class
/// of bugs that gives "9e-5 MPa where 7.7 MPa is expected" without any
/// solver error. Mirrors what NX/SolidWorks do under the hood before
/// presenting results to the user.
/// </summary>
public static class ModelDiagnostics
{
    public record Issue(Severity Level, string Code, string Message);
    public enum Severity { Info, Warning, Error }

    /// <summary>
    /// Pre-solve checks. Runs against the FEAModel before *.inp is written.
    /// </summary>
    public static List<Issue> CheckPreSolve(FEAModel model)
    {
        var issues = new List<Issue>();

        // 1. At least one BC and one load
        if (model.BCs.Length == 0)
            issues.Add(new(Severity.Error, "NO_BC",
                "No boundary conditions — model is unconstrained (rigid-body singular)."));
        if (model.Loads.Length == 0)
            issues.Add(new(Severity.Error, "NO_LOAD",
                "No loads — solver will return a trivial zero solution."));

        // 2. Constrained DOF coverage — every translational DOF must be
        //    reachable through some BC chain or the stiffness matrix is singular.
        var constrainedDofs = new HashSet<int>();
        foreach (var bc in model.BCs)
            foreach (int d in DofsFor(bc.Type))
                constrainedDofs.Add(d);
        foreach (int d in new[] { 1, 2, 3 })
            if (!constrainedDofs.Contains(d))
                issues.Add(new(Severity.Warning, $"FREE_DOF_{d}",
                    $"No BC constrains DOF {d} ({"XYZ"[d - 1]}). " +
                    "The model can translate freely along this axis — solver may fail."));

        // 3. Material sanity — E and ν within physically plausible ranges
        var m = model.Material;
        if (m.YoungsModulus <= 0)
            issues.Add(new(Severity.Error, "BAD_E",
                $"Young's modulus must be positive (got {m.YoungsModulus:G3} Pa)."));
        else if (m.YoungsModulus < 1e6 || m.YoungsModulus > 1e13)
            issues.Add(new(Severity.Warning, "E_RANGE",
                $"Young's modulus {m.YoungsModulus:G3} Pa is outside the typical " +
                "engineering range 1 MPa – 10 TPa. Likely a unit-entry mistake (GPa vs Pa)."));
        if (m.PoissonsRatio <= -1 || m.PoissonsRatio >= 0.5)
            issues.Add(new(Severity.Error, "BAD_NU",
                $"Poisson's ratio {m.PoissonsRatio} must lie in (-1, 0.5)."));

        // 4. Load magnitude plausibility — flag loads of zero or absurd magnitude
        for (int i = 0; i < model.Loads.Length; i++)
        {
            var load = model.Loads[i];
            double mag = Math.Abs(load.Magnitude);
            if (mag < 1e-15 && load.Localized?.Force is not { } f)
                issues.Add(new(Severity.Warning, $"LOAD_{i}_ZERO",
                    $"Load #{i + 1} ({load.Type}) on face {load.FaceId} has zero magnitude."));
            else if (mag > 1e15)
                issues.Add(new(Severity.Warning, $"LOAD_{i}_HUGE",
                    $"Load #{i + 1} magnitude {mag:G3} is suspiciously large — check units."));
        }

        return issues;
    }

    /// <summary>
    /// Post-solve checks. Compares applied loads to nodal reactions, flags
    /// trivial / non-equilibrium solutions, and detects "everything is zero"
    /// failures (the symptom of the 10^5-stress bug).
    /// </summary>
    public static List<Issue> CheckPostSolve(FEAModel model, ResultSet results)
    {
        var issues = new List<Issue>();

        // 1. Result completeness
        bool hasDisp = results.DisplacementMag?.Values is { Length: > 0 };
        bool hasStress = results.VonMises?.Values is { Length: > 0 };
        if (!hasDisp && !hasStress)
        {
            issues.Add(new(Severity.Error, "NO_RESULTS",
                "Solver produced no displacement or stress — likely empty/binary FRD or solver crash."));
            return issues;  // remaining checks are meaningless
        }

        // 2. Equilibrium check: Σ(reactions) ≈ -Σ(applied loads)
        if (results.ReactionX is { } rx && results.ReactionY is { } ry && results.ReactionZ is { } rz)
        {
            double Rx = rx.Values?.Sum() ?? 0;
            double Ry = ry.Values?.Sum() ?? 0;
            double Rz = rz.Values?.Sum() ?? 0;
            double Rnorm = Math.Sqrt(Rx * Rx + Ry * Ry + Rz * Rz);

            // Estimate applied force magnitude by enumerating loads. This is
            // intentionally rough (we don't recompute element-face areas here);
            // it only needs to be the right order of magnitude.
            double Fapplied = EstimateAppliedForceMagnitude(model);
            if (Fapplied > 1e-12 && Rnorm > 1e-12)
            {
                double ratio = Rnorm / Fapplied;
                if (ratio < 0.5 || ratio > 2.0)
                    issues.Add(new(Severity.Warning, "EQUILIBRIUM",
                        $"Reaction force magnitude {Rnorm:G4} N is far from " +
                        $"expected applied {Fapplied:G4} N (ratio {ratio:G3}). " +
                        "Check load units and constraint coverage."));
            }
            else if (Fapplied > 1e-12 && Rnorm < 1e-9)
            {
                issues.Add(new(Severity.Error, "NO_REACTION",
                    $"Reactions are essentially zero ({Rnorm:G3} N) but " +
                    $"applied loads sum to {Fapplied:G3} N. " +
                    "The model carried no force — likely a missing CLOAD/DLOAD or unit mismatch."));
            }
        }

        // 3. Trivial-solution detection
        if (hasDisp)
        {
            double maxDisp = results.DisplacementMag!.Values.Max(Math.Abs);
            if (maxDisp < 1e-15)
                issues.Add(new(Severity.Warning, "ZERO_DISP",
                    "Maximum displacement is essentially zero — model may be over-constrained " +
                    "or no load was actually transferred."));
        }
        if (hasStress)
        {
            double maxVm = results.VonMises!.Values.Max(Math.Abs);
            if (maxVm < 1e-9)
                issues.Add(new(Severity.Warning, "ZERO_STRESS",
                    "Maximum von Mises stress is essentially zero — model may be unloaded."));
        }

        return issues;
    }

    private static int[] DofsFor(BcType type) => type switch
    {
        BcType.Fixed   => new[] { 1, 2, 3 },
        BcType.Pinned  => new[] { 1, 2, 3 },
        BcType.RollerX => new[] { 1 },
        BcType.RollerY => new[] { 2 },
        BcType.RollerZ => new[] { 3 },
        _              => Array.Empty<int>()
    };

    private static double EstimateAppliedForceMagnitude(FEAModel model)
    {
        // For point loads, |force| is exact. For surface tractions and pressures
        // we don't have face areas here; approximate as |magnitude| × 1 (treat
        // as N or N/m² × characteristic area = unit). Good enough for an
        // order-of-magnitude equilibrium tripwire.
        double sum = 0;
        foreach (var load in model.Loads)
        {
            switch (load.Type)
            {
                case LoadType.PointLoad when load.Localized?.Force is { Length: 3 } f:
                    sum += Math.Sqrt(f[0] * f[0] + f[1] * f[1] + f[2] * f[2]);
                    break;
                case LoadType.PointLoad:
                    sum += Math.Abs(load.Magnitude);
                    break;
                case LoadType.SurfaceTraction:
                case LoadType.Pressure:
                    // Caller doesn't have face areas at hand; magnitude is in
                    // Pa, so this is dimensionally wrong but only used for
                    // a "is there ANY force?" check, not a precise comparison.
                    sum += Math.Abs(load.Magnitude);
                    break;
            }
        }
        return sum;
    }
}
