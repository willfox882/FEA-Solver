namespace FEASolver.Core.Numerics;

/// <summary>
/// Principal values (eigenvalues) of a symmetric 3×3 Cauchy stress or strain
/// tensor, via the normalized-deviatoric trigonometric solution (the standard
/// stable analytic algorithm for a symmetric 3×3 matrix).
///
/// This replaces a hand-rolled characteristic-polynomial root formula that
/// returned incorrect values for any state containing shear or repeated
/// eigenvalues (only fully-distinct diagonal tensors happened to be correct).
/// Validated against numpy.linalg.eigvalsh across diagonal, sheared,
/// hydrostatic, pure-shear and compressive states.
/// </summary>
public static class PrincipalStress
{
    /// <summary>
    /// Three principal values ordered (min, mid, max) of the symmetric tensor
    ///   [ s11 s12 s13 ]
    ///   [ s12 s22 s23 ]
    ///   [ s13 s23 s33 ].
    /// </summary>
    public static (double Min, double Mid, double Max) Principals(
        double s11, double s22, double s33,
        double s12, double s23, double s13)
    {
        double q = (s11 + s22 + s33) / 3.0;            // mean (hydrostatic) part
        double p1 = s12 * s12 + s23 * s23 + s13 * s13;
        double p2 = (s11 - q) * (s11 - q)
                  + (s22 - q) * (s22 - q)
                  + (s33 - q) * (s33 - q)
                  + 2.0 * p1;

        // Fully isotropic state (A = qI): all three eigenvalues equal the mean.
        if (p2 <= 0.0)
            return (q, q, q);

        double p = Math.Sqrt(p2 / 6.0);

        // B = (A - qI) / p ;  r = det(B) / 2  ∈ [-1, 1]
        double b11 = (s11 - q) / p, b22 = (s22 - q) / p, b33 = (s33 - q) / p;
        double b12 = s12 / p, b23 = s23 / p, b13 = s13 / p;
        double detB = b11 * (b22 * b33 - b23 * b23)
                    - b12 * (b12 * b33 - b23 * b13)
                    + b13 * (b12 * b23 - b22 * b13);
        double r = Math.Clamp(detB / 2.0, -1.0, 1.0);

        double phi = Math.Acos(r) / 3.0;
        // cos is monotonically decreasing on [0, π], and phi ∈ [0, π/3],
        // so cos(phi) is the largest root and cos(phi + 2π/3) the smallest.
        double max = q + 2.0 * p * Math.Cos(phi);
        double min = q + 2.0 * p * Math.Cos(phi + 2.0 * Math.PI / 3.0);
        double mid = 3.0 * q - max - min;              // trace invariant
        return (min, mid, max);
    }

    /// <summary>Maximum principal value (σ₁).</summary>
    public static double Max(double s11, double s22, double s33,
                             double s12, double s23, double s13)
        => Principals(s11, s22, s33, s12, s23, s13).Max;

    /// <summary>Minimum principal value (σ₃).</summary>
    public static double Min(double s11, double s22, double s33,
                             double s12, double s23, double s13)
        => Principals(s11, s22, s33, s12, s23, s13).Min;
}
