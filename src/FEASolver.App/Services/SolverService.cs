using System.IO;
using Serilog;
using System.Diagnostics;
using System.Text;

namespace FEASolver.Services;

public class SolverService(ConfigService config)
{
    private readonly ConfigService _config = config;

    /// <summary>
    /// Validates and runs ccx.exe on {workDir}/{inputName}.inp.
    /// Returns path to .frd file on success; throws with full diagnostics on failure.
    /// </summary>
    public async Task<string> RunAsync(
        string workDir,
        string inputName,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string ccx = _config.ResolveCcxExe();
        if (!File.Exists(ccx))
            throw new FileNotFoundException(
                $"CalculiX executable not found: {ccx}\n" +
                "Check your configuration (Tools → Configuration).");

        string inpPath = Path.Combine(workDir, inputName + ".inp");
        if (!File.Exists(inpPath))
            throw new FileNotFoundException($".inp file not found: {inpPath}");

        // Pre-flight validation
        ValidateInpFile(inpPath);

        Log.Information("Running CalculiX: {Ccx} -i {Input}", ccx, inputName);
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = ccx,
            // ArgumentList lets the framework apply correct Windows argument
            // escaping per token (audit-011). String interpolation would break if
            // inputName ever contained a space or quote; this is robust by construction.
            ArgumentList = { "-i", inputName },
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CalculiX process.");

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdoutBuf.AppendLine(e.Data);
                Log.Debug("[ccx] {Line}", e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrBuf.AppendLine(e.Data);
                Log.Warning("[ccx stderr] {Line}", e.Data);
            }
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct)
                .WaitAsync(TimeSpan.FromSeconds(_config.Solver.TimeoutSeconds), ct);
        }
        catch (TimeoutException)
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"CalculiX exceeded the {_config.Solver.TimeoutSeconds}s timeout.");
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            throw;
        }

        sw.Stop();
        string stdout = stdoutBuf.ToString();
        string stderr = stderrBuf.ToString();

        Log.Information("CalculiX finished in {Elapsed:F1}s, exit {Code}",
            sw.Elapsed.TotalSeconds, proc.ExitCode);

        if (stdout.Contains("NO CONVERGENCE", StringComparison.OrdinalIgnoreCase))
            Log.Warning("CalculiX: NO CONVERGENCE — check constraints and loads.");

        if (proc.ExitCode != 0)
        {
            // Build a diagnostic message that includes the most relevant output lines
            string diag = BuildDiagnosticMessage(proc.ExitCode, stdout, stderr, inpPath);
            throw new SolverException(diag);
        }

        string frdPath = Path.Combine(workDir, inputName + ".frd");
        if (!File.Exists(frdPath))
            throw new FileNotFoundException(
                $"CalculiX ran successfully (exit 0) but produced no .frd file.\n" +
                $"Expected: {frdPath}");

        return frdPath;
    }

    // ── Pre-flight validation ─────────────────────────────────────────────────

    private static void ValidateInpFile(string inpPath)
    {
        string content;
        try { content = File.ReadAllText(inpPath); }
        catch (Exception ex)
        {
            throw new SolverException($"Cannot read .inp file: {ex.Message}");
        }

        var errors = new List<string>();

        if (!content.Contains("*NODE", StringComparison.OrdinalIgnoreCase))
            errors.Add("No *NODE section found.");

        if (!content.Contains("*ELEMENT", StringComparison.OrdinalIgnoreCase))
            errors.Add("No *ELEMENT section found.");

        if (!content.Contains("*MATERIAL", StringComparison.OrdinalIgnoreCase))
            errors.Add("No *MATERIAL section found.");

        if (!content.Contains("*SOLID SECTION", StringComparison.OrdinalIgnoreCase))
            errors.Add("No *SOLID SECTION found — material not linked to element set.");

        if (!content.Contains("*STEP", StringComparison.OrdinalIgnoreCase))
            errors.Add("No *STEP section found.");

        bool hasBoundary = content.Contains("*BOUNDARY", StringComparison.OrdinalIgnoreCase);
        if (!hasBoundary)
            errors.Add("No *BOUNDARY conditions found — structure is unconstrained (will be singular).");

        bool hasLoad = content.Contains("*CLOAD",  StringComparison.OrdinalIgnoreCase)
                    || content.Contains("*DLOAD",  StringComparison.OrdinalIgnoreCase)
                    || content.Contains("*DSLOAD", StringComparison.OrdinalIgnoreCase);
        if (!hasLoad)
            errors.Add("No loads found (*CLOAD / *DLOAD / *DSLOAD) — no forces applied.");

        if (errors.Count > 0)
            throw new SolverException(
                "Generated .inp file is invalid:\n• " + string.Join("\n• ", errors));
    }

    // ── Diagnostic helper ─────────────────────────────────────────────────────

    private static string BuildDiagnosticMessage(
        int exitCode, string stdout, string stderr, string inpPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CalculiX failed (exit code {exitCode}).");
        sb.AppendLine($"Input file: {inpPath}");
        sb.AppendLine();

        // Pull the most useful lines from CalculiX output
        string[] relevantKeywords =
            ["error", "***", "problem", "unknown", "***warning",
             "cannot find", "no such", "singular", "non-positive"];
        var outputLines = (stdout + "\n" + stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var highlighted = outputLines
            .Where(l => relevantKeywords.Any(k =>
                l.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Take(25)
            .ToList();

        if (highlighted.Count > 0)
        {
            sb.AppendLine("CalculiX diagnostic output:");
            foreach (var line in highlighted)
                sb.AppendLine("  " + line.Trim());
        }
        else
        {
            var last = outputLines.TakeLast(30).ToList();
            if (last.Count > 0)
            {
                sb.AppendLine("CalculiX output (last lines):");
                foreach (var line in last)
                    sb.AppendLine("  " + line.Trim());
            }
        }

        // Heuristic hint about the likely-responsible BC/Load
        string joined = string.Join("\n", outputLines);
        string? hint = InferHint(joined);
        if (hint is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Hint: " + hint);
        }

        return sb.ToString().TrimEnd();
    }

    private static string? InferHint(string combined)
    {
        if (combined.Contains("set ", StringComparison.OrdinalIgnoreCase) &&
            combined.Contains("does not", StringComparison.OrdinalIgnoreCase))
            return "A referenced NSET/ELSET/SURFACE does not exist — check that BCs/Loads are mapped to the correct face.";
        if (combined.Contains("singular", StringComparison.OrdinalIgnoreCase))
            return "Stiffness matrix is singular — likely insufficient constraints (add a fixed/pinned BC to a face that restrains all rigid-body modes).";
        if (combined.Contains("non-positive", StringComparison.OrdinalIgnoreCase))
            return "Non-positive pivot — model may be unstable or material/Young's modulus is invalid.";
        if (combined.Contains("unknown dload type", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("DLOAD", StringComparison.OrdinalIgnoreCase) &&
            combined.Contains("error", StringComparison.OrdinalIgnoreCase))
            return "A distributed-load type is invalid — check your surface traction / pressure load.";
        return null;
    }
}

public class SolverException(string message, Exception? inner = null)
    : Exception(message, inner);
