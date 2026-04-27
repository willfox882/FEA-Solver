using Newtonsoft.Json;

namespace FEASolver.Core.Models;

public record MaterialModel(
    [property: JsonProperty("name")] string Name,
    [property: JsonProperty("youngs_modulus")] double YoungsModulus,
    [property: JsonProperty("poissons_ratio")] double PoissonsRatio);
