using Newtonsoft.Json;

namespace FEASolver.Core.Models;

public record ResultField(
    [property: JsonProperty("node_ids")] int[] NodeIds,
    [property: JsonProperty("values")] double[] Values);

public record ResultSet(
    [property: JsonProperty("step")] int Step,
    [property: JsonProperty("displacement_x")] ResultField? DisplacementX,
    [property: JsonProperty("displacement_y")] ResultField? DisplacementY,
    [property: JsonProperty("displacement_z")] ResultField? DisplacementZ,
    [property: JsonProperty("displacement_mag")] ResultField? DisplacementMag,
    [property: JsonProperty("vonmises")] ResultField? VonMises,
    // Full Cauchy stress tensor (Pa). Optional — older results.json may omit them.
    [property: JsonProperty("s11")] ResultField? S11 = null,
    [property: JsonProperty("s22")] ResultField? S22 = null,
    [property: JsonProperty("s33")] ResultField? S33 = null,
    [property: JsonProperty("s12")] ResultField? S12 = null,
    [property: JsonProperty("s23")] ResultField? S23 = null,
    [property: JsonProperty("s13")] ResultField? S13 = null,
    // Nodal reaction forces (N). Used to verify equilibrium post-solve.
    [property: JsonProperty("reaction_x")] ResultField? ReactionX = null,
    [property: JsonProperty("reaction_y")] ResultField? ReactionY = null,
    [property: JsonProperty("reaction_z")] ResultField? ReactionZ = null,
    // Strain tensor components (dimensionless). Tensor shears (γ_ij/2).
    [property: JsonProperty("e11")] ResultField? E11 = null,
    [property: JsonProperty("e22")] ResultField? E22 = null,
    [property: JsonProperty("e33")] ResultField? E33 = null,
    [property: JsonProperty("e12")] ResultField? E12 = null,
    [property: JsonProperty("e23")] ResultField? E23 = null,
    [property: JsonProperty("e13")] ResultField? E13 = null,
    [property: JsonProperty("strain_vonmises")] ResultField? StrainVonMises = null);
