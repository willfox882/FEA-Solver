using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FEASolver.Core.Models;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FEASolver.ViewModels;

public partial class ResultsViewModel : ObservableObject
{
    [ObservableProperty] private ResultSet? _currentResults;
    [ObservableProperty] private ResultDisplayMode _displayMode = ResultDisplayMode.VonMises;
    [ObservableProperty] private double _deformationScale = 1.0;
    [ObservableProperty] private bool _autoDeformationScale = true;
    // When false, stress/strain plots render on the undeformed mesh. This is
    // the safe default while post-processing is being debugged — any "bump"
    // visible in that mode cannot come from the displacement pipeline.
    [ObservableProperty] private bool _renderOnDeformedGeometry = false;
    [ObservableProperty] private double _minValue = 0;
    [ObservableProperty] private double _maxValue = 1;
    [ObservableProperty] private string _minLabel = "0";
    [ObservableProperty] private string _maxLabel = "0";
    [ObservableProperty] private Model3D? _resultModel;
    [ObservableProperty] private string _probeText = "";
    [ObservableProperty] private string _surfaceDiagnostic = "";

    /// <summary>
    /// Set to a non-empty string when UpdateColormap aborts before painting.
    /// Cleared to "" at the start of every UpdateColormap call.
    /// Bindable — wire to a TextBlock in ResultsView.xaml to surface errors.
    /// </summary>
    [ObservableProperty] private string _renderError = "";

    // Auto-scale computes a multiplier so |δ_max| ≈ 5% of mesh diagonal. This
    // makes axial-tension cases visible (δ ≈ 10 µm on 1 m beam would be invisible
    // at 1×) without blowing up bending cases where δ is already mm-scale.
    private const double AUTO_DEFORM_TARGET_FRACTION = 0.05;

    private MeshData? _mesh;

    // vertex index → node_id (for probing)
    private int[] _vertexNodeMap = [];

    // CalculiX tet face corners (same as ViewportViewModel)
    private static readonly (int A, int B, int C)[] TetFaceCorners =
    [
        (0, 1, 2),
        (0, 1, 3),
        (1, 2, 3),
        (0, 2, 3),
    ];

    public void LoadResults(ResultSet results, MeshData? mesh = null)
    {
        CurrentResults = results;
        _mesh = mesh;
        UpdateColormap();
    }

    [RelayCommand]
    private void SetDisplayMode(ResultDisplayMode mode)
    {
        DisplayMode = mode;
        UpdateColormap();
    }

    partial void OnDeformationScaleChanged(double value) => UpdateColormap();
    partial void OnDisplayModeChanged(ResultDisplayMode value) => UpdateColormap();
    partial void OnAutoDeformationScaleChanged(bool value) => UpdateColormap();
    partial void OnRenderOnDeformedGeometryChanged(bool value) => UpdateColormap();

