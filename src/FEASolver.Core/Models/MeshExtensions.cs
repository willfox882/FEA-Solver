namespace FEASolver.Core.Models;

/// <summary>
/// Stable CAD-face → mesh-entity mapping built once per MeshData. Used by
/// ResolveBcNodes / ResolveLoadNodes / ValidateModel and by the UI layer to
/// reject Apply on faces with empty mesh sets.
/// </summary>
public sealed class MeshFaceMap
{
    private readonly Dictionary<int, FaceGroup> _byFaceId;

    public MeshFaceMap(MeshData mesh)
    {
        _byFaceId = new Dictionary<int, FaceGroup>(mesh.FaceGroups.Length);
        foreach (var fg in mesh.FaceGroups)
        {
            // Keep the first occurrence; later duplicates would just overwrite.
            _byFaceId.TryAdd(fg.FaceId, fg);
        }
    }

    public bool TryGet(int faceId, out FaceGroup fg) =>
        _byFaceId.TryGetValue(faceId, out fg!);

    public IReadOnlyCollection<int> KnownFaceIds => _byFaceId.Keys;

    public bool HasNodes(int faceId) =>
        _byFaceId.TryGetValue(faceId, out var fg) && fg.NodeIds.Length > 0;

    public bool HasElementFaces(int faceId) =>
        _byFaceId.TryGetValue(faceId, out var fg) && fg.ElementFaces.Length > 0;
}

/// <summary>
/// Extension methods on MeshData for C#-side computations.
/// </summary>
public static class MeshExtensions
{
    /// <summary>Builds a reusable CAD-face-id → FaceGroup map.</summary>
    public static MeshFaceMap BuildFaceMap(this MeshData mesh) => new(mesh);
    public static BoundingBox3D GetBoundingBox(this MeshData mesh)
    {
        if (mesh.Nodes.Length == 0)
            return new BoundingBox3D(0, 0, 0, 0, 0, 0);

        double xmin = double.MaxValue, ymin = double.MaxValue, zmin = double.MaxValue;
        double xmax = double.MinValue, ymax = double.MinValue, zmax = double.MinValue;

        foreach (var n in mesh.Nodes)
        {
            if (n.X < xmin) xmin = n.X;
            if (n.Y < ymin) ymin = n.Y;
            if (n.Z < zmin) zmin = n.Z;
            if (n.X > xmax) xmax = n.X;
            if (n.Y > ymax) ymax = n.Y;
            if (n.Z > zmax) zmax = n.Z;
        }

        return new BoundingBox3D(xmin, ymin, zmin, xmax, ymax, zmax);
    }

