using CommunityToolkit.Mvvm.ComponentModel;
using FEASolver.Core.Models;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FEASolver.ViewModels;

public partial class ViewportViewModel : ObservableObject
{
    [ObservableProperty] private bool _meshLoaded = false;
    [ObservableProperty] private MeshGeometry3D? _meshGeometry;
    [ObservableProperty] private MeshGeometry3D? _selectionGeometry;
    [ObservableProperty] private MeshGeometry3D? _constraintMarkersGeometry;
    [ObservableProperty] private MeshGeometry3D? _loadArrowsGeometry;
    [ObservableProperty] private ObservableCollection<int> _selectedFaceIds = new();

    public MeshData? CurrentMesh { get; private set; }
    public event EventHandler<IReadOnlyList<int>>? FaceSelectionChanged;

    // CalculiX tet face corner indices: face_idx → (nodeA, nodeB, nodeC) within element.Nodes
    private static readonly (int A, int B, int C)[] TetFaceCorners =
    [
        (0, 1, 2),  // face 0 — S1/P1
        (0, 1, 3),  // face 1 — S2/P2
        (1, 2, 3),  // face 2 — S3/P3
        (0, 2, 3),  // face 3 — S4/P4
    ];

    // vertex index → face_id for O(1) hit-test lookup
    private int[] _vertexFaceMap = [];

    // face_id → cached triangle list (for selection rebuild)
    private Dictionary<int, List<(Point3D A, Point3D B, Point3D C)>> _faceTriangles = new();

    public void LoadMesh(MeshData meshData)
    {
        CurrentMesh = meshData;
        MeshGeometry = BuildGeometry(meshData);
        MeshLoaded = true;
        SelectedFaceIds.Clear();
        SelectionGeometry = null;
    }

    public void ClearMesh()
    {
        CurrentMesh = null;
        MeshGeometry = null;
        SelectionGeometry = null;
        MeshLoaded = false;
        SelectedFaceIds.Clear();
        _vertexFaceMap = [];
        _faceTriangles.Clear();
    }

    /// <summary>Clears the current face selection and notifies listeners.</summary>
    public void ClearSelection()
    {
        if (SelectedFaceIds.Count == 0) return;
        SelectedFaceIds.Clear();
        SelectionGeometry = null;
        FaceSelectionChanged?.Invoke(this, []);
    }

    /// <summary>
    /// Selects exactly one face (e.g. from a model-tree click).
    /// Does nothing if faceId is unknown.
    /// </summary>
    public void SelectFace(int faceId)
    {
        if (!_faceTriangles.ContainsKey(faceId)) return;
        SelectedFaceIds.Clear();
        SelectedFaceIds.Add(faceId);
        UpdateSelectionGeometry();
        FaceSelectionChanged?.Invoke(this, SelectedFaceIds.ToList());
    }

    /// <summary>
    /// Called from viewport code-behind on face hit-test.
    /// </summary>
    public void OnFaceHit(int faceId, bool addToSelection)
    {
        if (!addToSelection)
            SelectedFaceIds.Clear();

        if (SelectedFaceIds.Contains(faceId))
            SelectedFaceIds.Remove(faceId);
        else
            SelectedFaceIds.Add(faceId);

        UpdateSelectionGeometry();
        FaceSelectionChanged?.Invoke(this, SelectedFaceIds.ToList());
    }

    /// <summary>Returns face_id for a vertex index in the main MeshGeometry, or -1.</summary>
    public int GetFaceIdFromVertex(int vertexIndex)
    {
        if (vertexIndex < 0 || vertexIndex >= _vertexFaceMap.Length) return -1;
        return _vertexFaceMap[vertexIndex];
    }

