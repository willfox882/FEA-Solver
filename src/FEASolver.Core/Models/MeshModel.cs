using Newtonsoft.Json;

namespace FEASolver.Core.Models;

// ── Primitives ──────────────────────────────────────────────────────────────

public record Node(
    [property: JsonProperty("id")] int Id,
    [property: JsonProperty("x")] double X,
    [property: JsonProperty("y")] double Y,
    [property: JsonProperty("z")] double Z);

public record Element(
    [property: JsonProperty("id")] int Id,
    [property: JsonProperty("type")] string Type,
    [property: JsonProperty("nodes")] int[] Nodes);

public record FaceGroup(
    [property: JsonProperty("face_id")] int FaceId,
    [property: JsonProperty("element_faces")] int[][] ElementFaces,
    [property: JsonProperty("node_ids")] int[] NodeIds);

// ── Top-level mesh data (output of mesh_step.py) ────────────────────────────

public record MeshData(
    [property: JsonProperty("nodes")] Node[] Nodes,
    [property: JsonProperty("elements")] Element[] Elements,
    [property: JsonProperty("faces")] FaceGroup[] FaceGroups);

// ── Full FEA model passed to write_inp.py ───────────────────────────────────

public record FEAModel(
    [property: JsonProperty("mesh")] MeshData Mesh,
    [property: JsonProperty("material")] MaterialModel Material,
    [property: JsonProperty("boundary_conditions")] BoundaryCondition[] BCs,
    [property: JsonProperty("loads")] Load[] Loads,
    [property: JsonProperty("work_dir")] string WorkDir);
