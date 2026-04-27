using System.IO;
using FEASolver.Core.Models;
using FEASolver.Core.Services;
using Newtonsoft.Json;
using Serilog;

namespace FEASolver.Services;

public class ResultService(ConfigService config)
{
    private readonly ConfigService _config = config;

    /// <summary>
    /// Calls parse_frd.py → results.json → ResultSet
    /// </summary>
    public async Task<ResultSet> ParseAsync(
        string frdPath,
        string workDir,
        CancellationToken ct = default)
    {
        string script = _config.ResolveScript("parse_frd.py");
        string resultsJson = Path.Combine(workDir, "results.json");

        string args = $"\"{script}\" --frd \"{frdPath}\" --output \"{resultsJson}\"";

        Log.Information("Parsing FRD: {Script}", script);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _config.Paths.PythonExe,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Python for FRD parsing.");

        string stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new Exception($"FRD parsing failed (exit {proc.ExitCode}):\n{stderr}");

        if (!File.Exists(resultsJson))
            throw new FileNotFoundException("results.json not produced.", resultsJson);

        string json = await File.ReadAllTextAsync(resultsJson, ct);
        var result = JsonConvert.DeserializeObject<ResultSet>(json)
            ?? throw new InvalidDataException("results.json is empty or invalid.");

        int dispNodes = result.DisplacementMag?.NodeIds?.Length ?? 0;
        int stressNodes = result.VonMises?.NodeIds?.Length ?? 0;
        Log.Information("Results parsed: {DispNodes} disp nodes, {StressNodes} stress nodes",
            dispNodes, stressNodes);

        if (dispNodes == 0 && stressNodes == 0)
            Log.Warning("FRD parse produced valid JSON but ALL result fields are empty");

        return result;
    }
}