    private void UpdateColormap()
    {
        RenderError = "";
        if (CurrentResults == null || _mesh == null) return;

        var field = DisplayMode switch
        {
            ResultDisplayMode.VonMises          => CurrentResults.VonMises,
            ResultDisplayMode.DisplacementMag   => CurrentResults.DisplacementMag,
            ResultDisplayMode.DisplacementX     => CurrentResults.DisplacementX,
            ResultDisplayMode.DisplacementY     => CurrentResults.DisplacementY,
            ResultDisplayMode.DisplacementZ     => CurrentResults.DisplacementZ,
            ResultDisplayMode.MaxPrincipal      => ComputePrincipal(CurrentResults, takeMax: true),
            ResultDisplayMode.MinPrincipal      => ComputePrincipal(CurrentResults, takeMax: false),
            ResultDisplayMode.StrainVonMises    => CurrentResults.StrainVonMises,
            ResultDisplayMode.StrainXX          => CurrentResults.E11,
            ResultDisplayMode.StrainYY          => CurrentResults.E22,
            ResultDisplayMode.StrainZZ          => CurrentResults.E33,
            _                                   => CurrentResults.VonMises
        };

        if (field == null || field.Values == null || field.NodeIds == null
            || field.Values.Length == 0 || field.NodeIds.Length == 0) return;

        MinValue = field.Values.Min();
        MaxValue = field.Values.Max();

        bool isStress = DisplayMode is ResultDisplayMode.VonMises
                          or ResultDisplayMode.MaxPrincipal
                          or ResultDisplayMode.MinPrincipal;
        bool isStrain = DisplayMode is ResultDisplayMode.StrainVonMises
                          or ResultDisplayMode.StrainXX
                          or ResultDisplayMode.StrainYY
                          or ResultDisplayMode.StrainZZ;
        double displayFactor = isStress ? 1e-6 : (isStrain ? 1.0 : 1000.0);
        string unit = isStress ? "MPa" : (isStrain ? "" : "mm");
        string fmt = isStrain ? "G4" : "G4";
        MinLabel = $"{MinValue * displayFactor:G4} {unit}".TrimEnd();
        MaxLabel = $"{MaxValue * displayFactor:G4} {unit}".TrimEnd();

        // Build node_id → result value lookup (guard against mismatched array lengths)
        int fieldCount = Math.Min(field.NodeIds.Length, field.Values.Length);
        var valueByNode = new Dictionary<int, double>(fieldCount);
        for (int i = 0; i < fieldCount; i++)
            valueByNode[field.NodeIds[i]] = field.Values[i];

        // Build node_id → displacement (for deformed mesh). Applies several
        // guards against the "huge displacement / bump" symptom:
        //   1. node-id → value map is keyed only by dx.NodeIds (not by positional
        //      index), so FRD ordering can never shift the mapping.
        //   2. any |δ| > 10× mesh diagonal is dropped as unphysical (prevents a
        //      single solver-NaN node from launching the deformed mesh to infinity).
        //   3. effectiveScale is computed once and clamped so `scale · |δ|_max`
        //      never exceeds 25% of the mesh diagonal — no double-apply, no blow-up.
        Dictionary<int, (double ux, double uy, double uz)>? dispByNode = null;
        double effectiveScale = DeformationScale;
        double meshDiag = MeshDiagonal(_mesh);
        int dispRejectedCount = 0;
        double rawMaxU = 0;
        if (RenderOnDeformedGeometry
            && CurrentResults.DisplacementX != null
            && CurrentResults.DisplacementY != null && CurrentResults.DisplacementZ != null)
        {
            dispByNode = BuildDispMap(
                CurrentResults.DisplacementX,
                CurrentResults.DisplacementY,
                CurrentResults.DisplacementZ);

            // Guard: reject wildly unphysical displacement magnitudes.
            if (meshDiag > 0)
            {
                double cutoff = 10.0 * meshDiag;
                var bad = new List<int>();
                foreach (var kv in dispByNode)
                {
                    var d = kv.Value;
                    if (double.IsNaN(d.ux) || double.IsNaN(d.uy) || double.IsNaN(d.uz)
                        || double.IsInfinity(d.ux) || double.IsInfinity(d.uy) || double.IsInfinity(d.uz)
                        || Math.Sqrt(d.ux*d.ux + d.uy*d.uy + d.uz*d.uz) > cutoff)
                        bad.Add(kv.Key);
                }
                foreach (int k in bad) dispByNode.Remove(k);
                dispRejectedCount = bad.Count;
            }

            foreach (var (_, d) in dispByNode)
            {
                double m = Math.Sqrt(d.ux * d.ux + d.uy * d.uy + d.uz * d.uz);
                if (m > rawMaxU) rawMaxU = m;
            }

            LogFirstDisplacements(dispByNode);

            if (AutoDeformationScale && dispByNode.Count > 0
                && rawMaxU > 1e-18 && meshDiag > 1e-18)
                effectiveScale = AUTO_DEFORM_TARGET_FRACTION * meshDiag / rawMaxU;
            else if (AutoDeformationScale)
                effectiveScale = 0;

            // Hard cap: the deformed view should never be more distorted than
            // ~25% of the model's own size. Prevents any remaining bug below
            // this point from producing a "bump" the user perceives as garbage.
            if (meshDiag > 0 && rawMaxU > 0)
            {
                double maxAllowed = 0.25 * meshDiag / rawMaxU;
                if (effectiveScale > maxAllowed) effectiveScale = maxAllowed;
            }
        }
        if (effectiveScale <= 0) dispByNode = null;

        // Displacement sanity check — gates deformed geometry ONLY.
        // A displacement magnitude > 10% of model size is physically impossible
        // for linear statics and signals a load-application or unit mismatch.
        // We suppress the deformed view and set a visible warning, but continue
        // to paint the scalar heatmap on the undeformed mesh. The viewport is
        // never left blank by this check.
        bool isDispMode = DisplayMode is ResultDisplayMode.DisplacementMag
                          or ResultDisplayMode.DisplacementX
                          or ResultDisplayMode.DisplacementY
                          or ResultDisplayMode.DisplacementZ;

        // Choose the displacement magnitude to test:
        //   • displacement scalar field  → use the field values directly (metres)
        //   • stress/strain on deformed  → use rawMaxU from the filtered dispByNode
        double maxAbsDispM = 0;
        if (isDispMode && field.Values.Length > 0)
            maxAbsDispM = field.Values.Max(v => Math.Abs(v));
        else if (dispByNode != null && rawMaxU > 0)
            maxAbsDispM = rawMaxU;

        if (maxAbsDispM > 0.1 * meshDiag && meshDiag > 1e-10)
        {
            Debug.WriteLine(
                $"[DispSanity] WARN — |δ|_max={maxAbsDispM * 1000:G4} mm  " +
                $"threshold=10%·diag={0.1 * meshDiag * 1000:G4} mm  " +
                $"isDispMode={isDispMode}  RenderOnDeformed={RenderOnDeformedGeometry}");
            RenderError =
                $"⚠ Deformed view disabled: |δ|_max = {maxAbsDispM * 1000:G4} mm " +
                $"exceeds 10% of model size ({meshDiag * 1000:G4} mm). " +
                $"Scalar field rendered on undeformed geometry. " +
                $"Check load application and unit settings (toolbar units selector).";
            // Suppress deformed geometry — continue rendering scalar heatmap on undeformed mesh.
            dispByNode = null;
            effectiveScale = 0;
        }

        // Range floor. When the field is (numerically) uniform — synthetic σ = F/A,
        // or an unloaded region — the raw range is either 0 or floating-point noise
        // around zero. Normalising by a near-zero range amplifies noise to the full
        // colormap and produces the "speckled, non-symmetric patches" symptom.
        // Floor the range at 1e-4 × peak magnitude so uniform fields map to a
        // single colour instead of a rainbow of round-off.
        double peakMag = Math.Max(Math.Abs(MaxValue), Math.Abs(MinValue));
        double rawRange = MaxValue - MinValue;
        double rangeFloor = Math.Max(1e-12, 1e-4 * peakMag);
        // Uniform field detection: when raw range is at/below floor, the only
        // sane thing to paint is a single colour. Forcing U=0.5 (mid-colormap
        // green) avoids LinearGradientBrush artifacts when every vertex texU is
        // clustered at 0 — the degenerate case that produced "speckled" output.
        bool uniformField = rawRange < rangeFloor;
        double range = uniformField ? 1.0 : rawRange;

        LogFirstScalarValues(valueByNode);

        // Missing-node detector.
        // Collects all unique surface corner node IDs and checks which are absent
        // from valueByNode. Two-tier policy:
        //   ALL missing  → abort (result field covers a completely different node set).
        //   SOME missing → warn in RenderError + continue; GetValueOrDefault below
        //                  gives MinValue (blue) at those vertices, which is visible
        //                  and tells the user something is wrong without hiding the field.
        {
            var preElemMap = _mesh.Elements.ToDictionary(e => e.Id);
            var allSurfaceNodes = new HashSet<int>();
            foreach (var face in _mesh.FaceGroups)
            {
                foreach (var ef in face.ElementFaces)
                {
                    if (ef is not { Length: >= 2 }) continue;
                    if (!preElemMap.TryGetValue(ef[0], out var elem)) continue;
                    int fi = ef[1];
                    if (fi < 0 || fi >= TetFaceCorners.Length) continue;
                    var (ia, ib, ic) = TetFaceCorners[fi];
                    if (ia < elem.Nodes.Length) allSurfaceNodes.Add(elem.Nodes[ia]);
                    if (ib < elem.Nodes.Length) allSurfaceNodes.Add(elem.Nodes[ib]);
                    if (ic < elem.Nodes.Length) allSurfaceNodes.Add(elem.Nodes[ic]);
                }
            }

            var missing = allSurfaceNodes
                .Where(nid => !valueByNode.ContainsKey(nid))
                .OrderBy(n => n)
                .ToList();

            Debug.WriteLine(
                $"[MissingNode] field={DisplayMode}  surface-corner-nodes={allSurfaceNodes.Count}  " +
                $"in-field={allSurfaceNodes.Count - missing.Count}  missing={missing.Count}");

            if (missing.Count > 0)
            {
                string idList = string.Join(", ", missing.Take(20));
                Debug.WriteLine(
                    $"[MissingNode] missing IDs: {idList}" +
                    $"{(missing.Count > 20 ? $" …(+{missing.Count - 20} more)" : "")}");

                if (missing.Count == allSurfaceNodes.Count)
                {
                    // All surface nodes missing — field is unusable; abort.
                    string abort =
                        $"No {DisplayMode} values for any surface node " +
                        $"({allSurfaceNodes.Count} total). " +
                        $"FRD result block may be empty or cover a different node set.";
                    Debug.WriteLine($"[RenderAbort] {abort}");
                    RenderError = abort;
                    ResultModel = null;
                    return;
                }

                // Partial gap — warn and keep rendering.
                string warn = missing.Count <= 10
                    ? $"⚠ {missing.Count} surface node(s) missing {DisplayMode} values " +
                      $"(IDs: {string.Join(", ", missing)}). Blue shown at those nodes."
                    : $"⚠ {missing.Count}/{allSurfaceNodes.Count} surface nodes missing " +
                      $"{DisplayMode} values (first 20: {idList}). " +
                      $"Blue shown at those nodes. Check for truncated .frd output.";
                RenderError = string.IsNullOrEmpty(RenderError)
                    ? warn
                    : RenderError + "\n" + warn;
            }
        }

        var positions  = new Point3DCollection();
        var normals    = new Vector3DCollection();
        var triIndices = new Int32Collection();
        var texCoords  = new PointCollection();
        var vertNodes  = new List<int>();

        var nodePos = _mesh.Nodes.ToDictionary(n => n.Id, n => new Point3D(n.X, n.Y, n.Z));
        var elemMap = _mesh.Elements.ToDictionary(e => e.Id);

        foreach (var face in _mesh.FaceGroups)
        {
            foreach (var ef in face.ElementFaces)
            {
                if (ef == null || ef.Length < 2) continue;
                int elemId  = ef[0];
                int faceIdx = ef[1];
                if (!elemMap.TryGetValue(elemId, out var elem)) continue;
                if (faceIdx < 0 || faceIdx >= TetFaceCorners.Length) continue;

                var (ia, ib, ic) = TetFaceCorners[faceIdx];
                if (ia >= elem.Nodes.Length || ib >= elem.Nodes.Length || ic >= elem.Nodes.Length) continue;

                int nA = elem.Nodes[ia], nB = elem.Nodes[ib], nC = elem.Nodes[ic];
                if (!nodePos.TryGetValue(nA, out var bpA) ||
                    !nodePos.TryGetValue(nB, out var bpB) ||
                    !nodePos.TryGetValue(nC, out var bpC)) continue;

                // Apply deformation if available
                var pA = Deform(bpA, nA, effectiveScale, dispByNode);
                var pB = Deform(bpB, nB, effectiveScale, dispByNode);
                var pC = Deform(bpC, nC, effectiveScale, dispByNode);

                int v = positions.Count;
                positions.Add(pA); positions.Add(pB); positions.Add(pC);

                var norm = ComputeNormal(pA, pB, pC);
                normals.Add(norm); normals.Add(norm); normals.Add(norm);

                triIndices.Add(v); triIndices.Add(v + 1); triIndices.Add(v + 2);

                // Texture U = normalized result value (forced to 0.5 for uniform fields)
                double uA = uniformField ? 0.5 : NormalizeValue(valueByNode.GetValueOrDefault(nA, MinValue), MinValue, range);
                double uB = uniformField ? 0.5 : NormalizeValue(valueByNode.GetValueOrDefault(nB, MinValue), MinValue, range);
                double uC = uniformField ? 0.5 : NormalizeValue(valueByNode.GetValueOrDefault(nC, MinValue), MinValue, range);
                texCoords.Add(new Point(uA, 0));
                texCoords.Add(new Point(uB, 0));
                texCoords.Add(new Point(uC, 0));

                vertNodes.Add(nA); vertNodes.Add(nB); vertNodes.Add(nC);
            }
        }

        _vertexNodeMap = vertNodes.ToArray();

        SurfaceDiagnostic = BuildSurfaceDiagnostic(
            positions, vertNodes, dispByNode, effectiveScale, dispRejectedCount);

        var geo = new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = triIndices,
            TextureCoordinates = texCoords
        };

