using System.IO;
using FEASolver.Core.Services;
using Microsoft.Extensions.Configuration;

namespace FEASolver.Services;

public class ConfigService : IConfigService
{
    public AppPaths Paths { get; }
    public MeshingSettings Meshing { get; }
    public SolverSettings Solver { get; }

    public ConfigService()
    {
        var cfg = App.Configuration;
        Paths = cfg.GetSection("Paths").Get<AppPaths>() ?? new AppPaths();
        Meshing = cfg.GetSection("Meshing").Get<MeshingSettings>() ?? new MeshingSettings();
        Solver = cfg.GetSection("Solver").Get<SolverSettings>() ?? new SolverSettings();
        ExpandPaths();
    }

    private void ExpandPaths()
    {
        Paths.WorkspaceRoot = Environment.ExpandEnvironmentVariables(Paths.WorkspaceRoot);
        Paths.CcxExe = ResolveRelative(Paths.CcxExe);
        Paths.ScriptsDir = ResolveRelative(Paths.ScriptsDir);
    }

    private static string ResolveRelative(string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    public string ResolveCcxExe() => Paths.CcxExe;

    public string ResolveScript(string scriptName) =>
        Path.Combine(Paths.ScriptsDir, scriptName);

    public string CreateWorkDir(string projectName)
    {
        string dir = Path.Combine(
            Paths.WorkspaceRoot,
            $"{Sanitize(projectName)}_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public ValidationResult Validate()
    {
        var errors = new List<string>();
        if (!File.Exists(Paths.CcxExe))
            errors.Add($"CalculiX not found: {Paths.CcxExe}");
        if (!Directory.Exists(Paths.ScriptsDir))
            errors.Add($"Scripts directory not found: {Paths.ScriptsDir}");
        return new ValidationResult(errors);
    }

    // Allow runtime path updates (from config dialog)
    public void SetCcxPath(string path)
    {
        Paths.CcxExe = path;
        PersistPaths();
    }

    public void SetPythonPath(string path)
    {
        Paths.PythonExe = path;
        PersistPaths();
    }

    private void PersistPaths()
    {
        // Rewrite appsettings.json with updated paths
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(settingsPath)) return;

        string json = File.ReadAllText(settingsPath);
        var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
        if (obj == null) return;

        obj["Paths"] = new
        {
            CcxExe = Paths.CcxExe,
            PythonExe = Paths.PythonExe,
            ScriptsDir = Paths.ScriptsDir,
            WorkspaceRoot = Paths.WorkspaceRoot
        };
        File.WriteAllText(settingsPath,
            Newtonsoft.Json.JsonConvert.SerializeObject(obj,
                Newtonsoft.Json.Formatting.Indented));
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