    private MeshGeometry3D BuildGeometry(MeshData meshData)
    {
        var positions    = new Point3DCollection();
        var normals      = new Vector3DCollection();
        var triIndices   = new Int32Collection();
        var vertexFaces  = new List<int>();

        var nodePos  = meshData.Nodes.ToDictionary(n => n.Id, n => new Point3D(n.X, n.Y, n.Z));
        var elemMap  = meshData.Elements.ToDictionary(e => e.Id);
        _faceTriangles.Clear();

        foreach (var face in meshData.FaceGroups)
        {
            var faceTris = new List<(Point3D, Point3D, Point3D)>();

            foreach (var ef in face.ElementFaces)
            {
                if (ef == null || ef.Length < 2) continue;
                int elemId  = ef[0];
                int faceIdx = ef[1];

                if (!elemMap.TryGetValue(elemId, out var elem)) continue;
                if (faceIdx < 0 || faceIdx >= TetFaceCorners.Length) continue;

                var (ia, ib, ic) = TetFaceCorners[faceIdx];
                if (ia >= elem.Nodes.Length || ib >= elem.Nodes.Length || ic >= elem.Nodes.Length) continue;

                if (!nodePos.TryGetValue(elem.Nodes[ia], out var pA) ||
                    !nodePos.TryGetValue(elem.Nodes[ib], out var pB) ||
                    !nodePos.TryGetValue(elem.Nodes[ic], out var pC)) continue;

                faceTris.Add((pA, pB, pC));

                int v = positions.Count;
                positions.Add(pA);
                positions.Add(pB);
                positions.Add(pC);

                var n = ComputeNormal(pA, pB, pC);
                normals.Add(n); normals.Add(n); normals.Add(n);

                triIndices.Add(v); triIndices.Add(v + 1); triIndices.Add(v + 2);

                vertexFaces.Add(face.FaceId);
                vertexFaces.Add(face.FaceId);
                vertexFaces.Add(face.FaceId);
            }

            _faceTriangles[face.FaceId] = faceTris;
        }

        _vertexFaceMap = vertexFaces.ToArray();

        return new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = triIndices
        };
    }

    private void UpdateSelectionGeometry()
    {
        if (SelectedFaceIds.Count == 0)
        {
            SelectionGeometry = null;
            return;
        }

        var positions  = new Point3DCollection();
        var triIndices = new Int32Collection();

        foreach (int faceId in SelectedFaceIds)
        {
            if (!_faceTriangles.TryGetValue(faceId, out var tris)) continue;
            foreach (var (pA, pB, pC) in tris)
            {
                int v = positions.Count;
                positions.Add(pA);
                positions.Add(pB);
                positions.Add(pC);
                triIndices.Add(v); triIndices.Add(v + 1); triIndices.Add(v + 2);
            }
        }

        SelectionGeometry = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = triIndices
        };
    }

    private static Vector3D ComputeNormal(Point3D a, Point3D b, Point3D c)
    {
        var u = b - a;
        var v = c - a;
        var n = Vector3D.CrossProduct(u, v);
        if (n.Length > 1e-15) n.Normalize();
        return n;
    }

    // ── Constraint / load visuals ─────────────────────────────────────────────

    public void UpdateConstraintMarkers(IEnumerable<BoundaryCondition> bcs)
    {
        if (CurrentMesh == null) { ConstraintMarkersGeometry = null; return; }

        var nodePos = CurrentMesh.Nodes.ToDictionary(n => n.Id, n => new Point3D(n.X, n.Y, n.Z));
        double diag = ComputeDiagonal(nodePos.Values);
        double L = diag * 0.03;
        double T = L * 0.15;

        var pts = new Point3DCollection();
        var idx = new Int32Collection();

        foreach (var bc in bcs)
        {
            var centroid = GetFaceCentroid(bc.FaceId, nodePos);
            if (centroid == null) continue;
            var c = centroid.Value;
            AddBox(pts, idx, c, L, T, T); // X bar
            AddBox(pts, idx, c, T, L, T); // Y bar
            AddBox(pts, idx, c, T, T, L); // Z bar
        }

        ConstraintMarkersGeometry = pts.Count > 0
            ? new MeshGeometry3D { Positions = pts, TriangleIndices = idx }
            : null;
    }

    public void UpdateLoadArrows(IEnumerable<Load> loads)
    {
        if (CurrentMesh == null) { LoadArrowsGeometry = null; return; }

        var nodePos = CurrentMesh.Nodes.ToDictionary(n => n.Id, n => new Point3D(n.X, n.Y, n.Z));
        double diag = ComputeDiagonal(nodePos.Values);
        double totalLen = diag * 0.20;

        var pts = new Point3DCollection();
        var idx = new Int32Collection();

        foreach (var load in loads)
        {
            var centroid = GetFaceCentroid(load.FaceId, nodePos);
            if (centroid == null) continue;

            if (load.Type is LoadType.Torque or LoadType.Moment)
            {
                // Torque/Moment: draw a circular arc arrow (curved glyph) instead of
                // a straight arrow — makes rotation direction immediately obvious.
                Vector3D axis = new(0, 0, 1);
                if (load.Localized?.AxisDirection is { Length: >= 3 } a)
                    axis = new Vector3D(a[0], a[1], a[2]);
                if (axis.Length < 1e-10) axis = new Vector3D(0, 0, 1);
                axis.Normalize();

                // Sized to read clearly as a curl, not a thin line: arc diameter
                // ≈ the linear force-arrow length, with a visibly round tube.
                double arcRadius = totalLen * 0.50;
                double tubeRadius = arcRadius * 0.12;
                double magnitude = load.Localized?.Magnitude ?? load.Magnitude;
                // Negative magnitude → reverse the right-hand-rule sense.
                if (magnitude < 0) axis = -axis;

                AddTorqueGlyph(pts, idx, centroid.Value, axis, arcRadius, tubeRadius);
            }
            else
            {
                // Linear force loads: straight arrow
                // Direction priority: explicit Direction → Localized.Force → default −Z
                Vector3D dir = new(0, 0, -1);
                if (load.Direction is { Length: >= 3 } d)
                    dir = new Vector3D(d[0], d[1], d[2]);
                else if (load.Localized?.Force is { Length: >= 3 } f)
                    dir = new Vector3D(f[0], f[1], f[2]);

                if (dir.Length < 1e-10) dir = new Vector3D(0, 0, -1);
                dir.Normalize();

                AddArrow(pts, idx, centroid.Value, dir, totalLen,
                    shaftR: totalLen * 0.04, headR: totalLen * 0.10, headFrac: 0.25);
            }
        }

        LoadArrowsGeometry = pts.Count > 0
            ? new MeshGeometry3D { Positions = pts, TriangleIndices = idx }
            : null;
    }

    private Point3D? GetFaceCentroid(int faceId, Dictionary<int, Point3D> nodePos)
    {
        if (!_faceTriangles.TryGetValue(faceId, out var tris) || tris.Count == 0) return null;
        double x = 0, y = 0, z = 0;
        int count = 0;
        foreach (var (a, b, c) in tris)
        {
            x += a.X + b.X + c.X;
            y += a.Y + b.Y + c.Y;
            z += a.Z + b.Z + c.Z;
            count += 3;
        }
        return new Point3D(x / count, y / count, z / count);
    }

    private static double ComputeDiagonal(IEnumerable<Point3D> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }
        double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return d > 1e-10 ? d : 1.0;
    }

    private static void AddBox(Point3DCollection pts, Int32Collection idx,
        Point3D center, double hx, double hy, double hz)
    {
        int b = pts.Count;
        foreach (var sx in new[] { -1.0, 1.0 })
            foreach (var sy in new[] { -1.0, 1.0 })
                foreach (var sz in new[] { -1.0, 1.0 })
                    pts.Add(new Point3D(center.X + sx * hx, center.Y + sy * hy, center.Z + sz * hz));
        int[] faces = { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4, 2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5 };
        foreach (var f in faces) idx.Add(b + f);
    }

    /// <summary>
    /// Draws a curved arc-arrow glyph to represent a torque load.
    /// The arc is a 300° torus segment about <paramref name="axis"/> centred at
    /// <paramref name="center"/>, with a cone arrowhead at the arc end.
    /// The sense of rotation follows the right-hand rule about <paramref name="axis"/>.
    /// </summary>
    private static void AddTorqueGlyph(
        Point3DCollection pts, Int32Collection idx,
        Point3D center, Vector3D axis,
        double arcRadius, double tubeRadius)
    {
        axis.Normalize();

        // Build orthonormal basis (u, v) in the plane perpendicular to axis such
        // that positive rotation from u → v about axis follows the right-hand rule.
        var upRef = Math.Abs(Vector3D.DotProduct(axis, new Vector3D(1, 0, 0))) < 0.8
            ? new Vector3D(1, 0, 0)
            : new Vector3D(0, 1, 0);
        var u = Vector3D.CrossProduct(axis, upRef); u.Normalize(); // u ⊥ axis, ⊥ upRef
        var v = Vector3D.CrossProduct(axis, u);     v.Normalize(); // v ⊥ axis, ⊥ u; arc u→v is CCW about axis

        const int arcSegs  = 48;   // smoother spine so the curl never reads as facets
        const int tubeSegs = 14;   // rounder tube cross-section
        const double arcDeg = 300.0;
        double arcAngle = arcDeg * Math.PI / 180.0;

        int baseIdx = pts.Count;

        // Tube rings along the arc
        for (int i = 0; i <= arcSegs; i++)
        {
            double theta = arcAngle * i / arcSegs;
            // Outward radial from axis to arc spine point
            var r_hat = Math.Cos(theta) * u + Math.Sin(theta) * v;
            var arcPt = center + arcRadius * r_hat;

            for (int j = 0; j < tubeSegs; j++)
            {
                double phi = 2 * Math.PI * j / tubeSegs;
                // Tube cross-section: circle in the plane of r_hat and axis
                var off = tubeRadius * (Math.Cos(phi) * r_hat + Math.Sin(phi) * axis);
                pts.Add(arcPt + off);
            }
        }

        // Triangulated tube surface
        for (int i = 0; i < arcSegs; i++)
        {
            for (int j = 0; j < tubeSegs; j++)
            {
                int jn = (j + 1) % tubeSegs;
                int a0 = baseIdx + i * tubeSegs + j;
                int b0 = baseIdx + i * tubeSegs + jn;
                int c0 = baseIdx + (i + 1) * tubeSegs + j;
                int d0 = baseIdx + (i + 1) * tubeSegs + jn;
                idx.Add(a0); idx.Add(c0); idx.Add(b0);
                idx.Add(b0); idx.Add(c0); idx.Add(d0);
            }
        }

        // Arrowhead cone at arc end (theta = arcAngle)
        var endR_hat  = Math.Cos(arcAngle) * u + Math.Sin(arcAngle) * v;
        var endPt     = center + arcRadius * endR_hat;
        // Tangent direction of the arc at the end point (derivative of arc position wrt theta)
        var tangent   = -Math.Sin(arcAngle) * u + Math.Cos(arcAngle) * v;
        tangent.Normalize();

        // Prominent arrowhead so the rotational sense (right-hand rule about the
        // applied axis) is obvious at a glance.
        double headLen = tubeRadius * 4.5;
        double headR   = tubeRadius * 2.8;
        var tipPt      = endPt + headLen * tangent;

        int headBase = pts.Count;
        for (int j = 0; j < tubeSegs; j++)
        {
            double phi = 2 * Math.PI * j / tubeSegs;
            // Cone base circle in the endR_hat / axis plane
            var off = headR * (Math.Cos(phi) * endR_hat + Math.Sin(phi) * axis);
            pts.Add(endPt + off);
        }
        pts.Add(tipPt);

        for (int j = 0; j < tubeSegs; j++)
        {
            int jn = (j + 1) % tubeSegs;
            idx.Add(headBase + j); idx.Add(headBase + jn); idx.Add(headBase + tubeSegs);
        }
    }

    private static void AddArrow(Point3DCollection pts, Int32Collection idx,
        Point3D tail, Vector3D dir, double totalLen, double shaftR, double headR, double headFrac)
    {
        var up = Math.Abs(dir.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var right = Vector3D.CrossProduct(dir, up); right.Normalize();
        var fwd = Vector3D.CrossProduct(right, dir);

        var shaftEnd = tail + dir * totalLen * (1 - headFrac);
        var arrowTip = tail + dir * totalLen;

        int segs = 6;
        int b = pts.Count;
        for (int i = 0; i < segs; i++)
        {
            double a = 2 * Math.PI * i / segs;
            var off = right * Math.Cos(a) * shaftR + fwd * Math.Sin(a) * shaftR;
            pts.Add(tail + off);
            pts.Add(shaftEnd + off);
        }
        for (int i = 0; i < segs; i++)
        {
            int n = (i + 1) % segs;
            idx.Add(b + i * 2);     idx.Add(b + n * 2);     idx.Add(b + i * 2 + 1);
            idx.Add(b + n * 2);     idx.Add(b + n * 2 + 1); idx.Add(b + i * 2 + 1);
        }
        int hb = pts.Count;
        for (int i = 0; i < segs; i++)
        {
            double a = 2 * Math.PI * i / segs;
            var off = right * Math.Cos(a) * headR + fwd * Math.Sin(a) * headR;
            pts.Add(shaftEnd + off);
        }
        pts.Add(arrowTip);
        for (int i = 0; i < segs; i++)
        {
            int n = (i + 1) % segs;
            idx.Add(hb + i); idx.Add(hb + n); idx.Add(hb + segs);
        }
    }
}
