using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using FEASolver.ViewModels;
using HelixToolkit.Wpf;

namespace FEASolver.Views;

public partial class ResultsView : UserControl
{
    private ResultsViewModel? _vm;

    public ResultsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachVm();
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        DetachVm();
        if (e.NewValue is ResultsViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void DetachVm()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    /// <summary>
    /// HelixViewport3D's ZoomExtentsWhenLoaded fires once at control load — by then
    /// ResultModel is still null, so the camera lands at the origin and the heatmap
    /// appears blank. Re-zoom every time a new result mesh is bound.
    /// </summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ResultsViewModel.ResultModel)) return;
        if (_vm?.ResultModel is null) return;

        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            try { ResultsViewport.ZoomExtents(400); } catch { /* viewport not ready */ }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ResultsViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ResultsViewModel vm) return;
        if (vm.ResultModel == null) return;

        var pt = e.GetPosition(ResultsViewport);
        var hits = ResultsViewport.Viewport.FindHits(pt);

        foreach (var hit in hits)
        {
            var meshHit = hit.RayHit;
            if (meshHit != null)
                vm.OnNodeProbed(meshHit.VertexIndex1);
            break;
        }
    }

    // ── Nav-bar helpers ───────────────────────────────────────────────────────

    private void FitToScreen() => ResultsViewport.ZoomExtents(250);

    private void SetPresetView(Vector3D lookDir, Vector3D upDir)
    {
        var cam = ResultsViewport.Camera as PerspectiveCamera;
        if (cam == null) return;
        lookDir.Normalize();
        cam.LookDirection = lookDir;
        cam.UpDirection   = upDir;
        ResultsViewport.ZoomExtents(250);
    }

    // ── Nav-bar button handlers ───────────────────────────────────────────────

    private void ResBtnFit_Click   (object sender, RoutedEventArgs e) => FitToScreen();
    private void ResBtnIso_Click   (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(-1, -1, -0.8), new Vector3D(0, 0, 1));
    private void ResBtnFront_Click (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(0, 1, 0),      new Vector3D(0, 0, 1));
    private void ResBtnRight_Click (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(-1, 0, 0),     new Vector3D(0, 0, 1));
    private void ResBtnTop_Click   (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(0, 0, -1),     new Vector3D(0, 1, 0));
    private void ResBtnLeft_Click  (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(1, 0, 0),      new Vector3D(0, 0, 1));
    private void ResBtnBack_Click  (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(0, -1, 0),     new Vector3D(0, 0, 1));
    private void ResBtnBottom_Click(object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(0, 0, 1),      new Vector3D(0, 1, 0));
}
