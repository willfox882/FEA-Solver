namespace FEASolver.Core.Services;

public interface IConfigService
{
    AppPaths Paths { get; }
    MeshingSettings Meshing { get; }
    SolverSettings Solver { get; }
    string ResolveScript(string scriptName);
    string ResolveCcxExe();
    string CreateWorkDir(string projectName);
    ValidationResult Validate();
}

public class AppPaths
{
    public string CcxExe { get; set; } = "tools/ccx/ccx.exe";
    public string PythonExe { get; set; } = "python";
    public string ScriptsDir { get; set; } = "src/FEASolver.Scripts";
    public string WorkspaceRoot { get; set; } = "%APPDATA%/FEASolver/workspace";
}

public class MeshingSettings
{
    public double DefaultElementSize { get; set; } = 0.01;
    public int ElementOrder { get; set; } = 2;
    public int Algorithm3D { get; set; } = 4;

    /// <summary>
    /// Length unit of the source STEP file ("mm" or "m"). Coordinates are
    /// converted to SI metres before export. Explicit (no auto-detection),
    /// because a wrong heuristic silently produces ~10^6× stress errors.
    /// </summary>
    public string InputUnits { get; set; } = "mm";
}

public class SolverSettings
{
    public int TimeoutSeconds { get; set; } = 300;
}

public record ValidationResult(List<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
