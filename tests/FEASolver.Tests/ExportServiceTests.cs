using FEASolver.Core.Models;
using FEASolver.Core.Services;
using Xunit;

namespace FEASolver.Tests;

public class ExportServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MeshData MakeMesh() => new MeshData(
        Nodes: [
            new Node(1, 0.0, 0.0, 0.0),
            new Node(2, 0.01, 0.0, 0.0),
            new Node(3, 0.0, 0.01, 0.0),
            new Node(4, 0.0, 0.0, 0.01),
        ],
        Elements: [new Element(1, "C3D4", [1, 2, 3, 4])],
        FaceGroups: [
            new FaceGroup(1, [[1, 0]], [1, 2, 3]),
        ]);

    private static ResultSet MakeResults() => new ResultSet(
        Step: 1,
        DisplacementX: new ResultField([1, 2, 3, 4], [0.0, 1e-4, 0.0, 0.0]),
        DisplacementY: new ResultField([1, 2, 3, 4], [0.0, 0.0, 2e-4, 0.0]),
        DisplacementZ: new ResultField([1, 2, 3, 4], [0.0, 0.0, 0.0, 3e-4]),
        DisplacementMag: new ResultField([1, 2, 3, 4], [0.0, 1e-4, 2e-4, 3e-4]),
        VonMises: new ResultField([1, 2, 3, 4], [0.0, 1e6, 2e6, 3e6]));

    // ── File creation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteResultsCsv_CreatesFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await ExportService.WriteResultsCsvAsync(path, MakeResults(), MakeMesh());
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteResultsCsv_HasHeaderRow()
    {
        var path = Path.GetTempFileName();
        try
        {
            await ExportService.WriteResultsCsvAsync(path, MakeResults(), MakeMesh());
            var firstLine = File.ReadLines(path).First();
            Assert.Contains("NodeId", firstLine);
            Assert.Contains("VonMises_MPa", firstLine);
            Assert.Contains("Ux_mm", firstLine);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteResultsCsv_RowCountMatchesNodes()
    {
        var path = Path.GetTempFileName();
        try
        {
            await ExportService.WriteResultsCsvAsync(path, MakeResults(), MakeMesh());
            // header + 4 data rows + possible trailing empty line
            var lines = File.ReadLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            Assert.Equal(5, lines.Count); // 1 header + 4 nodes
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteResultsCsv_DisplacementConvertedToMm()
    {
        var path = Path.GetTempFileName();
        try
        {
            await ExportService.WriteResultsCsvAsync(path, MakeResults(), MakeMesh());
            var dataLines = File.ReadLines(path).Skip(1)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            // Node 2: Ux = 1e-4 m → 0.1 mm
            var node2 = dataLines.First(l => l.StartsWith("2,"));
            var cols = node2.Split(',');
            double uxMm = double.Parse(cols[4], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0.1, uxMm, precision: 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteResultsCsv_VonMisesConvertedToMPa()
    {
        var path = Path.GetTempFileName();
        try
        {
            await ExportService.WriteResultsCsvAsync(path, MakeResults(), MakeMesh());
            var dataLines = File.ReadLines(path).Skip(1)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            // Node 2: VM = 1e6 Pa → 1.0 MPa
            var node2 = dataLines.First(l => l.StartsWith("2,"));
            var cols = node2.Split(',');
            double vmMpa = double.Parse(cols[8], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1.0, vmMpa, precision: 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteResultsCsv_NodePositionsWritten()
    {
        var path = Path.GetTempFileName();
        try
        {
            await ExportService.WriteResultsCsvAsync(path, MakeResults(), MakeMesh());
            var dataLines = File.ReadLines(path).Skip(1)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            // Node 2: X = 0.01 m
            var node2 = dataLines.First(l => l.StartsWith("2,"));
            var cols = node2.Split(',');
            double x = double.Parse(cols[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0.01, x, precision: 8);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteResultsCsv_NullFieldsWriteZero()
    {
        var path = Path.GetTempFileName();
        try
        {
            // ResultSet with only VonMises, no displacements
            var results = new ResultSet(1, null, null, null, null,
                VonMises: new ResultField([1, 2], [5e5, 1.5e6]));
            var mesh = MakeMesh();

            await ExportService.WriteResultsCsvAsync(path, results, mesh);
            var dataLines = File.ReadLines(path).Skip(1)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            Assert.True(dataLines.Count > 0);
            // Ux_mm column should be 0 since DisplacementX is null
            var node1 = dataLines.First(l => l.StartsWith("1,"));
            var cols = node1.Split(',');
            Assert.Equal(0.0, double.Parse(cols[4], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteResultsCsv_UsesInvariantCulture()
    {
        var path = Path.GetTempFileName();
        try
        {
            await ExportService.WriteResultsCsvAsync(path, MakeResults(), MakeMesh());
            string content = File.ReadAllText(path);
            // Decimal separator must be '.' not ','
            // A value like 0.1 should appear as "0.1", not "0,1"
            Assert.DoesNotContain("0,1", content.Split('\n').Skip(1).First());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteResultsCsv_CancellationThrows()
    {
        var path = Path.GetTempFileName();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ExportService.WriteResultsCsvAsync(path, MakeResults(), MakeMesh(), cts.Token));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
