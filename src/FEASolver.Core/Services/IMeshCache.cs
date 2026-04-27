namespace FEASolver.Core.Services;

/// <summary>
/// Checks whether a cached mesh result is still valid before invoking Python.
/// Cache is file-based (.mesh_cache in work dir), validated by content hash + params.
/// </summary>
public interface IMeshCache
{
    /// <summary>
    /// Cache key MUST include actual element size used and the input units —
    /// otherwise re-meshing with a different size or unit returns stale data.
    /// </summary>
    bool IsValid(string workDir, string stepPath, MeshingSettings settings,
                 double elementSize, string inputUnits);
    void Invalidate(string workDir);
}
