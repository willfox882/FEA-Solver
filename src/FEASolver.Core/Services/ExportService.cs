using FEASolver.Core.Models;
using System.Text;

namespace FEASolver.Core.Services;

/// <summary>
/// Exports FEA results to CSV.
/// Columns: NodeId, X_m, Y_m, Z_m, Ux_mm, Uy_mm, Uz_mm, U_mag_mm, VonMises_MPa
/// </summary>
public static class ExportService
{
    public static async Task WriteResultsCsvAsync(
        string path,
        ResultSet results,
        MeshData mesh,
        CancellationToken ct = default)
    {
        var nodePos  = mesh.Nodes.ToDictionary(n => n.Id);
        var dispX    = BuildValueMap(results.DisplacementX);
        var dispY    = BuildValueMap(results.DisplacementY);
        var dispZ    = BuildValueMap(results.DisplacementZ);
        var dispMag  = BuildValueMap(results.DisplacementMag);
        var vonMises = BuildValueMap(results.VonMises);
        var e11      = BuildValueMap(results.E11);
        var e22      = BuildValueMap(results.E22);
        var e33      = BuildValueMap(results.E33);
        var eVm      = BuildValueMap(results.StrainVonMises);

        // Prefer result node IDs; fall back to mesh nodes only if result fields exist
        // but have null NodeIds (shouldn't happen, but defensive)
        var resultField = results.DisplacementMag ?? results.VonMises;
        var nodeIds = resultField?.NodeIds is { Length: > 0 }
            ? resultField.NodeIds
            : mesh.Nodes.Select(n => n.Id).ToArray();

        if (nodeIds.Length == 0)
            throw new InvalidOperationException(
                "No nodes available for export — results and mesh are both empty.");

        var sb = new StringBuilder();
        sb.AppendLine("NodeId,X_m,Y_m,Z_m,Ux_mm,Uy_mm,Uz_mm,U_mag_mm," +
                      "VonMises_MPa,E_xx,E_yy,E_zz,E_vm");

        foreach (int nid in nodeIds)
        {
            double x = 0, y = 0, z = 0;
            if (nodePos.TryGetValue(nid, out var n)) { x = n.X; y = n.Y; z = n.Z; }

            sb.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0},{1:G8},{2:G8},{3:G8},{4:G8},{5:G8},{6:G8},{7:G8},{8:G8}," +
                "{9:G8},{10:G8},{11:G8},{12:G8}",
                nid, x, y, z,
                dispX .GetValueOrDefault(nid) * 1000.0,
                dispY .GetValueOrDefault(nid) * 1000.0,
                dispZ .GetValueOrDefault(nid) * 1000.0,
                dispMag .GetValueOrDefault(nid) * 1000.0,
                vonMises.GetValueOrDefault(nid) / 1e6,
                e11.GetValueOrDefault(nid),
                e22.GetValueOrDefault(nid),
                e33.GetValueOrDefault(nid),
                eVm.GetValueOrDefault(nid)));
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
    }

    private static Dictionary<int, double> BuildValueMap(ResultField? field)
    {
        if (field?.NodeIds == null || field.Values == null) return [];
        int count = Math.Min(field.NodeIds.Length, field.Values.Length);
        var map = new Dictionary<int, double>(count);
        for (int i = 0; i < count; i++)
            map[field.NodeIds[i]] = field.Values[i];
        return map;
    }
}
