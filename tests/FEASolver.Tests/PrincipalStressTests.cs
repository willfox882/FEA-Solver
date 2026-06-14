using FEASolver.Core.Numerics;
using Xunit;

namespace FEASolver.Tests;

/// <summary>
/// Regression tests for AUDIT-001: the previous principal-stress solver
/// returned wrong eigenvalues for any tensor with shear or repeated roots
/// (e.g. (100,0,0) → [-33.3, 66.7] instead of [0, 100]). Reference values
/// below are from numpy.linalg.eigvalsh.
/// </summary>
public class PrincipalStressTests
{
    private const double Tol = 1e-6;

    [Theory]
    // s11, s22, s33, s12, s23, s13,  expectedMin, expectedMid, expectedMax
    [InlineData(100, 0, 0, 0, 0, 0,      0.0,            0.0,           100.0)]            // repeated roots — old code gave [-33.3, 66.7]
    [InlineData(50, 30, 10, 0, 0, 0,     10.0,           30.0,          50.0)]             // diagonal distinct (old code's only correct case)
    [InlineData(100, 50, 25, 20, 10, 5,  21.4368948323,  45.7602219743, 107.8028831934)]  // general sheared state
    [InlineData(-40, -10, -70, 5, -3, 8, -72.2751735757, -38.5843314972, -9.1404949270)]  // compressive sheared
    [InlineData(5, 5, 5, 0, 0, 0,        5.0,            5.0,           5.0)]              // hydrostatic
    [InlineData(0, 0, 0, 7, 0, 0,        -7.0,           0.0,           7.0)]              // pure shear
    public void Principals_MatchReferenceEigenvalues(
        double s11, double s22, double s33, double s12, double s23, double s13,
        double expMin, double expMid, double expMax)
    {
        var (min, mid, max) = PrincipalStress.Principals(s11, s22, s33, s12, s23, s13);
        Assert.Equal(expMin, min, Tol);
        Assert.Equal(expMid, mid, Tol);
        Assert.Equal(expMax, max, Tol);
    }

    [Theory]
    [InlineData(100, 50, 25, 20, 10, 5)]
    [InlineData(-40, -10, -70, 5, -3, 8)]
    [InlineData(1.0e8, -3.0e7, 2.5e7, 1.2e7, -8.0e6, 4.0e6)]   // realistic Pa magnitudes
    public void Principals_AreOrderedAndTracePreserving(
        double s11, double s22, double s33, double s12, double s23, double s13)
    {
        var (min, mid, max) = PrincipalStress.Principals(s11, s22, s33, s12, s23, s13);

        Assert.True(min <= mid + Tol);
        Assert.True(mid <= max + Tol);

        // Σ principals == trace (first invariant)
        double trace = s11 + s22 + s33;
        double relTol = 1e-9 * System.Math.Max(1.0, System.Math.Abs(trace));
        Assert.Equal(trace, min + mid + max, relTol);
    }

    [Fact]
    public void MaxAndMin_Helpers_AgreeWithPrincipals()
    {
        var (min, _, max) = PrincipalStress.Principals(100, 50, 25, 20, 10, 5);
        Assert.Equal(max, PrincipalStress.Max(100, 50, 25, 20, 10, 5), Tol);
        Assert.Equal(min, PrincipalStress.Min(100, 50, 25, 20, 10, 5), Tol);
    }
}