    public static MeshStats GetStats(this MeshData mesh)
    {
        var byType = mesh.Elements
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Count());
        return new MeshStats(
            mesh.Nodes.Length,
            mesh.Elements.Length,
            mesh.FaceGroups.Length,
            byType,
            mesh.GetBoundingBox());
    }

    /// <summary>
    /// Returns nodes belonging to a face group, ordered by ID.
    /// </summary>
    public static Node[] NodesOnFace(this MeshData mesh, int faceId)
    {
        var fg = mesh.FaceGroups.FirstOrDefault(f => f.FaceId == faceId);
        if (fg == null) return [];

        var nodeDict = mesh.Nodes.ToDictionary(n => n.Id);
        return fg.NodeIds
            .Where(nodeDict.ContainsKey)
            .Select(id => nodeDict[id])
            .ToArray();
    }

    /// <summary>
    /// Returns the node on the given face closest to targetXYZ.
    /// </summary>
    public static Node? NearestNodeOnFace(this MeshData mesh, int faceId,
                                           double tx, double ty, double tz)
    {
        var candidates = mesh.NodesOnFace(faceId);
        if (candidates.Length == 0) return null;

        return candidates.MinBy(n =>
            Math.Sqrt((n.X - tx) * (n.X - tx) +
                      (n.Y - ty) * (n.Y - ty) +
                      (n.Z - tz) * (n.Z - tz)));
    }

    /// <summary>
    /// Re-resolves face node_ids for all BCs from the current mesh face groups.
    /// Always uses the mesh's authoritative node set — never trusts pre-stored
    /// NodeIds, which become stale after a remesh.
    /// </summary>
    public static BoundaryCondition[] ResolveBcNodes(
        this MeshData mesh, BoundaryCondition[] bcs)
    {
        var map = mesh.BuildFaceMap();
        var result = new List<BoundaryCondition>(bcs.Length);
        foreach (var bc in bcs)
        {
            if (!map.TryGet(bc.FaceId, out var fg)) continue;
            if (fg.ElementFaces.Length == 0) continue;
            if (fg.NodeIds.Length == 0)     continue;

            // Always re-resolve from the current mesh face group.
            result.Add(bc with { NodeIds = fg.NodeIds.ToArray() });
        }
        return result.ToArray();
    }

    /// <summary>
    /// Returns the set of non-empty element-face lists for every face referenced
    /// by <paramref name="loads"/>. Any load whose face has zero element-faces
    /// is excluded. Used by INP generation pre-flight to guarantee that
    /// SURFACE/ELSET emission never writes an empty set.
    /// </summary>
    public static Dictionary<int, int[][]> ResolveLoadFaces(
        this MeshData mesh, Load[] loads)
    {
        var map = mesh.BuildFaceMap();
        var result = new Dictionary<int, int[][]>();
        foreach (var ld in loads)
        {
            if (result.ContainsKey(ld.FaceId)) continue;
            if (!map.TryGet(ld.FaceId, out var fg)) continue;

            // Drop malformed element-face entries defensively: every entry
            // must be [elem_id, face_index] with face_index in 0..3.
            var cleaned = fg.ElementFaces
                .Where(ef => ef is { Length: >= 2 } && ef[1] >= 0 && ef[1] <= 3)
                .Select(ef => new[] { ef[0], ef[1] })
                .ToArray();
            if (cleaned.Length == 0) continue;
            result[ld.FaceId] = cleaned;
        }
        return result;
    }

    /// <summary>
    /// Re-resolves all load face mappings from the current mesh. For point loads,
    /// always re-resolves nearest node (prior ResolvedNodeId may be stale after
    /// remesh). For distributed loads, validates face has element-faces.
    /// </summary>
    public static Load[] ResolveLoadNodes(this MeshData mesh, Load[] loads)
    {
        var map = mesh.BuildFaceMap();
        var result = new List<Load>(loads.Length);
        foreach (var load in loads)
        {
            if (!map.TryGet(load.FaceId, out var fg))
                continue;

            // Distributed loads need at least one element-face on the referenced face.
            if (load.Type is LoadType.Pressure or LoadType.SurfaceTraction
                             or LoadType.DistributedForce)
            {
                if (fg.ElementFaces.Length == 0)
                    continue;
                result.Add(load);
                continue;
            }

            // Moment/Torque: need nodes to distribute forces across.
            if (load.Type is LoadType.Moment or LoadType.Torque)
            {
                if (fg.NodeIds.Length == 0)
                    continue;
                result.Add(load);
                continue;
            }

            // Point load: always re-resolve nearest node from current mesh.
            if (load.Localized is null) { result.Add(load); continue; }
            var loc = load.Localized;

            if (fg.NodeIds.Length == 0)
                continue;

            var xyz = loc.Xyz;
            double tx = xyz is { Length: >= 3 } ? xyz[0] : 0;
            double ty = xyz is { Length: >= 3 } ? xyz[1] : 0;
            double tz = xyz is { Length: >= 3 } ? xyz[2] : 0;
            var nearest = mesh.NearestNodeOnFace(load.FaceId, tx, ty, tz);
            int resolvedId = nearest?.Id ?? fg.NodeIds[0];
            result.Add(load with { Localized = loc with { ResolvedNodeId = resolvedId } });
        }
        return result.ToArray();
    }

    /// <summary>
    /// Validates that every BC and Load references a known face with a non-empty
    /// set of mesh entities. Returns a list of human-readable error strings.
    /// </summary>
    public static List<string> ValidateModel(
        this MeshData mesh, BoundaryCondition[] bcs, Load[] loads)
    {
        var errors = new List<string>();
        var faceMap = new Dictionary<int, FaceGroup>();
        foreach (var f in mesh.FaceGroups) faceMap.TryAdd(f.FaceId, f);

        if (mesh.Nodes.Length == 0) errors.Add("Mesh has no nodes.");
        if (mesh.Elements.Length == 0) errors.Add("Mesh has no elements.");
        if (bcs.Length == 0)
            errors.Add("No boundary conditions defined — model will be singular.");
        if (loads.Length == 0)
            errors.Add("No loads defined — nothing to solve.");

        // Rigid body motion check: verify all 3 translational DOFs are constrained
        // by at least one BC. Solid elements (C3D4/C3D10) have no rotational DOFs,
        // so translation-only is the minimum; rotation is prevented when multiple
        // non-collinear nodes are fixed.
        if (bcs.Length > 0)
        {
            var constrainedDofs = new HashSet<int>();
            foreach (var bc in bcs)
            {
                if (!faceMap.ContainsKey(bc.FaceId)) continue;
                var resolved = bc.NodeIds.Length > 0 ? bc.NodeIds
                    : (faceMap.TryGetValue(bc.FaceId, out var fgRbm) ? fgRbm.NodeIds : []);
                if (resolved.Length == 0) continue;

                foreach (int dof in bc.Type switch
                {
                    BcType.Fixed   => new[] { 1, 2, 3 },
                    BcType.Pinned  => new[] { 1, 2, 3 },
                    BcType.RollerX => new[] { 1 },
                    BcType.RollerY => new[] { 2 },
                    BcType.RollerZ => new[] { 3 },
                    _ => Array.Empty<int>()
                })
                    constrainedDofs.Add(dof);
            }

            if (!constrainedDofs.Contains(1))
                errors.Add("Model is unconstrained in X — no BC constrains DOF 1. Structure will undergo rigid body motion.");
            if (!constrainedDofs.Contains(2))
                errors.Add("Model is unconstrained in Y — no BC constrains DOF 2. Structure will undergo rigid body motion.");
            if (!constrainedDofs.Contains(3))
                errors.Add("Model is unconstrained in Z — no BC constrains DOF 3. Structure will undergo rigid body motion.");
        }

        for (int i = 0; i < bcs.Length; i++)
        {
            var bc = bcs[i];
            if (!faceMap.TryGetValue(bc.FaceId, out var fg))
            {
                errors.Add($"BC #{i + 1} ({bc.Type}) references face {bc.FaceId} which no longer exists in the mesh.");
                continue;
            }
            var resolved = bc.NodeIds.Length > 0 ? bc.NodeIds : fg.NodeIds;
            if (resolved.Length == 0)
                errors.Add($"BC #{i + 1} ({bc.Type}) on face {bc.FaceId} has no associated nodes (NSET would be empty).");
            if (fg.ElementFaces.Length == 0)
                errors.Add($"BC #{i + 1} ({bc.Type}) on face {bc.FaceId} has no element-faces — face is not connected to any solid elements.");
        }

        for (int i = 0; i < loads.Length; i++)
        {
            var ld = loads[i];
            if (!faceMap.TryGetValue(ld.FaceId, out var fg))
            {
                errors.Add($"Load #{i + 1} ({ld.Type}) references face {ld.FaceId} which no longer exists in the mesh.");
                continue;
            }
            if (ld.Type is LoadType.Pressure or LoadType.SurfaceTraction
                            or LoadType.DistributedForce)
            {
                if (fg.ElementFaces.Length == 0)
                    errors.Add($"Load #{i + 1} ({ld.Type}) on face {ld.FaceId} has no element-faces (cannot apply distributed load).");
                if (Math.Abs(ld.Magnitude) < 1e-15)
                    errors.Add($"Load #{i + 1} ({ld.Type}) on face {ld.FaceId} has zero magnitude.");
            }
            else if (ld.Type is LoadType.PointLoad)
            {
                if (fg.NodeIds.Length == 0)
                    errors.Add($"Load #{i + 1} (PointLoad) on face {ld.FaceId} has no nodes to attach to.");
                if (fg.ElementFaces.Length == 0)
                    errors.Add($"Load #{i + 1} (PointLoad) on face {ld.FaceId} has no element-faces — face is not connected to any solid elements.");
                double fx = 0, fy = 0, fz = 0;
                if (ld.Localized?.Force is { Length: >= 3 } f) { fx = f[0]; fy = f[1]; fz = f[2]; }
                if (Math.Abs(fx) + Math.Abs(fy) + Math.Abs(fz) < 1e-15)
                    errors.Add($"Load #{i + 1} (PointLoad) on face {ld.FaceId} has zero force vector.");
            }
            else if (ld.Type is LoadType.Moment or LoadType.Torque)
            {
                if (fg.NodeIds.Length == 0)
                    errors.Add($"Load #{i + 1} ({ld.Type}) on face {ld.FaceId} has no nodes to distribute forces across.");
                if (fg.ElementFaces.Length == 0)
                    errors.Add($"Load #{i + 1} ({ld.Type}) on face {ld.FaceId} has no element-faces.");
                double mag = ld.Localized?.Magnitude ?? ld.Magnitude;
                if (Math.Abs(mag) < 1e-15)
                    errors.Add($"Load #{i + 1} ({ld.Type}) on face {ld.FaceId} has zero magnitude.");
            }
        }
        return errors;
    }
}

