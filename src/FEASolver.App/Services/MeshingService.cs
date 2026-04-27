using System.IO;
using FEASolver.Core.Models;
using FEASolver.Core.Services;
using Newtonsoft.Json;
using Serilog;
using System.Diagnostics;

namespace FEASolver.Services;

public class MeshingService(ConfigService config, MeshCacheService? cache = null)
{
    private readonly ConfigService _config = config;
    private readonly MeshCacheService _cache = cache ?? new MeshCacheService();

    /// <summary>
    /// Calls mesh_step.py: STEP → mesh_data.json → MeshData.
    /// Uses hash-based cache; pass forceRemesh=true to bypass.
    /// </summary>
    public Task<MeshData> MeshStepAsync(
        string stepPath,
        string workDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        double? elementSizeMm = null)
        => MeshStepCoreAsync(stepPath, workDir, forceRemesh: false, progress, ct, elementSizeMm);

    public Task<MeshData> RemeshAsync(
        string stepPath,
        string workDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        double? elementSizeMm = null)
        => MeshStepCoreAsync(stepPath, workDir, forceRemesh: true, progress, ct, elementSizeMm);

    private async Task<MeshData> MeshStepCoreAsync(
        string stepPath,
        string workDir,
        bool forceRemesh,
        IProgress<double>? progress,
        CancellationToken ct,
        double? elementSizeMm = null)
    {
        ValidateStepPath(stepPath);

        string script = _config.ResolveScript("mesh_step.py");
        string meshJsonPath = Path.Combine(workDir, "mesh_data.json");

        double resolvedSize = elementSizeMm.HasValue
            ? elementSizeMm.Value
            : _config.Meshing.DefaultElementSize;

        string units = (_config.Meshing.InputUnits ?? "mm").ToLowerInvariant();
        if (units is not ("mm" or "m"))
            throw new MeshingException(
                $"Invalid Meshing.InputUnits '{units}'. Expected 'mm' or 'm'.");

        string args = $"\"{script}\" " +
                      $"--step \"{stepPath}\" " +
                      $"--output-dir \"{workDir}\" " +
                      $"--element-size {resolvedSize.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                      $"--order {_config.Meshing.ElementOrder} " +
                      $"--algorithm {_config.Meshing.Algorithm3D} " +
                      $"--input-units {units}" +
                      (forceRemesh ? " --no-cache" : "");

        // C#-side cache check — avoids launching Python process entirely.
        // Key MUST include the actual element size used (not just default) and
        // the input units, otherwise re-meshes with different params silently
        // return stale geometry.
        if (!forceRemesh && _cache.IsValid(workDir, stepPath, _config.Meshing, resolvedSize, units))
        {
            string cachedJson = Path.Combine(workDir, "mesh_data.json");
            if (File.Exists(cachedJson))
            {
                Log.Information("Mesh cache hit — loading {Json}", cachedJson);
                string cachedData = await File.ReadAllTextAsync(cachedJson, ct);
                var cached = JsonConvert.DeserializeObject<MeshData>(cachedData)
                    ?? throw new MeshingException("Cached mesh_data.json is invalid.");
                progress?.Report(1.0);
                return cached;
            }
        }

        Log.Information("Meshing: {Script}", script);
        var sw = Stopwatch.StartNew();

        var (stdout, stderr, exitCode) = await RunPythonAsync(args, workDir, ct);

        if (!string.IsNullOrWhiteSpace(stdout))
            Log.Debug("[mesh_step] {Out}", stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            Log.Information("[mesh_step] {Err}", stderr.TrimEnd());  // INFO: Gmsh logs to stderr

        if (exitCode != 0)
            throw new MeshingException(
                $"Meshing script failed (exit {exitCode}):\n{stderr}");

        if (!File.Exists(meshJsonPath))
            throw new MeshingException(
                $"mesh_data.json not produced. Check script path: {script}");

        progress?.Report(0.9);

        string json = await File.ReadAllTextAsync(meshJsonPath, ct);
        var meshData = JsonConvert.DeserializeObject<MeshData>(json)
            ?? throw new MeshingException("mesh_data.json is empty or invalid.");

        sw.Stop();
        Log.Information(
            "Meshing complete in {Elapsed:F1}s: {Nodes} nodes, {Elements} elements, {Faces} faces",
            sw.Elapsed.TotalSeconds,
            meshData.Nodes.Length, meshData.Elements.Length, meshData.FaceGroups.Length);

        ValidateMeshData(meshData);
        progress?.Report(1.0);
        return meshData;
    }

    private static void ValidateMeshData(MeshData mesh)
    {
        if (mesh.Nodes.Length == 0)
            throw new MeshingException("Mesh contains no nodes.");
        if (mesh.Elements.Length == 0)
            throw new MeshingException("Mesh contains no elements.");
        if (mesh.FaceGroups.Length == 0)
            throw new MeshingException("Mesh contains no face groups — face selection unavailable.");
    }

    private async Task<(string stdout, string stderr, int exitCode)> RunPythonAsync(
        string args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.Paths.PythonExe,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Python process.");

        // Read stdout and stderr concurrently to avoid deadlock on full pipes
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(ct);

        return (await stdoutTask, await stderrTask, proc.ExitCode);
    }

    private static void ValidateStepPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("STEP path is empty.");

        if (!File.Exists(path))
            throw new FileNotFoundException("STEP file not found.", path);

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".step" or ".stp"))
            throw new ArgumentException(
                $"File extension '{ext}' is not a STEP file. Expected .step or .stp");

        // Path traversal guard
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) ?? "";
        if (path.Contains("..") || dir.Contains(".."))
            throw new ArgumentException("Path traversal detected in STEP path.");
    }
}

public class MeshingException(string message, Exception? inner = null)
    : Exception(message, inner);
