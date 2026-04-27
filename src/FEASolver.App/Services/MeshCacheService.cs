using System.IO;
using FEASolver.Core.Services;
using Newtonsoft.Json;
using Serilog;
using System.Security.Cryptography;

namespace FEASolver.Services;

/// <summary>
/// C#-side mesh cache check — mirrors mesh_cache.py logic.
/// The Python script also checks the cache; this C# check avoids launching
/// the Python process entirely on a cache hit.
/// </summary>
public class MeshCacheService : IMeshCache
{
    private const string CacheFile = ".mesh_cache";

    public bool IsValid(string workDir, string stepPath, MeshingSettings settings,
                        double elementSize, string inputUnits)
    {
        string cachePath = Path.Combine(workDir, CacheFile);
        if (!File.Exists(cachePath) || !File.Exists(stepPath))
            return false;

        try
        {
            string json = File.ReadAllText(cachePath);
            var data = JsonConvert.DeserializeObject<CacheData>(json);
            if (data == null) return false;

            string expected = ComputeKey(stepPath, settings, elementSize, inputUnits);
            bool hit = data.Key == expected;
            if (hit) Log.Debug("MeshCache hit for {Step}", stepPath);
            return hit;
        }
        catch (Exception ex)
        {
            Log.Debug("MeshCache read error: {Ex}", ex.Message);
            return false;
        }
    }

    public void Invalidate(string workDir)
    {
        string cachePath = Path.Combine(workDir, CacheFile);
        if (File.Exists(cachePath))
        {
            File.Delete(cachePath);
            Log.Debug("MeshCache invalidated in {Dir}", workDir);
        }
    }

    private static string ComputeKey(string stepPath, MeshingSettings settings,
                                     double elementSize, string inputUnits)
    {
        // Mirror Python make_key() exactly: sha256("{stepHash}|{size}|{order}|{algo}|u={units}")
        // Python writes the hex-digest of UTF-8 bytes; default float repr ("5.0") is used,
        // not G17, so match that by trimming trailing zeros.
        string stepHash = Sha256File(stepPath);
        // "G10" matches Python's f"{x:.10g}" formatting in mesh_cache.make_key.
        string sizeStr = elementSize.ToString("G10", System.Globalization.CultureInfo.InvariantCulture);
        string units = (inputUnits ?? "mm").ToLowerInvariant();
        string payload = $"{stepHash}|{sizeStr}|{settings.ElementOrder}|{settings.Algorithm3D}|u={units}";
        byte[] bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private record CacheData([property: JsonProperty("key")] string Key);
}
