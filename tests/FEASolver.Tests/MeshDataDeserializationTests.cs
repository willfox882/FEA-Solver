using FEASolver.Core.Models;
using Newtonsoft.Json;
using Xunit;

namespace FEASolver.Tests;

/// <summary>
/// Verifies C# deserialization of JSON produced by mesh_io.py.
/// The JSON fixture below is the exact format written by write_mesh_data().
/// </summary>
public class MeshDataDeserializationTests
{
    // Compact JSON matching mesh_io.py output format exactly
    private const string MeshJson = """
        {
          "nodes": [
            {"id":1,"x":0.0,"y":0.0,"z":0.0},
            {"id":2,"x":1.0,"y":0.0,"z":0.0},
            {"id":3,"x":0.0,"y":1.0,"z":0.0},
            {"id":4,"x":0.0,"y":0.0,"z":1.0}
          ],
          "elements": [
            {"id":1,"type":"C3D4","nodes":[1,2,3,4]}
          ],
          "faces": [
            {"face_id":1,"element_faces":[[1,0]],"node_ids":[1,2,3]},
            {"face_id":2,"element_faces":[[1,3]],"node_ids":[1,3,4]}
          ]
        }
        """;

    [Fact]
    public void Deserialize_MeshData_NodeCount()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        Assert.Equal(4, mesh.Nodes.Length);
    }

    [Fact]
    public void Deserialize_MeshData_ElementType()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        Assert.Equal("C3D4", mesh.Elements[0].Type);
        Assert.Equal(4, mesh.Elements[0].Nodes.Length);
    }

    [Fact]
    public void Deserialize_MeshData_FaceGroups()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        Assert.Equal(2, mesh.FaceGroups.Length);
        Assert.Equal(1, mesh.FaceGroups[0].FaceId);
        Assert.Equal(new[] { 1, 2, 3 }, mesh.FaceGroups[0].NodeIds);
    }

    [Fact]
    public void Deserialize_MeshData_ElementFaces_NestedArray()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        var ef = mesh.FaceGroups[0].ElementFaces;
        Assert.Single(ef);
        Assert.Equal(1, ef[0][0]);  // element id
        Assert.Equal(0, ef[0][1]);  // face index
    }

    [Fact]
    public void Deserialize_MeshData_NodeCoords()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        var n2 = mesh.Nodes.First(n => n.Id == 2);
        Assert.Equal(1.0, n2.X, precision: 10);
        Assert.Equal(0.0, n2.Y, precision: 10);
    }

    [Fact]
    public void BoundingBox_CorrectExtents()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        var bb = mesh.GetBoundingBox();
        Assert.Equal(0.0, bb.XMin, 10);
        Assert.Equal(1.0, bb.XMax, 10);
        Assert.Equal(1.0, bb.YMax, 10);
        Assert.Equal(1.0, bb.ZMax, 10);
    }

    [Fact]
    public void BoundingBox_Diagonal_SqrtThree()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        var bb = mesh.GetBoundingBox();
        Assert.Equal(Math.Sqrt(3.0), bb.Diagonal, 10);
    }

    [Fact]
    public void MeshStats_Counts()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        var stats = mesh.GetStats();
        Assert.Equal(4, stats.NodeCount);
        Assert.Equal(1, stats.ElementCount);
        Assert.Equal(2, stats.FaceCount);
        Assert.Equal(1, stats.ElementsByType["C3D4"]);
    }

    [Fact]
    public void NodesOnFace_ReturnsCorrectNodes()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        var nodes = mesh.NodesOnFace(1);
        Assert.Equal(3, nodes.Length);
        Assert.Contains(nodes, n => n.Id == 1);
        Assert.Contains(nodes, n => n.Id == 2);
        Assert.Contains(nodes, n => n.Id == 3);
    }

    [Fact]
    public void NearestNodeOnFace_FindsClosest()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        var nearest = mesh.NearestNodeOnFace(1, 0.9, 0.1, 0.0);
        Assert.NotNull(nearest);
        Assert.Equal(2, nearest!.Id);  // node 2 is at (1,0,0)
    }

    [Fact]
    public void ResolveBcNodes_PopulatesFromFace()
    {
        var mesh = JsonConvert.DeserializeObject<MeshData>(MeshJson)!;
        var bcs = new[]
        {
            new BoundaryCondition(BcType.Fixed, 1, [])  // empty node_ids
        };
        var resolved = mesh.ResolveBcNodes(bcs);
        Assert.Equal(3, resolved[0].NodeIds.Length);
    }
}
