using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FEASolver.Core.Services;
using FEASolver.Services;
using Serilog;

namespace FEASolver.ViewModels;

public partial class ConfigDialogViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly ToolValidator _validator;

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty] private string _ccxPath = "";
    [ObservableProperty] private string _pythonPath = "";
    [ObservableProperty] private string _scriptsDir = "";
    [ObservableProperty] private string _workspaceRoot = "";
    [ObservableProperty] private string _validationMessage = "";

    public ConfigDialogViewModel()
    {
        _config = new ConfigService();
        _validator = new ToolValidator(_config);

        CcxPath = _config.Paths.CcxExe;
        PythonPath = _config.Paths.PythonExe;
        ScriptsDir = _config.Paths.ScriptsDir;
        WorkspaceRoot = _config.Paths.WorkspaceRoot;
    }

    [RelayCommand]
    private void BrowseCcx()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate CalculiX Executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) CcxPath = dlg.FileName;
    }

    [RelayCommand]
    private void BrowsePython()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate Python Executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) PythonPath = dlg.FileName;
    }

    [RelayCommand]
    private void BrowseScriptsDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Scripts Directory",
            InitialDirectory = ScriptsDir
        };
        if (dlg.ShowDialog() == true)
            ScriptsDir = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseWorkspace()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Workspace Root",
            InitialDirectory = WorkspaceRoot
        };
        if (dlg.ShowDialog() == true)
            WorkspaceRoot = dlg.FolderName;
    }

    [RelayCommand]
    private async Task Validate()
    {
        ApplyToConfig();
        var report = await _validator.ValidateAllAsync();

        if (report.IsValid && !report.HasWarnings)
            ValidationMessage = "All tools validated successfully.";
        else
        {
            var parts = new List<string>();
            if (!report.IsValid) parts.AddRange(report.Errors);
            if (report.HasWarnings) parts.Add("Warnings:\n" + report.FormatWarnings());
            ValidationMessage = string.Join("\n\n", parts);
        }
    }

    [RelayCommand]
    private void Save()
    {
        ApplyToConfig();
        _config.SetCcxPath(CcxPath);
        _config.SetPythonPath(PythonPath);
        Log.Information("Config saved: ccx={Ccx}, python={Python}", CcxPath, PythonPath);
        CloseRequested?.Invoke(this, true);
    }

    private void ApplyToConfig()
    {
        _config.Paths.CcxExe = CcxPath;
        _config.Paths.PythonExe = PythonPath;
        _config.Paths.ScriptsDir = ScriptsDir;
        _config.Paths.WorkspaceRoot = WorkspaceRoot;
    }
}
