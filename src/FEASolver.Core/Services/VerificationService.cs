using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;

namespace FEASolver.Core.Services;

/// <summary>
/// Solver-free verification. Invokes verify_synthetic.py which:
///   1. Builds a steel cantilever through the real Gmsh pipeline
///   2. Writes the INP through the real InpWriterV2 (tests mapping + distribution)
///   3. Injects a synthetic σ/ε/u field matching closed-form axial tension
///   4. Exercises the result-JSON schema, CSV export, and surface-triangle rebuild
///
/// Returns a per-subsystem pass/fail report so a failing subsystem can be
/// identified without ever running ccx.exe. Used in two places:
///   • CI / dev loop — validate no regression after a backend change
///   • Support path — "heatmap looks wrong" → run this, read diagnostics
/// </summary>
public sealed class VerificationService
{
    private readonly IConfigService _config;
    public VerificationService(IConfigService config) => _config = config;

    public record VerificationReport(
        [property: JsonProperty("mappingOK")]          bool MappingOK,
        [property: JsonProperty("loadDistributionOK")] bool LoadDistributionOK,
        [property: JsonProperty("stressOK")]           bool StressOK,
        [property: JsonProperty("strainOK")]           bool StrainOK,
        [property: JsonProperty("heatmapOK")]          bool HeatmapOK,
        [property: JsonProperty("geometryOK")]         bool GeometryOK,
        [property: JsonProperty("csvOK")]              bool CsvOK,
        [property: JsonProperty("allPassed")]          bool AllPassed,
        [property: JsonProperty("diagnostics")]        List<string> Diagnostics,
        string Stdout,
        string Stderr);

    public async Task<VerificationReport> RunSyntheticTensionVerificationAsync(
        double elementSize = 0.020,
        CancellationToken ct = default)
    {
        string workDir = _config.CreateWorkDir("verify_synth");
        string reportPath = Path.Combine(workDir, "verify_report.json");
        string script = _config.ResolveScript("verify_synthetic.py");

        var psi = new ProcessStartInfo
        {
            FileName = _config.Paths.PythonExe,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--element-size");
        psi.ArgumentList.Add(elementSize.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--work-dir");
        psi.ArgumentList.Add(workDir);
        psi.ArgumentList.Add("--report-json");
        psi.ArgumentList.Add(reportPath);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch python for verify_synthetic.py");
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        VerificationReport report;
        VerificationReport? parsed = null;
        if (File.Exists(reportPath))
        {
            string json = await File.ReadAllTextAsync(reportPath, ct);
            parsed = JsonConvert.DeserializeObject<VerificationReport>(json,
                new JsonSerializerSettings
                {
                    ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
                });
        }
        if (parsed is not null)
        {
            report = parsed with { Stdout = stdout.ToString(), Stderr = stderr.ToString() };
        }
        else
        {
            report = new VerificationReport(
                false, false, false, false, false, false, false, false,
                new List<string> {
                    File.Exists(reportPath)
                        ? $"verify_synthetic.py produced an unparseable report (exit {proc.ExitCode})."
                        : $"verify_synthetic.py did not produce a report (exit {proc.ExitCode}).",
                    stderr.ToString().Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? ""
                },
                stdout.ToString(),
                stderr.ToString());
        }

        return report;
    }
}
