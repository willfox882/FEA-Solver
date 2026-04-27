using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using FEASolver.Core.Models;
using FEASolver.Core.Services;
using FEASolver.Services;
using FEASolver.Views;
using Serilog;

namespace FEASolver.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly PipelineService _pipeline;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _meshStats = "";
    [ObservableProperty] private double _progress = 0;
    [ObservableProperty] private Visibility _progressVisible = Visibility.Collapsed;
    [ObservableProperty] private Visibility _cancelVisible = Visibility.Collapsed;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportResultsCommand))]
    private bool _hasResults = false;
    [ObservableProperty] private int _selectedTabIndex = 0;
    [ObservableProperty] private string _lastErrorDetail = "";
    [ObservableProperty] private Visibility _errorPanelVisible = Visibility.Collapsed;
    [ObservableProperty] private double _meshElementSizeMm = 5.0;

    /// <summary>
    /// Length unit declared by the user for the source STEP file ("mm" or "m").
    /// Bound to the toolbar combobox; flows through to Meshing.InputUnits and
    /// the Python script's --input-units flag. Wrong value silently produces
    /// ~10^6× stress errors, so it must be explicit (no auto-detection).
    /// </summary>
    public string StepInputUnits
    {
        get => _config.Meshing.InputUnits;
        set
        {
            string v = (value ?? "mm").Trim().ToLowerInvariant();
            if (v is not ("mm" or "m")) return;
            if (_config.Meshing.InputUnits == v) return;
            _config.Meshing.InputUnits = v;
            OnPropertyChanged();
        }
    }

    public ModelTreeViewModel ModelTree { get; } = new();
    public ViewportViewModel Viewport { get; } = new();
    public PropertiesViewModel Properties { get; } = new();
    public ResultsViewModel Results { get; } = new();

    public MainViewModel()
    {
        _config = new ConfigService();
        _pipeline = new PipelineService(_config);

        // Viewport → properties panel
        Viewport.FaceSelectionChanged += OnFaceSelectionChanged;

        // Properties rejected-apply feedback → error panel
        Properties.ApplyError += msg => ShowError("Cannot apply", msg);

        // BC collection changes → update visuals + solver button + model tree
        Properties.BoundaryConditions.CollectionChanged += (_, _) =>
        {
            Viewport.UpdateConstraintMarkers(Properties.BoundaryConditions);
            ModelTree.SyncBoundaryConditions(Properties.BoundaryConditions);
            RunSolverCommand.NotifyCanExecuteChanged();
        };

        // Load collection changes → update visuals + solver button + model tree
        Properties.Loads.CollectionChanged += (_, _) =>
        {
            Viewport.UpdateLoadArrows(Properties.Loads);
            ModelTree.SyncLoads(Properties.Loads);
            RunSolverCommand.NotifyCanExecuteChanged();
        };

        // Model tree delete requests → remove from properties
        ModelTree.DeleteBcRequested   += bc   => Properties.BoundaryConditions.Remove(bc);
        ModelTree.DeleteLoadRequested += load => Properties.Loads.Remove(load);

        // Model tree selection → highlight face in viewport + update properties panel
        ModelTree.FaceHighlightRequested += faceId =>
        {
            Viewport.SelectFace(faceId);
            Properties.SelectedFaceIds = [faceId];
        };
    }

    /// <summary>
    /// Clears all BCs, loads, results, and mesh, then opens an Import STEP dialog.
    /// Equivalent to starting completely fresh without restarting the application.
    /// </summary>
    [RelayCommand]
    private void NewModel()
    {
        if (Viewport.MeshLoaded)
        {
            var ans = MessageBox.Show(
                "Clear all boundary conditions, loads, and results and open a new STEP file?",
                "New Model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return;
        }
        ResetModelState();
        ImportStep();  // immediately prompt for the new STEP file
    }

    [RelayCommand]
    private void ImportStep()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import STEP File",
            Filter = "STEP Files (*.step;*.stp)|*.step;*.stp|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        _pipeline.SetStepPath(dlg.FileName);
        StatusText = "STEP loaded — set element size and click Mesh.";
        MeshCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Clears loads and BCs on the current mesh so the user can define a new
    /// load case and re-run without re-importing or re-meshing.
    /// </summary>
    [RelayCommand]
    private void ResetLoads()
    {
        if (Properties.BoundaryConditions.Count == 0 && Properties.Loads.Count == 0) return;
        var ans = MessageBox.Show(
            "Clear all boundary conditions and loads?\n" +
            "(Mesh is kept — click Run Solver after setting new loads.)",
            "Reset Loads",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ans != MessageBoxResult.Yes) return;

        Properties.BoundaryConditions.Clear();
        Properties.Loads.Clear();
        HasResults = false;
        SelectedTabIndex = 0;
        StatusText = "Loads cleared — assign new boundary conditions and loads.";
        ClearError();
    }

    /// <summary>Navigates back to the Geometry tab (e.g. after viewing results).</summary>
    [RelayCommand]
    private void GoToGeometry()
    {
        SelectedTabIndex = 0;
    }

    private void ResetModelState()
    {
        Properties.BoundaryConditions.Clear();
        Properties.Loads.Clear();
        Viewport.ClearMesh();
        // Clear model tree
        ModelTree.RootNodes.Clear();
        // Clear results
        Results.CurrentResults = null;
        Results.ResultModel    = null;
        Results.ProbeText      = "";
        Results.RenderError    = "";
        HasResults = false;
        SelectedTabIndex = 0;
        MeshStats = "";
        StatusText = "Ready";
        ClearError();
        MeshCommand.NotifyCanExecuteChanged();
        RunSolverCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMesh))]
    private async Task MeshAsync()
    {
        BeginOperation("Meshing STEP file...");
        try
        {
            var meshData = await _pipeline.ImportAndMeshAsync(
                _pipeline.CurrentStepPath!,
                p => Progress = 10 + p * 50,
                _cts!.Token,
                MeshElementSizeMm);

            Viewport.LoadMesh(meshData);
            ModelTree.LoadMesh(meshData);
            // Wire the new mesh into Properties so BC/Load assignments can
            // resolve node sets immediately and validate face IDs.
            Properties.CurrentMesh = meshData;
            // Drop any stale BCs/Loads whose face IDs no longer exist after remesh.
            PruneStaleAssignments(meshData);
            // Re-sync surviving BCs/Loads into the freshly built tree
            ModelTree.SyncBoundaryConditions(Properties.BoundaryConditions);
            ModelTree.SyncLoads(Properties.Loads);
            Viewport.UpdateConstraintMarkers(Properties.BoundaryConditions);
            Viewport.UpdateLoadArrows(Properties.Loads);
            MeshStats = meshData.GetStats().Summary;
            StatusText = "Mesh ready.";
            Progress = 60;
            RunSolverCommand.NotifyCanExecuteChanged();
            ClearError();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Meshing cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mesh failed");
            ShowError("Meshing failed", ex.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    private bool CanMesh() => !string.IsNullOrEmpty(_pipeline.CurrentStepPath);

    [RelayCommand(CanExecute = nameof(CanRunSolver))]
    private async Task RunSolverAsync()
    {
        BeginOperation("Running analysis...");
        try
        {
            var feaModel = BuildFEAModel();

            // Pre-solve sanity checks — catch zero-load, free DOFs, bad units
            // BEFORE we spend 30s in CalculiX only to get a useless result.
            var pre = ModelDiagnostics.CheckPreSolve(feaModel);
            foreach (var issue in pre)
                Log.Information("[pre-solve {Code}] {Msg}", issue.Code, issue.Message);
            var preErr = pre.FirstOrDefault(i => i.Level == ModelDiagnostics.Severity.Error);
            if (preErr is not null)
            {
                ShowError("Cannot solve", preErr.Message);
                return;
            }

            var results = await _pipeline.RunSolverAsync(
                feaModel,
                p => Progress = 60 + p * 38,
                _cts!.Token);

            // Check for empty results before loading
            bool hasDisp = results.DisplacementMag?.NodeIds is { Length: > 0 };
            bool hasStress = results.VonMises?.NodeIds is { Length: > 0 };

            if (!hasDisp && !hasStress)
            {
                Log.Warning("Solver returned empty results — no displacement or stress data");
                ShowError("No results",
                    "CalculiX ran but produced no displacement or stress data.\n" +
                    "The .frd file may be empty, binary, or in an unexpected format.\n" +
                    "Check that the model constraints and loads are valid.");
                return;
            }

            // Post-solve diagnostics: equilibrium, trivial-solution, etc.
            var post = ModelDiagnostics.CheckPostSolve(feaModel, results);
            foreach (var issue in post)
                Log.Warning("[post-solve {Code}] {Msg}", issue.Code, issue.Message);
            // Surface the most severe post-solve issue so the user sees it
            // without digging into the log file.
            var worst = post
                .OrderByDescending(i => i.Level)
                .FirstOrDefault();
            if (worst is { Level: ModelDiagnostics.Severity.Error })
            {
                ShowError("Solver result rejected", worst.Message);
                return;
            }
            if (worst is { Level: ModelDiagnostics.Severity.Warning })
                ShowError("Solver warning", worst.Message);

            try
            {
                Results.LoadResults(results, Viewport.CurrentMesh);
            }
            catch (Exception vizEx)
            {
                Log.Error(vizEx, "Results visualization failed");
                // Still mark results as available so CSV export works
            }

            HasResults = true;
            // Auto-switch to Results tab so the user sees the heatmap
            // immediately rather than having to click the tab header.
            SelectedTabIndex = 1;
            int dispCount = results.DisplacementMag?.NodeIds?.Length ?? 0;
            int stressCount = results.VonMises?.NodeIds?.Length ?? 0;
            StatusText = $"Analysis complete — {dispCount} disp nodes, {stressCount} stress nodes.";
            Progress = 100;
            ClearError();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Solver failed");
            ShowError("Solver failed", ex.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    private bool CanRunSolver() =>
        Viewport.MeshLoaded &&
        Properties.BoundaryConditions.Count > 0 &&
        Properties.Loads.Count > 0;

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportResultsAsync()
    {
        if (Results.CurrentResults == null || Viewport.CurrentMesh == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Results CSV",
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = "fea_results.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await ExportService.WriteResultsCsvAsync(
                dlg.FileName, Results.CurrentResults, Viewport.CurrentMesh);
            StatusText = $"Results exported: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            ShowError("Export failed", ex.Message);
        }
    }

    private bool CanExport() => HasResults;

    [RelayCommand]
    private void DismissError() => ClearError();

    [RelayCommand]
    private void OpenConfig()
    {
        var dlg = new ConfigDialog { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private void About()
    {
        MessageBox.Show(
            "FEA Solver v0.1\nWPF + HelixToolkit + CalculiX + Gmsh\n\nLocal 3D linear static analysis.",
            "About FEA Solver",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private FEAModel BuildFEAModel() =>
        new FEAModel(
            Viewport.CurrentMesh!,
            Properties.CurrentMaterial,
            Properties.BoundaryConditions.ToArray(),
            Properties.Loads.ToArray(),
            _pipeline.WorkDir);

    private void PruneStaleAssignments(MeshData mesh)
    {
        var faceMap = mesh.BuildFaceMap();

        // Remove BCs whose face no longer exists, re-resolve NodeIds for survivors
        for (int i = Properties.BoundaryConditions.Count - 1; i >= 0; i--)
        {
            var bc = Properties.BoundaryConditions[i];
            if (!faceMap.TryGet(bc.FaceId, out var fg) || fg.NodeIds.Length == 0)
            {
                Properties.BoundaryConditions.RemoveAt(i);
                continue;
            }
            // Re-resolve NodeIds from the new mesh's face group
            Properties.BoundaryConditions[i] = bc with { NodeIds = fg.NodeIds.ToArray() };
        }

        // Remove Loads whose face no longer exists, re-resolve point load nodes
        for (int i = Properties.Loads.Count - 1; i >= 0; i--)
        {
            var load = Properties.Loads[i];
            if (!faceMap.TryGet(load.FaceId, out var fg))
            {
                Properties.Loads.RemoveAt(i);
                continue;
            }
            if (load.Type is LoadType.Pressure or LoadType.SurfaceTraction)
            {
                if (fg.ElementFaces.Length == 0) { Properties.Loads.RemoveAt(i); continue; }
            }
            else if (fg.NodeIds.Length == 0)
            {
                Properties.Loads.RemoveAt(i);
                continue;
            }
            // Re-resolve nearest node for point loads
            if (load.Type is LoadType.PointLoad && load.Localized is { } loc)
            {
                var xyz = loc.Xyz;
                double tx = xyz is { Length: >= 3 } ? xyz[0] : 0;
                double ty = xyz is { Length: >= 3 } ? xyz[1] : 0;
                double tz = xyz is { Length: >= 3 } ? xyz[2] : 0;
                var nearest = mesh.NearestNodeOnFace(load.FaceId, tx, ty, tz);
                int resolvedId = nearest?.Id ?? fg.NodeIds[0];
                Properties.Loads[i] = load with { Localized = loc with { ResolvedNodeId = resolvedId } };
            }
        }
    }

    private void OnFaceSelectionChanged(object? sender, IReadOnlyList<int> selectedFaceIds) =>
        Properties.SelectedFaceIds = selectedFaceIds;

    private void BeginOperation(string status)
    {
        _cts = new CancellationTokenSource();
        StatusText = status;
        ProgressVisible = Visibility.Visible;
        CancelVisible = Visibility.Visible;
        Progress = 0;
    }

    private void EndOperation()
    {
        ProgressVisible = Visibility.Collapsed;
        CancelVisible = Visibility.Collapsed;
        _cts?.Dispose();
        _cts = null;
    }

    private void ShowError(string title, string detail)
    {
        StatusText = title;
        LastErrorDetail = detail;
        ErrorPanelVisible = Visibility.Visible;
    }

    private void ClearError()
    {
        LastErrorDetail = "";
        ErrorPanelVisible = Visibility.Collapsed;
    }
}
