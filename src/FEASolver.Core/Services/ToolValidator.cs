using System.Diagnostics;

namespace FEASolver.Core.Services;

/// <summary>
/// Validates that all required external tools are present and executable.
/// Called at startup and before pipeline execution.
/// </summary>
public class ToolValidator(IConfigService config)
{
    private readonly IConfigService _config = config;

    public async Task<ToolValidationReport> ValidateAllAsync()
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        await Task.WhenAll(
            CheckCcxAsync(errors),
            CheckPythonAsync(errors, warnings),
            Task.Run(() => CheckScriptsDir(errors)),
            Task.Run(() => CheckWorkspaceWritable(errors, warnings))
        );

        return new ToolValidationReport(errors, warnings);
    }

    private async Task CheckCcxAsync(List<string> errors)
    {
        string ccx = _config.ResolveCcxExe();
        if (!File.Exists(ccx))
        {
            errors.Add($"CalculiX not found: {ccx}\nDownload from https://calculix.de or place ccx.exe in tools/ccx/");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ccx,
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            // ccx -v may not be a valid flag; any exit is acceptable — we just verify it launches
        }
        catch (Exception ex)
        {
            errors.Add($"CalculiX found but failed to execute: {ex.Message}");
        }
    }

    private async Task CheckPythonAsync(List<string> errors, List<string> warnings)
    {
        string python = _config.Paths.PythonExe;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string version = await proc.StandardOutput.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(version))
                version = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                errors.Add($"Python not found or not executable: '{python}'");
                return;
            }

            // Check required packages
            await CheckPythonPackagesAsync(python, errors, warnings);
        }
        catch (Exception ex)
        {
            errors.Add($"Python not found: '{python}' — {ex.Message}\nInstall Python 3.11+ and ensure it's on PATH.");
        }
    }

    private static async Task CheckPythonPackagesAsync(
        string python, List<string> errors, List<string> warnings)
    {
        string checkScript = "import gmsh; import numpy; print('OK')";
        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-c \"{checkScript}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        try
        {
            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0 || !stdout.Contains("OK"))
                warnings.Add(
                    $"Python packages not fully installed.\n" +
                    $"Run: pip install -r src/FEASolver.Scripts/requirements.txt\n" +
                    $"Detail: {stderr.Trim()}");
        }
        catch { /* python already checked above */ }
    }

    private void CheckScriptsDir(List<string> errors)
    {
        string dir = _config.Paths.ScriptsDir;
        if (!Directory.Exists(dir))
        {
            errors.Add($"Scripts directory not found: {dir}");
            return;
        }
        foreach (string script in new[] { "mesh_step.py", "write_inp.py", "parse_frd.py" })
        {
            if (!File.Exists(Path.Combine(dir, script)))
                errors.Add($"Required script missing: {Path.Combine(dir, script)}");
        }
    }

    private void CheckWorkspaceWritable(List<string> errors, List<string> warnings)
    {
        string root = _config.Paths.WorkspaceRoot;
        try
        {
            Directory.CreateDirectory(root);
            string probe = Path.Combine(root, ".write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            errors.Add($"Workspace directory not writable: {root}\n{ex.Message}");
        }
    }
}

public record ToolValidationReport(List<string> Errors, List<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
    public bool HasWarnings => Warnings.Count > 0;

    public string FormatErrors() => string.Join("\n\n", Errors);
    public string FormatWarnings() => string.Join("\n", Warnings);
}