        var brush = BuildColormapBrush();
        var mat = new DiffuseMaterial(brush);
        ResultModel = new GeometryModel3D(geo, mat) { BackMaterial = mat };
    }

    public void OnNodeProbed(int vertexIndex)
    {
        if (CurrentResults == null) return;
        if (vertexIndex < 0 || vertexIndex >= _vertexNodeMap.Length) return;
        int nodeId = _vertexNodeMap[vertexIndex];

        double ux = GetNodeValue(CurrentResults.DisplacementX, nodeId);
        double uy = GetNodeValue(CurrentResults.DisplacementY, nodeId);
        double uz = GetNodeValue(CurrentResults.DisplacementZ, nodeId);
        double vm = GetNodeValue(CurrentResults.VonMises, nodeId);

        ProbeText = $"Node {nodeId}\n" +
                    $"U: ({ux*1000:G4}, {uy*1000:G4}, {uz*1000:G4}) mm\n" +
                    $"σ_vm: {vm/1e6:G4} MPa";
    }

    private static double GetNodeValue(ResultField? field, int nodeId)
    {
        if (field == null || field.NodeIds == null || field.Values == null) return 0;
        int idx = Array.IndexOf(field.NodeIds, nodeId);
        return idx >= 0 && idx < field.Values.Length ? field.Values[idx] : 0;
    }

    private static Dictionary<int, (double, double, double)> BuildDispMap(
        ResultField dx, ResultField dy, ResultField dz)
    {
        // Key by node-id independently for each component. A prior implementation
        // read dy.Values[i] / dz.Values[i] by the positional index into dx.NodeIds,
        // which silently corrupts the deformed shape when the three components
        // come in different orderings (possible with some FRD writers).
        if (dx.NodeIds == null || dx.Values == null) return new();
        var yMap = IndexByNodeId(dy);
        var zMap = IndexByNodeId(dz);
        int count = Math.Min(dx.NodeIds.Length, dx.Values.Length);
        var map = new Dictionary<int, (double, double, double)>(count);
        for (int i = 0; i < count; i++)
        {
            int nid = dx.NodeIds[i];
            double ux = dx.Values[i];
            double uy = yMap.TryGetValue(nid, out var vy) ? vy : 0.0;
            double uz = zMap.TryGetValue(nid, out var vz) ? vz : 0.0;
            map[nid] = (ux, uy, uz);
        }
        return map;
    }

    private static Dictionary<int, double> IndexByNodeId(ResultField? f)
    {
        if (f?.NodeIds == null || f.Values == null) return new();
        int n = Math.Min(f.NodeIds.Length, f.Values.Length);
        var d = new Dictionary<int, double>(n);
        for (int i = 0; i < n; i++) d[f.NodeIds[i]] = f.Values[i];
        return d;
    }

    private static Point3D Deform(Point3D basePos, int nodeId, double scale,
        Dictionary<int, (double ux, double uy, double uz)>? dispMap)
    {
        if (dispMap == null || scale == 0 || !dispMap.TryGetValue(nodeId, out var d))
            return basePos;
        return new Point3D(basePos.X + scale * d.ux,
                           basePos.Y + scale * d.uy,
                           basePos.Z + scale * d.uz);
    }

    private static double NormalizeValue(double val, double min, double range)
        => Math.Clamp((val - min) / range, 0, 1);

    private static Vector3D ComputeNormal(Point3D a, Point3D b, Point3D c)
    {
        var u = b - a;
        var v = c - a;
        var n = Vector3D.CrossProduct(u, v);
        if (n.Length > 1e-15) n.Normalize();
        return n;
    }

    /// <summary>
    /// Closed-form principal stress (max or min) of the symmetric Cauchy
    /// tensor at every node. Returns null if the tensor was not parsed
    /// (older results.json without S11..S13). Eigenvalues come from the
    /// trigonometric solution of the characteristic polynomial.
    /// </summary>
    private static ResultField? ComputePrincipal(ResultSet r, bool takeMax)
    {
        if (r.S11 is null || r.S22 is null || r.S33 is null
            || r.S12 is null || r.S23 is null || r.S13 is null) return null;
        var ids = r.S11.NodeIds;
        if (ids is null || ids.Length == 0) return null;
        int n = Math.Min(ids.Length, r.S11.Values.Length);
        var vals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s11 = r.S11.Values[i], s22 = r.S22.Values[i], s33 = r.S33.Values[i];
            double s12 = r.S12.Values[i], s23 = r.S23.Values[i], s13 = r.S13.Values[i];
            // Invariants of σ
            double I1 = s11 + s22 + s33;
            double I2 = s11*s22 + s22*s33 + s33*s11 - s12*s12 - s23*s23 - s13*s13;
            double I3 = s11*s22*s33 + 2*s12*s23*s13 - s11*s23*s23 - s22*s13*s13 - s33*s12*s12;
            // Solve λ³ − I1λ² + I2λ − I3 = 0 via trigonometric method
            double p = I1 / 3.0;
            double q = (2*I1*I1*I1 - 9*I1*I2 + 27*I3) / 54.0;
            double r2 = (I1*I1 - 3*I2) / 9.0;
            double s1, s3;
            if (r2 <= 1e-30)
            {
                s1 = s3 = p;  // hydrostatic state
            }
            else
            {
                double sr = Math.Sqrt(r2);
                double cosArg = Math.Clamp(q / (sr * sr * sr), -1.0, 1.0);
                double phi = Math.Acos(cosArg) / 3.0;
                double a = -2 * sr;
                s1 = p + a * Math.Cos(phi + 2 * Math.PI / 3.0);  // max
                s3 = p + a * Math.Cos(phi);                       // min
            }
            vals[i] = takeMax ? s1 : s3;
        }
        return new ResultField(ids, vals);
    }

    private static void LogFirstDisplacements(
        Dictionary<int, (double ux, double uy, double uz)> disp)
    {
        int n = 0;
        Debug.WriteLine("[Results] First displacements (nodeId, ux, uy, uz) [m]:");
        foreach (var kv in disp.OrderBy(k => k.Key))
        {
            if (n++ >= 20) break;
            Debug.WriteLine($"  {kv.Key,8}  {kv.Value.ux,+14:G6}  " +
                            $"{kv.Value.uy,+14:G6}  {kv.Value.uz,+14:G6}");
        }
    }

    private void LogFirstScalarValues(Dictionary<int, double> valByNode)
    {
        int n = 0;
        Debug.WriteLine($"[Results] First scalar values ({DisplayMode}) by nodeId:");
        foreach (var kv in valByNode.OrderBy(k => k.Key))
        {
            if (n++ >= 20) break;
            Debug.WriteLine($"  {kv.Key,8}  {kv.Value:G6}");
        }
    }

    private static double MeshDiagonal(MeshData mesh)
    {
        if (mesh.Nodes.Length == 0) return 0;
        double xmin=double.MaxValue, ymin=double.MaxValue, zmin=double.MaxValue;
        double xmax=double.MinValue, ymax=double.MinValue, zmax=double.MinValue;
        foreach (var n in mesh.Nodes)
        {
            if (n.X<xmin) xmin=n.X; if (n.X>xmax) xmax=n.X;
            if (n.Y<ymin) ymin=n.Y; if (n.Y>ymax) ymax=n.Y;
            if (n.Z<zmin) zmin=n.Z; if (n.Z>zmax) zmax=n.Z;
        }
        double dx=xmax-xmin, dy=ymax-ymin, dz=zmax-zmin;
        return Math.Sqrt(dx*dx+dy*dy+dz*dz);
    }

    private string BuildSurfaceDiagnostic(
        Point3DCollection positions,
        List<int> vertNodes,
        Dictionary<int, (double ux, double uy, double uz)>? dispByNode,
        double effectiveScale,
        int dispRejectedCount)
    {
        if (_mesh == null) return "";
        int nTri = positions.Count / 3;
        int nUnique = vertNodes.Distinct().Count();
        double diag = MeshDiagonal(_mesh);
        int nanCount = 0;
        double maxDelta = 0;
        foreach (var p in positions)
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsNaN(p.Z)) nanCount++;
        int missing = 0;
        if (dispByNode != null)
        {
            foreach (int nid in vertNodes.Distinct())
                if (!dispByNode.ContainsKey(nid)) missing++;
            foreach (var (_, d) in dispByNode)
            {
                double m = Math.Sqrt(d.ux*d.ux + d.uy*d.uy + d.uz*d.uz);
                if (m > maxDelta) maxDelta = m;
            }
        }
        var msg = $"surf-tris={nTri}, nodes={nUnique}, " +
                  $"diag={diag*1000:G4}mm, |δ|max={maxDelta*1000:G4}mm, " +
                  $"scale×={effectiveScale:G3}";
        if (nanCount > 0) msg += $"  [!] NaN pos: {nanCount}";
        if (missing > 0) msg += $"  [!] missing disp: {missing} surface nodes";
        if (dispRejectedCount > 0)
            msg += $"  [!] dropped {dispRejectedCount} unphysical disp entries (>10×diag)";
        if (effectiveScale * maxDelta > 0.5 * diag && diag > 0)
            msg += "  [!] deform > 50% of diag — reduce scale";
        return msg;
    }

    /// <summary>
    /// Blue (min) → Cyan → Green → Yellow → Red (max) rainbow.
    /// Sampled via TextureCoordinates U in [0,1].
    /// </summary>
    private static LinearGradientBrush BuildColormapBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint   = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        brush.GradientStops.Add(new GradientStop(Colors.Blue,    0.00));
        brush.GradientStops.Add(new GradientStop(Colors.Cyan,    0.25));
        brush.GradientStops.Add(new GradientStop(Colors.Green,   0.50));
        brush.GradientStops.Add(new GradientStop(Colors.Yellow,  0.75));
        brush.GradientStops.Add(new GradientStop(Colors.Red,     1.00));
        return brush;
    }
}

public enum ResultDisplayMode
{
    VonMises,
    MaxPrincipal,
    MinPrincipal,
    DisplacementMag,
    DisplacementX,
    DisplacementY,
    DisplacementZ,
    StrainVonMises,
    StrainXX,
    StrainYY,
    StrainZZ
}
