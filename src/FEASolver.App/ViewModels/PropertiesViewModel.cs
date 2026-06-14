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

    // CalculiX tet face index → the 3 corner indices into Element.Nodes.
    private static readonly int[][] TetFaceCorners =
        [[0, 1, 2], [0, 1, 3], [1, 2, 3], [0, 2, 3]];

    /// <summary>
    /// Outward unit normal of a CAD face, used as the torque axis in FaceNormal
    /// mode. The sign matters: a torque about +n vs −n curls the opposite way,
    /// so an arbitrarily-wound normal makes the applied torque (and its glyph)
    /// point the wrong direction half the time.
    ///
    /// We therefore orient each element-face normal OUTWARD by pointing it away
    /// from that tet's 4th vertex — the same unambiguous rule the INP writer
    /// uses — and average over the face's element-faces (smooths curved faces).
    /// </summary>
    private double[] ComputeFaceNormal(int faceId)
    {
        var fg = GetFaceGroup(faceId);
        if (fg is null || CurrentMesh is null) return [0.0, 0.0, 1.0];
        var nodeMap = CurrentMesh.Nodes.ToDictionary(n => n.Id);

        double sx = 0, sy = 0, sz = 0;
        if (fg.ElementFaces is { Length: > 0 } && CurrentMesh.Elements is not null)
        {
            var elemMap = CurrentMesh.Elements.ToDictionary(e => e.Id);
            foreach (var ef in fg.ElementFaces)
            {
                if (ef.Length < 2 || !elemMap.TryGetValue(ef[0], out var elem)) continue;
                int fi = ef[1];
                if (fi < 0 || fi > 3) continue;
                var corners = TetFaceCorners[fi];
                if (corners[2] >= elem.Nodes.Length) continue;
                if (!nodeMap.TryGetValue(elem.Nodes[corners[0]], out var A) ||
                    !nodeMap.TryGetValue(elem.Nodes[corners[1]], out var B) ||
                    !nodeMap.TryGetValue(elem.Nodes[corners[2]], out var C)) continue;

                double ax = B.X - A.X, ay = B.Y - A.Y, az = B.Z - A.Z;
                double bx = C.X - A.X, by = C.Y - A.Y, bz = C.Z - A.Z;
                double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-15) continue;
                nx /= len; ny /= len; nz /= len;

                // Orient outward: flip to point away from the tet's 4th vertex.
                int fourth = 6 - corners[0] - corners[1] - corners[2];
                if (fourth < elem.Nodes.Length &&
                    nodeMap.TryGetValue(elem.Nodes[fourth], out var D))
                {
                    double dx = D.X - A.X, dy = D.Y - A.Y, dz = D.Z - A.Z;
                    if (nx * dx + ny * dy + nz * dz > 0) { nx = -nx; ny = -ny; nz = -nz; }
                }
                sx += nx; sy += ny; sz += nz;
            }
            double slen = Math.Sqrt(sx * sx + sy * sy + sz * sz);
            if (slen > 1e-12) return [sx / slen, sy / slen, sz / slen];
        }

        // Fallback (no element-faces): first 3 face nodes, oriented away from the
        // mesh centroid so the sign is still meaningful for convex bodies.
        var ids = fg.NodeIds.Where(nodeMap.ContainsKey).Take(3).ToArray();
        if (ids.Length < 3) return [0.0, 0.0, 1.0];
        var p0 = nodeMap[ids[0]]; var p1 = nodeMap[ids[1]]; var p2 = nodeMap[ids[2]];
        double ux = p1.X - p0.X, uy = p1.Y - p0.Y, uz = p1.Z - p0.Z;
        double vx = p2.X - p0.X, vy = p2.Y - p0.Y, vz = p2.Z - p0.Z;
        double mx = uy * vz - uz * vy, my = uz * vx - ux * vz, mz = ux * vy - uy * vx;
        double mlen = Math.Sqrt(mx * mx + my * my + mz * mz);
        if (mlen < 1e-15) return [0.0, 0.0, 1.0];
        mx /= mlen; my /= mlen; mz /= mlen;
        double cx = CurrentMesh.Nodes.Average(n => n.X);
        double cy = CurrentMesh.Nodes.Average(n => n.Y);
        double cz = CurrentMesh.Nodes.Average(n => n.Z);
        if (mx * (p0.X - cx) + my * (p0.Y - cy) + mz * (p0.Z - cz) < 0)
            { mx = -mx; my = -my; mz = -mz; }
        return [mx, my, mz];
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
