using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FEASolver.Core.Models;
using FEASolver.Core.Services;
using System.Collections.ObjectModel;

namespace FEASolver.ViewModels;

public enum TorqueAxisMode { GlobalX, GlobalY, GlobalZ, FaceNormal, Custom }

public partial class PropertiesViewModel : ObservableObject
{
    // ── Selected faces (set by viewport) ──
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignBoundaryConditionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AssignLoadCommand))]
    private IReadOnlyList<int> _selectedFaceIds = [];

    private MeshData? _currentMesh;
    private MeshFaceMap? _faceMap;

    /// <summary>Current mesh (set by MainViewModel after Mesh step). Used to
    /// resolve BC/Load node sets at Apply time so the stored backend model is
    /// immediately mesh-bound (non-empty NSETs, resolved nearest nodes).</summary>
    public MeshData? CurrentMesh
    {
        get => _currentMesh;
        set
        {
            _currentMesh = value;
            _faceMap = value?.BuildFaceMap();
        }
    }

    /// <summary>Raised when Apply is rejected (bad face, empty set, zero-magnitude, etc.).</summary>
    public event Action<string>? ApplyError;

    // ── Material ──
    [ObservableProperty] private MaterialModel _currentMaterial = new(
        "Steel", 200_000_000_000.0, 0.3);

    /// <summary>Young's modulus in GPa for UI display (stored internally as Pa).</summary>
    public double YoungsModulusGPa
    {
        get => CurrentMaterial.YoungsModulus / 1e9;
        set
        {
            CurrentMaterial = CurrentMaterial with { YoungsModulus = value * 1e9 };
            OnPropertyChanged();
        }
    }

    partial void OnCurrentMaterialChanged(MaterialModel value) =>
        OnPropertyChanged(nameof(YoungsModulusGPa));

    // ── BC/Load collections ──
    public ObservableCollection<BoundaryCondition> BoundaryConditions { get; } = new();
    public ObservableCollection<Load> Loads { get; } = new();

    // ── BC assignment UI state ──
    [ObservableProperty] private BcType _selectedBcType = BcType.Fixed;

    // ── Load assignment UI state ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadMagnitudeLabel))]
    [NotifyPropertyChangedFor(nameof(IsTorqueSelected))]
    private LoadType _selectedLoadType = LoadType.DistributedForce;

    public string LoadMagnitudeLabel => SelectedLoadType switch
    {
        LoadType.Pressure or LoadType.SurfaceTraction => "Magnitude (MPa)",
        LoadType.Torque => "Torque (N·m)",
        _ => "Magnitude (N, total)"
    };

    [ObservableProperty] private double _loadMagnitude = 0;
    [ObservableProperty] private double _loadDirX = 0;
    [ObservableProperty] private double _loadDirY = 0;
    [ObservableProperty] private double _loadDirZ = -1;

    // ── Point load UI state ──
    [ObservableProperty] private double _pointLoadX = 0;
    [ObservableProperty] private double _pointLoadY = 0;
    [ObservableProperty] private double _pointLoadZ = 0;
    [ObservableProperty] private double _pointForceX = 0;
    [ObservableProperty] private double _pointForceY = 0;
    [ObservableProperty] private double _pointForceZ = 0;

    // ── Torque axis UI state ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomTorqueAxis))]
    [NotifyPropertyChangedFor(nameof(TorqueAxisSummary))]
    private TorqueAxisMode _selectedTorqueAxisMode = TorqueAxisMode.GlobalZ;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TorqueAxisSummary))]
    private double _momentAxisX = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TorqueAxisSummary))]
    private double _momentAxisY = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TorqueAxisSummary))]
    private double _momentAxisZ = 1;

    // Legacy backing field — no longer shown in UI, kept for compat.
    [ObservableProperty] private double _momentMagnitude = 0;

    public bool IsTorqueSelected => SelectedLoadType == LoadType.Torque;

    public bool IsCustomTorqueAxis => SelectedTorqueAxisMode == TorqueAxisMode.Custom;

    public string TorqueAxisSummary => SelectedTorqueAxisMode switch
    {
        TorqueAxisMode.GlobalX => "Axis: Global X  [1, 0, 0]",
        TorqueAxisMode.GlobalY => "Axis: Global Y  [0, 1, 0]",
        TorqueAxisMode.GlobalZ => "Axis: Global Z  [0, 0, 1]",
        TorqueAxisMode.FaceNormal => "Axis: face normal (computed at Apply)",
        TorqueAxisMode.Custom =>
            $"Axis: [{MomentAxisX:G3}, {MomentAxisY:G3}, {MomentAxisZ:G3}] (normalised at Apply)",
        _ => ""
    };

    partial void OnSelectedTorqueAxisModeChanged(TorqueAxisMode value)
    {
        switch (value)
        {
            case TorqueAxisMode.GlobalX: MomentAxisX = 1; MomentAxisY = 0; MomentAxisZ = 0; break;
            case TorqueAxisMode.GlobalY: MomentAxisX = 0; MomentAxisY = 1; MomentAxisZ = 0; break;
            case TorqueAxisMode.GlobalZ: MomentAxisX = 0; MomentAxisY = 0; MomentAxisZ = 1; break;
        }
    }

    [RelayCommand(CanExecute = nameof(HasFaceSelection))]
    private void AssignBoundaryCondition()
    {
        int applied = 0;
        foreach (var faceId in SelectedFaceIds)
        {
            var fg = GetFaceGroup(faceId);
            if (fg is null)
            {
                ApplyError?.Invoke($"Face {faceId} does not exist in the current mesh.");
                continue;
            }
            if (fg.NodeIds.Length == 0)
            {
                ApplyError?.Invoke($"Face {faceId} has no mesh nodes — cannot apply BC.");
                continue;
            }
            // Duplicate-BC guard on same face+type
            if (BoundaryConditions.Any(b => b.FaceId == faceId && b.Type == SelectedBcType))
                continue;

            var bc = new BoundaryCondition(SelectedBcType, faceId, fg.NodeIds.ToArray());
            BoundaryConditions.Add(bc);
            applied++;
        }
        if (applied == 0 && SelectedFaceIds.Count > 0)
            ApplyError?.Invoke("No BCs applied (face(s) rejected by validation).");
    }

    [RelayCommand(CanExecute = nameof(HasFaceSelection))]
    private void AssignLoad()
    {
        int applied = 0;
        foreach (var faceId in SelectedFaceIds)
        {
            var fg = GetFaceGroup(faceId);
            if (fg is null)
            {
                ApplyError?.Invoke($"Face {faceId} does not exist in the current mesh.");
                continue;
            }
            // Type-specific entity presence check
            if (SelectedLoadType is LoadType.Pressure or LoadType.SurfaceTraction
                    or LoadType.DistributedForce
                && fg.ElementFaces.Length == 0)
            {
                ApplyError?.Invoke(
                    $"Face {faceId} has no element-faces — cannot apply {SelectedLoadType}.");
                continue;
            }
            if (fg.NodeIds.Length == 0)
            {
                ApplyError?.Invoke($"Face {faceId} has no nodes — cannot apply load.");
                continue;
            }

            Load load;
            if (SelectedLoadType is LoadType.PointLoad)
            {
                // Force = (Fx,Fy,Fz) if user filled those; otherwise derive from
                // (LoadMagnitude × unit(LoadDir)) so users who only typed
                // magnitude+direction still get a non-zero load.
                double fx = PointForceX, fy = PointForceY, fz = PointForceZ;
                if (Math.Abs(fx) < 1e-15 && Math.Abs(fy) < 1e-15 && Math.Abs(fz) < 1e-15)
                {
                    var (ux, uy, uz) = Normalize(LoadDirX, LoadDirY, LoadDirZ);
                    fx = LoadMagnitude * ux;
                    fy = LoadMagnitude * uy;
                    fz = LoadMagnitude * uz;
                }
                if (Math.Abs(fx) + Math.Abs(fy) + Math.Abs(fz) < 1e-15)
                {
                    ApplyError?.Invoke("Point load has zero force — enter magnitude/direction or Fx/Fy/Fz.");
                    continue;
                }

                // Use centroid of face as default target if user didn't specify XYZ.
                double tx = PointLoadX, ty = PointLoadY, tz = PointLoadZ;
                if (Math.Abs(tx) + Math.Abs(ty) + Math.Abs(tz) < 1e-15 && CurrentMesh is not null)
                {
                    var centroid = FaceCentroid(fg, CurrentMesh);
                    tx = centroid.x; ty = centroid.y; tz = centroid.z;
                }
                int? resolvedNid = CurrentMesh?.NearestNodeOnFace(faceId, tx, ty, tz)?.Id;

                load = new Load(LoadType.PointLoad, faceId,
                    Magnitude: Math.Sqrt(fx * fx + fy * fy + fz * fz),
                    Direction: null,
                    Localized: new LocalizedLoadData(
                        LocationMode.AbsoluteXYZ,
                        [tx, ty, tz], null,
                        [fx, fy, fz],
                        null, null, resolvedNid));
            }
            else if (SelectedLoadType is LoadType.Torque)
            {
                if (Math.Abs(LoadMagnitude) < 1e-15)
                {
                    ApplyError?.Invoke("Torque magnitude is zero — enter a value in N·m.");
                    continue;
                }
                if (fg.NodeIds.Length < 3)
                {
                    ApplyError?.Invoke(
                        $"Face {faceId} has only {fg.NodeIds.Length} node(s); " +
                        "torque requires at least 3 nodes to distribute forces.");
                    continue;
                }
                double[] axisDir = GetTorqueAxis(faceId);
                load = new Load(LoadType.Torque, faceId, LoadMagnitude,
                    Direction: null,
                    Localized: new LocalizedLoadData(
                        LocationMode.AbsoluteXYZ,
                        null, null, null,
                        axisDir,
                        LoadMagnitude,
                        null));
            }
            else if (SelectedLoadType is LoadType.Moment)
            {
                // Legacy path — kept for backward compat. Not exposed in UI.
                double mag = Math.Abs(MomentMagnitude) > 1e-15 ? MomentMagnitude : LoadMagnitude;
                if (Math.Abs(mag) < 1e-15)
                {
                    ApplyError?.Invoke("Moment magnitude is zero.");
                    continue;
                }
                load = new Load(LoadType.Moment, faceId, mag,
                    Direction: null,
                    Localized: new LocalizedLoadData(
                        LocationMode.AbsoluteXYZ,
                        null, null, null,
                        [MomentAxisX, MomentAxisY, MomentAxisZ],
                        mag,
                        null));
            }
            else
            {
                if (Math.Abs(LoadMagnitude) < 1e-15)
                {
                    ApplyError?.Invoke($"{SelectedLoadType} magnitude is zero.");
                    continue;
                }
                var (ux, uy, uz) = Normalize(LoadDirX, LoadDirY, LoadDirZ);
                // UI accepts MPa for Pressure/SurfaceTraction (the unit a
                // mechanical engineer thinks in); solver requires Pa. Without
                // this conversion the user typing "1" for 1 MPa silently
                // produced a 1 Pa load → ~10^6× too small a stress.
                double siMagnitude = SelectedLoadType is LoadType.Pressure or LoadType.SurfaceTraction
                    ? LoadMagnitude * 1e6
                    : LoadMagnitude;
                load = new Load(SelectedLoadType, faceId, siMagnitude,
                    Direction: [ux, uy, uz],
                    Localized: null);
            }
            Loads.Add(load);
            applied++;
        }
        if (applied == 0 && SelectedFaceIds.Count > 0)
            ApplyError?.Invoke("No loads applied (all faces rejected by validation).");
    }

    // ── Torque axis helpers ───────────────────────────────────────────────────

    private double[] GetTorqueAxis(int faceId) => SelectedTorqueAxisMode switch
    {
        TorqueAxisMode.GlobalX   => [1.0, 0.0, 0.0],
        TorqueAxisMode.GlobalY   => [0.0, 1.0, 0.0],
        TorqueAxisMode.GlobalZ   => [0.0, 0.0, 1.0],
        TorqueAxisMode.FaceNormal => ComputeFaceNormal(faceId),
        TorqueAxisMode.Custom    => NormAxis(MomentAxisX, MomentAxisY, MomentAxisZ),
        _                        => [0.0, 0.0, 1.0]
    };

    private double[] ComputeFaceNormal(int faceId)
    {
        var fg = GetFaceGroup(faceId);
        if (fg is null || CurrentMesh is null || fg.NodeIds.Length < 3)
            return [0.0, 0.0, 1.0];
        var nodeMap = CurrentMesh.Nodes.ToDictionary(n => n.Id);
        var ids = fg.NodeIds.Where(nodeMap.ContainsKey).Take(3).ToArray();
        if (ids.Length < 3) return [0.0, 0.0, 1.0];
        var A = nodeMap[ids[0]]; var B = nodeMap[ids[1]]; var C = nodeMap[ids[2]];
        double ax = B.X - A.X, ay = B.Y - A.Y, az = B.Z - A.Z;
        double bx = C.X - A.X, by = C.Y - A.Y, bz = C.Z - A.Z;
        double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return len < 1e-15 ? [0.0, 0.0, 1.0] : [nx / len, ny / len, nz / len];
    }

    private static double[] NormAxis(double x, double y, double z)
    {
        double n = Math.Sqrt(x * x + y * y + z * z);
        return n < 1e-15 ? [0.0, 0.0, 1.0] : [x / n, y / n, z / n];
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private FaceGroup? GetFaceGroup(int faceId)
    {
        if (_faceMap is null) return null;
        return _faceMap.TryGet(faceId, out var fg) ? fg : null;
    }

    private static (double x, double y, double z) Normalize(double x, double y, double z)
    {
        double n = Math.Sqrt(x * x + y * y + z * z);
        if (n < 1e-15) return (0, 0, -1);
        return (x / n, y / n, z / n);
    }

    private static (double x, double y, double z) FaceCentroid(FaceGroup fg, MeshData mesh)
    {
        var nodeMap = mesh.Nodes.ToDictionary(n => n.Id);
        double sx = 0, sy = 0, sz = 0;
        int count = 0;
        foreach (var nid in fg.NodeIds)
        {
            if (!nodeMap.TryGetValue(nid, out var n)) continue;
            sx += n.X; sy += n.Y; sz += n.Z; count++;
        }
        return count == 0 ? (0, 0, 0) : (sx / count, sy / count, sz / count);
    }

    [RelayCommand]
    private void RemoveSelected(object? item)
    {
        if (item is BoundaryCondition bc) BoundaryConditions.Remove(bc);
        else if (item is Load load) Loads.Remove(load);
    }

    private bool HasFaceSelection() => SelectedFaceIds.Count > 0;
}
