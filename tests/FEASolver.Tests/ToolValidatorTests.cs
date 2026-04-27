using FEASolver.Core.Services;
using Xunit;

namespace FEASolver.Tests;

// Minimal stub implementing IConfigService for unit tests
internal class StubConfig : IConfigService
{
    public AppPaths Paths { get; } = new AppPaths
    {
        CcxExe = "nonexistent/ccx.exe",
        PythonExe = "python",
        ScriptsDir = "nonexistent/scripts",
        WorkspaceRoot = Path.GetTempPath()
    };
    public MeshingSettings Meshing { get; } = new();
    public SolverSettings Solver { get; } = new();
    public string ResolveScript(string s) => Path.Combine(Paths.ScriptsDir, s);
    public string ResolveCcxExe() => Paths.CcxExe;
    public string CreateWorkDir(string n) => Path.Combine(Paths.WorkspaceRoot, n);
    public ValidationResult Validate() => new(new List<string>());
}

public class ToolValidatorTests
{
    [Fact]
    public async Task Validate_MissingCcx_ReturnsError()
    {
        var config = new StubConfig();
        var validator = new ToolValidator(config);
        var report = await validator.ValidateAllAsync();

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors,
            e => e.Contains("ccx.exe") || e.Contains("CalculiX"));
    }

    [Fact]
    public async Task Validate_MissingScriptsDir_ReturnsError()
    {
        var config = new StubConfig();
        var validator = new ToolValidator(config);
        var report = await validator.ValidateAllAsync();

        Assert.Contains(report.Errors,
            e => e.Contains("scripts") || e.Contains("Scripts") || e.Contains("nonexistent"));
    }

    [Fact]
    public void ValidationReport_IsValid_FalseWhenErrors()
    {
        var report = new ToolValidationReport(
            new List<string> { "error1" },
            new List<string>());
        Assert.False(report.IsValid);
    }

    [Fact]
    public void ValidationReport_IsValid_TrueWhenNoErrors()
    {
        var report = new ToolValidationReport(
            new List<string>(),
            new List<string>());
        Assert.True(report.IsValid);
    }
}
