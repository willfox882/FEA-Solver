using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using FEASolver.ViewModels;
using HelixToolkit.Wpf;

namespace FEASolver.Views;

public partial class ViewportView : UserControl
{
    public ViewportView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewportViewModel vm)
            vm.PropertyChanged += OnVmPropertyChanged;
    }

    // Watch for DataContext swaps (e.g. design-time vs runtime)
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != DataContextProperty) return;

        if (e.OldValue is ViewportViewModel old)
            old.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is ViewportViewModel nw)
            nw.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewportViewModel.MeshLoaded)) return;
        if (sender is ViewportViewModel vm && vm.MeshLoaded)
        {
            // Queue after the binding propagates geometry to HelixToolkit
            Dispatcher.InvokeAsync(FitToScreen, DispatcherPriority.Loaded);
        }
    }

    // ── Core view helpers ─────────────────────────────────────────────────────

    /// <summary>Fits all visible geometry to fill the viewport.</summary>
    private void FitToScreen() => Viewport3D.ZoomExtents(250);

    /// <summary>
    /// Sets camera looking along <paramref name="lookDir"/> with the given up-axis,
    /// then calls ZoomExtents so the model always fills the screen.
    /// </summary>
    private void SetPresetView(Vector3D lookDir, Vector3D upDir)
    {
        var cam = Viewport3D.Camera as PerspectiveCamera;
        if (cam == null) return;

        lookDir.Normalize();
        cam.LookDirection = lookDir;
        cam.UpDirection   = upDir;

        // ZoomExtents preserves the current LookDirection and only adjusts
        // the camera position / distance — exactly what we want for preset views.
        Viewport3D.ZoomExtents(250);
    }

    // ── Click-vs-drag tracking ────────────────────────────────────────────────

    private Point _mouseDownPos;
    private bool _isDragging;
    private const double DragThreshold = 5.0;

    // ── Viewport mouse / keyboard events ─────────────────────────────────────

    private void Viewport3D_MouseEnter(object sender, MouseEventArgs e)
        => Viewport3D.Focus();

    private void Viewport3D_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (DataContext is ViewportViewModel vmEsc)
                    vmEsc.ClearSelection();
                e.Handled = true;
                break;
            case Key.Home:
                FitToScreen();
                e.Handled = true;
                break;
            case Key.I:
                SetPresetView(new Vector3D(-1, -1, -0.8), new Vector3D(0, 0, 1));
                e.Handled = true;
                break;
            case Key.F:
                SetPresetView(new Vector3D(0, 1, 0), new Vector3D(0, 0, 1));
                e.Handled = true;
                break;
            case Key.R:
                SetPresetView(new Vector3D(-1, 0, 0), new Vector3D(0, 0, 1));
                e.Handled = true;
                break;
            case Key.T:
                SetPresetView(new Vector3D(0, 0, -1), new Vector3D(0, 1, 0));
                e.Handled = true;
                break;
            case Key.L:
                SetPresetView(new Vector3D(1, 0, 0), new Vector3D(0, 0, 1));
                e.Handled = true;
                break;
            case Key.K:
                SetPresetView(new Vector3D(0, -1, 0), new Vector3D(0, 0, 1));
                e.Handled = true;
                break;
            case Key.B:
                SetPresetView(new Vector3D(0, 0, 1), new Vector3D(0, 1, 0));
                e.Handled = true;
                break;
        }
    }

    private void Viewport3D_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Shortcut: double-click on geometry viewport jumps to the Results
        // tab so the user sees the stress heatmap without hunting for the
        // tab header. No-op if no solve has completed.
        if (e.ChangedButton != MouseButton.Left) return;
        var mainVm = Window.GetWindow(this)?.DataContext as MainViewModel;
        if (mainVm is null || !mainVm.HasResults) return;
        mainVm.SelectedTabIndex = 1;
        e.Handled = true;
    }

    private void Viewport3D_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPos = e.GetPosition(Viewport3D);
        _isDragging = false;
        Viewport3D.Focus();
        // Do NOT set Handled — HelixToolkit must receive this event for rotation.
    }

    private void Viewport3D_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Viewport3D);
        double dx = pos.X - _mouseDownPos.X;
        double dy = pos.Y - _mouseDownPos.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > DragThreshold)
            _isDragging = true;
        // Do NOT set Handled.
    }

    private void Viewport3D_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging) return;

        if (DataContext is not ViewportViewModel vm) return;
        if (!vm.MeshLoaded) return;

        var pt   = e.GetPosition(Viewport3D);
        var hits = Viewport3D.Viewport.FindHits(pt);

        bool hitMesh = false;
        foreach (var hit in hits)
        {
            if (hit.Visual != MeshVisual) continue;
            var meshHit = hit.RayHit;
            if (meshHit != null)
            {
                int faceId = vm.GetFaceIdFromVertex(meshHit.VertexIndex1);
                if (faceId < 0) break;
                bool add = Keyboard.IsKeyDown(Key.LeftCtrl) ||
                           Keyboard.IsKeyDown(Key.RightCtrl);
                vm.OnFaceHit(faceId, add);
                hitMesh = true;
            }
            break;
        }

        // Click on empty space (no mesh hit) → clear selection
        if (!hitMesh)
            vm.ClearSelection();

        // Do NOT set Handled.
    }

    // ── Nav-bar button handlers ───────────────────────────────────────────────

    private void BtnFit_Click   (object sender, RoutedEventArgs e) => FitToScreen();
    private void BtnIso_Click   (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(-1, -1, -0.8), new Vector3D(0, 0, 1));
    private void BtnFront_Click (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(0, 1, 0),      new Vector3D(0, 0, 1));
    private void BtnRight_Click (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(-1, 0, 0),     new Vector3D(0, 0, 1));
    private void BtnTop_Click   (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(0, 0, -1),     new Vector3D(0, 1, 0));
    private void BtnLeft_Click  (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(1, 0, 0),      new Vector3D(0, 0, 1));
    private void BtnBack_Click  (object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(0, -1, 0),     new Vector3D(0, 0, 1));
    private void BtnBottom_Click(object sender, RoutedEventArgs e)
        => SetPresetView(new Vector3D(0, 0, 1),      new Vector3D(0, 1, 0));
}
