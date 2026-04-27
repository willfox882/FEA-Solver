using Newtonsoft.Json;
using Xunit;
using FEASolver.Core.Models;

namespace FEASolver.Tests;

public class ModelSerializationTests
{
    [Fact]
    public void MeshData_RoundTrip_PreservesNodeCount()
    {
        var mesh = new MeshData(
            Nodes: [new Node(1, 0, 0, 0), new Node(2, 1, 0, 0)],
            Elements: [new Element(1, "C3D4", [1, 2, 3, 4])],
            FaceGroups: [new FaceGroup(1, [[1, 0]], [1, 2])]
        );

        string json = JsonConvert.SerializeObject(mesh);
        var result = JsonConvert.DeserializeObject<MeshData>(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Nodes.Length);
        Assert.Equal(1, result.Elements.Length);
        Assert.Equal(1, result.FaceGroups.Length);
    }

    [Fact]
    public void BoundaryCondition_Fixed_Serializes()
    {
        var bc = new BoundaryCondition(BcType.Fixed, 1, [10, 11, 12]);
        string json = JsonConvert.SerializeObject(bc);

        Assert.Contains("\"Fixed\"", json);
        Assert.Contains("\"face_id\":1", json);
    }

    [Fact]
    public void Load_SurfaceTraction_Serializes()
    {
        var load = new Load(LoadType.SurfaceTraction, 2, 5000.0,
            [0, 0, -1], null);
        string json = JsonConvert.SerializeObject(load);

        Assert.Contains("\"SurfaceTraction\"", json);
        Assert.Contains("5000", json);
    }
}