public record BoundingBox3D(
    double XMin, double YMin, double ZMin,
    double XMax, double YMax, double ZMax)
{
    public double SizeX => XMax - XMin;
    public double SizeY => YMax - YMin;
    public double SizeZ => ZMax - ZMin;
    public double Diagonal => Math.Sqrt(SizeX * SizeX + SizeY * SizeY + SizeZ * SizeZ);

    public (double X, double Y, double Z) Center => (
        (XMin + XMax) / 2,
        (YMin + YMax) / 2,
        (ZMin + ZMax) / 2);

    /// <summary>Display string in mm.</summary>
    public string ToDisplayString() =>
        $"X:[{XMin*1000:F2}, {XMax*1000:F2}]  " +
        $"Y:[{YMin*1000:F2}, {YMax*1000:F2}]  " +
        $"Z:[{ZMin*1000:F2}, {ZMax*1000:F2}] mm";
}

public record MeshStats(
    int NodeCount,
    int ElementCount,
    int FaceCount,
    Dictionary<string, int> ElementsByType,
    BoundingBox3D BoundingBox)
{
    public string Summary =>
        $"Nodes: {NodeCount:N0}  Elements: {ElementCount:N0}  Faces: {FaceCount}  " +
        $"Bbox: {BoundingBox.ToDisplayString()}";
}
