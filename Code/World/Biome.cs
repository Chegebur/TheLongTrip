namespace LongTrip.World;

/// <summary>
/// Everything the world generator needs to know about one biome: how its ground is shaped and how
/// much of each ground material shows up. The actual materials (sand, grass, snow...) live in
/// <c>Assets/shaders/terrain_pbr.shader</c> - each biome's slot picks a row of that shader's layer table.
/// </summary>
public sealed class Biome
{
	public string Name { get; init; }

	// ---- Ground materials ----------------------------------------------------------------

	/// <summary>Largest share of the ground the "alt" material may take (0 = never shows).</summary>
	public float AltAmount { get; init; } = 0.6f;

	/// <summary>Largest share of the ground the "extra" material may take.</summary>
	public float ExtraAmount { get; init; } = 0.5f;

	// ---- Shape (all distances in world units, 1 unit = 1 inch) ---------------------------

	/// <summary>Height of broad rolling hills.</summary>
	public float HillHeight { get; init; }

	/// <summary>Horizontal size of those hills.</summary>
	public float HillScale { get; init; } = 8000f;

	/// <summary>Height of sharp-crested ridges (dunes, drifts).</summary>
	public float RidgeHeight { get; init; }

	/// <summary>Horizontal size of ridges.</summary>
	public float RidgeScale { get; init; } = 3000f;

	/// <summary>How much ridges are stretched sideways (1 = round, &gt;1 = long dune lines).</summary>
	public float RidgeStretch { get; init; } = 1f;

	/// <summary>Small bumps that break up flat ground. Kept gentle so the ground stays smooth to drive on.</summary>
	public float DetailHeight { get; init; } = 8f;

	public float DetailScale { get; init; } = 1400f;

	/// <summary>
	/// Height of the large landforms (mesas, hills, ranges) that rise away from the road in some places.
	/// The road corridor itself always stays open.
	/// </summary>
	public float MountainHeight { get; init; } = 0f;
}

public static class BiomeLibrary
{
	public static readonly Biome Desert = new()
	{
		Name = "Desert",
		AltAmount = 0.65f,
		ExtraAmount = 0.7f,
		HillHeight = 380f,
		HillScale = 9000f,
		RidgeHeight = 420f,
		RidgeScale = 3200f,
		RidgeStretch = 2.2f,
		DetailHeight = 8f,
		MountainHeight = 2600f,
	};

	public static readonly Biome Grasslands = new()
	{
		Name = "Grasslands",
		AltAmount = 0.55f,
		ExtraAmount = 0.35f,
		HillHeight = 650f,
		HillScale = 7000f,
		RidgeHeight = 60f,
		RidgeScale = 2000f,
		DetailHeight = 10f,
		MountainHeight = 1800f,
	};

	public static readonly Biome IcePlains = new()
	{
		Name = "Ice Plains",
		AltAmount = 0.4f,   // trampled snow patches
		ExtraAmount = 0f,
		HillHeight = 160f,
		HillScale = 11000f,
		RidgeHeight = 90f,
		RidgeScale = 2600f,
		RidgeStretch = 3f,
		DetailHeight = 5f,
		MountainHeight = 3200f,
	};

	/// <summary>Dense woodland: gently rolling, soft forest floor, trees everywhere (see scatter.json). The biome the road starts in.</summary>
	public static readonly Biome Forest = new()
	{
		Name = "Forest",
		AltAmount = 0.55f,   // bare dirt between the trees
		ExtraAmount = 0.45f, // leafy grass
		HillHeight = 520f,
		HillScale = 6000f,
		RidgeHeight = 40f,
		RidgeScale = 1800f,
		DetailHeight = 10f,
		MountainHeight = 1500f,
	};

	/// <summary>
	/// All biomes the generator can pick from. The starting biome is <see cref="Forest"/> (see <c>WorldGenerator.StartBiome</c>). The index is also the
	/// biome's slot in the terrain shader (<c>BIOME_TABLE</c>), which currently has room for 4.
	/// </summary>
	public static readonly Biome[] All = { Desert, Grasslands, IcePlains, Forest };

	public const int MaxBiomes = 4;
}
