using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FEASolver.Core.Models;

[JsonConverter(typeof(StringEnumConverter))]
public enum BcType
{
    Fixed,
    RollerX,
    RollerY,
    RollerZ,
    Pinned
}

public record BoundaryCondition(
    [property: JsonProperty("type")] BcType Type,
    [property: JsonProperty("face_id")] int FaceId,
    [property: JsonProperty("node_ids")] int[] NodeIds);
