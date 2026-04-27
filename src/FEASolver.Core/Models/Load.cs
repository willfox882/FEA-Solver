using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FEASolver.Core.Models;

[JsonConverter(typeof(StringEnumConverter))]
public enum LoadType
{
    SurfaceTraction,
    Pressure,
    PointLoad,
    Moment,
    Torque,
    /// <summary>
    /// Total force (N) distributed across a face via consistent Galerkin
    /// lumping. Magnitude = |F|, Direction = unit vector. The Python INP
    /// writer converts to an equivalent surface traction of F/A and lumps,
    /// so Σ CLOAD equals F exactly regardless of mesh density.
    /// </summary>
    DistributedForce
}

[JsonConverter(typeof(StringEnumConverter))]
public enum LocationMode
{
    NearestNode,
    Parametric,
    AbsoluteXYZ
}

/// <summary>
/// How a surface load's direction vector is interpreted. When Normal*, the
/// INP writer computes the outward normal per element-face so tilted or
/// curved surfaces don't require the user to type a unit vector.
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum DirectionMode
{
    Explicit,
    NormalOutward,
    NormalInward
}

public record LocalizedLoadData(
    [property: JsonProperty("mode")] LocationMode Mode,
    [property: JsonProperty("xyz")] double[]? Xyz,
    [property: JsonProperty("uv")] double[]? Uv,
    [property: JsonProperty("force")] double[]? Force,
    [property: JsonProperty("axis_direction")] double[]? AxisDirection,
    [property: JsonProperty("magnitude")] double? Magnitude,
    [property: JsonProperty("resolved_node_id")] int? ResolvedNodeId);

public record Load(
    [property: JsonProperty("type")] LoadType Type,
    [property: JsonProperty("face_id")] int FaceId,
    [property: JsonProperty("magnitude")] double Magnitude,
    [property: JsonProperty("direction")] double[]? Direction,
    [property: JsonProperty("localized")] LocalizedLoadData? Localized,
    [property: JsonProperty("direction_mode",
        DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
    DirectionMode DirectionMode = DirectionMode.Explicit);
