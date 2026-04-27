using System.IO;
using FEASolver.Core.Models;
using FEASolver.Core.Services;
using Newtonsoft.Json;
using Serilog;

namespace FEASolver.Services;

/// <summary>
/// Orchestrates: ImportSTEP → Mesh → WriteINP → RunCCX → ParseFRD
/// </summary>
public class PipelineService
{
    private readonly ConfigService _config;
    private readonly MeshingService _meshing;
    private readonly SolverService _solver;
    private readonly ResultService _results;

    public string? CurrentStepPath { get; private set; }
    public string WorkDir { get; private set; } = "";

    public PipelineService(ConfigService config)
    {
        _config = config;
        _meshing = new MeshingService(config);
        _solver = new SolverService(config);
        _results = new ResultService(config);
    }

    /// <summary>Records the STEP path without meshing.</summary>
    public void SetStepPath(string stepPath)
    {
        CurrentStepPath = stepPath;
        string projectName = Path.GetFileNameWithoutExtension(stepPath);
        WorkDir = _config.CreateWorkDir(projectName);
    }

    public async Task<MeshData> ImportAndMeshAsync(
        string stepPath,
        Action<double>? progressCallback = null,
        CancellationToken ct = default,
        double? elementSizeMm = null)
    {
        CurrentStepPath = stepPath;
        string projectName = Path.GetFileNameWithoutExtension(stepPath);
        WorkDir = _config.CreateWorkDir(projectName);

        Log.Information("Pipeline: Import+Mesh started for {Step}, workDir={Dir}",
            stepPath, WorkDir);

        var progress = progressCallback != null
            ? new Progress<double>(v => progressCallback(v))
            : null;

        return await _meshing.MeshStepAsync(stepPath, WorkDir, progress, ct, elementSizeMm);
    }

    public async Task<ResultSet> RunSolverAsync(
        FEAModel model,
        Action<double>? progressCallback = null,
        CancellationToken ct = default)
    {
        const string inputName = "model";

        // Step 1a: Re-resolve BC and load node IDs from current mesh face groups.
        // This ensures node IDs are always authoritative even after a remesh.
        var resolvedBcs   = model.Mesh.ResolveBcNodes(model.BCs);
        var resolvedLoads = model.Mesh.ResolveLoadNodes(model.Loads);

        // Identify which BCs/Loads were dropped (face no longer in mesh)
        var resolvedBcFaces = resolvedBcs.Select(b => b.FaceId).ToHashSet();
        var resolvedLoadFaces = resolvedLoads.Select(l => (l.FaceId, l.Type)).ToHashSet();
        var droppedBcDetails = model.BCs
            .Where(b => !resolvedBcFaces.Contains(b.FaceId))
            .Select(b => $"{b.Type} on face {b.FaceId}")
            .ToList();
        var droppedLoadDetails = model.Loads
            .Where(l => !resolvedLoadFaces.Contains((l.FaceId, l.Type)))
            .Select(l => $"{l.Type} on face {l.FaceId}")
            .ToList();

        if (droppedBcDetails.Count > 0)
            Log.Warning("Pipeline: dropped BCs: {Details}", string.Join(", ", droppedBcDetails));
        if (droppedLoadDetails.Count > 0)
            Log.Warning("Pipeline: dropped Loads: {Details}", string.Join(", ", droppedLoadDetails));

        foreach (var bc in resolvedBcs)
            Log.Debug("BC face={F} type={T} resolved_nodes={N}",
                bc.FaceId, bc.Type, bc.NodeIds.Length);
        foreach (var ld in resolvedLoads)
            Log.Debug("Load face={F} type={T} resolved_node={N}",
                ld.FaceId, ld.Type, ld.Localized?.ResolvedNodeId);

        var resolvedModel = model with { BCs = resolvedBcs, Loads = resolvedLoads };

        // Step 1b: Pre-flight model validation — surface exact user-facing errors
        var errors = resolvedModel.Mesh.ValidateModel(resolvedModel.BCs, resolvedModel.Loads);
        foreach (var detail in droppedBcDetails)
            errors.Insert(0, $"BC dropped ({detail}) — face has no mesh nodes or element-faces.");
        foreach (var detail in droppedLoadDetails)
            errors.Insert(0, $"Load dropped ({detail}) — face has no mesh entities.");
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Cannot run solver — model is invalid:\n• " + string.Join("\n• ", errors));

        Log.Information("Pipeline: Writing .inp");
        progressCallback?.Invoke(0.1);
        await WriteInpAsync(resolvedModel, inputName, ct);

        // Step 2: Run ccx
        Log.Information("Pipeline: Running CalculiX");
        progressCallback?.Invoke(0.3);
        string frdPath = await _solver.RunAsync(WorkDir, inputName, null, ct);

        // Step 3: Parse .frd
        Log.Information("Pipeline: Parsing results");
        progressCallback?.Invoke(0.85);
        var results = await _results.ParseAsync(frdPath, WorkDir, ct);

        progressCallback?.Invoke(1.0);
        return results;
    }

    private async Task WriteInpAsync(FEAModel model, string inputName, CancellationToken ct)
    {
        string modelJson = Path.Combine(WorkDir, "fea_model.json");
        string json = JsonConvert.SerializeObject(model, Formatting.Indented);
        await File.WriteAllTextAsync(modelJson, json, ct);

        string script = _config.ResolveScript("write_inp.py");
        string outInp = Path.Combine(WorkDir, inputName + ".inp");

        string args = $"\"{script}\" " +
                      $"--model \"{modelJson}\" " +
                      $"--output \"{outInp}\"";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _config.Paths.PythonExe,
            Arguments = args,
            WorkingDirectory = WorkDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Python for INP writing.");

        string stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new Exception($"INP writer failed (exit {proc.ExitCode}):\n{stderr}");

        if (!File.Exists(outInp))
            throw new FileNotFoundException("model.inp not produced.", outInp);

        Log.Information("Pipeline: .inp written to {Path}", outInp);
    }
}
