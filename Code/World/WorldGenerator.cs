using System;

namespace LongTrip.World;

/// <summary>Up to two biomes and how far we are between them.</summary>
public readonly struct BiomeBlend
{
	public readonly int A;
	public readonly int B;

	/// <summary>0 = fully <see cref="A"/>, 1 = fully <see cref="B"/>.</summary>
	public readonly float T;

	public BiomeBlend( int a, int b, float t ) { A = a; B = b; T = t; }
}

/// <summary>
/// Per-vertex ground material weights, matching what <c>terrain_pbr.shader</c> expects:
/// four biome weights (sum 1) and four material-role weights (base / alt / slope, plus the road shoulder).
/// The fourth role, "extra", is whatever is left over (1 - base - alt - slope).
/// </summary>
public struct TerrainSplat
{
	public float Base, Alt, Slope, Shoulder;
	public float Biome0, Biome1, Biome2, Biome3;

	public void AddBiome( int slot, float weight )
	{
		switch ( slot )
		{
			case 0: Biome0 += weight; break;
			case 1: Biome1 += weight; break;
			case 2: Biome2 += weight; break;
			default: Biome3 += weight; break;
		}
	}

	/// <summary>Biome weights as raw bytes. Not gamma-converted - these are data, not colours.</summary>
	public Color32 BiomeBytes => new Color32( ToByte( Biome0 ), ToByte( Biome1 ), ToByte( Biome2 ), ToByte( Biome3 ) );

	private static byte ToByte( float v ) => (byte)(Math.Clamp( v, 0f, 1f ) * 255f + 0.5f);
}

/// <summary>
/// Settings that define a world. Same settings + same seed = same world.
/// </summary>
public sealed class WorldSettings
{
	public int Seed { get; set; } = 1337;

	/// <summary>Average length of one biome along the road.</summary>
	public float BiomeLength { get; set; } = 120000f;

	/// <summary>Length of the blend between two neighbouring biomes.</summary>
	public float BiomeTransition { get; set; } = 14000f;

	/// <summary>Full width of the asphalt.</summary>
	public float RoadWidth { get; set; } = 300f;

	/// <summary>Flat strip either side of the asphalt.</summary>
	public float ShoulderWidth { get; set; } = 160f;

	/// <summary>How far past the asphalt the gravel material reaches before fading into the terrain.</summary>
	public float ShoulderMaterialWidth { get; set; } = 130f;

	/// <summary>Distance over which terrain rises from the shoulder back to its natural height.</summary>
	public float RoadBlendWidth { get; set; } = 2600f;

	/// <summary>How far the road wanders sideways.</summary>
	public float RoadMeander { get; set; } = 5000f;

	/// <summary>Height of the very large landforms shared by every biome.</summary>
	public float MacroHeight { get; set; } = 900f;
}

/// <summary>
/// Pure, thread-safe description of the world: given a position it answers which biome it is,
/// how high the ground is, what colour it is and where the road goes.
/// The road runs along +X forever; biomes change as you drive.
/// </summary>
public sealed class WorldGenerator
{
	public WorldSettings Settings { get; }
	public Biome[] Biomes { get; }

	private readonly WorldNoise _macro;
	private readonly WorldNoise _hills;
	private readonly WorldNoise _ridges;
	private readonly WorldNoise _detail;
	private readonly WorldNoise _patches;
	private readonly WorldNoise _road;
	private readonly WorldNoise _warp;
	private readonly WorldNoise _extra;
	private readonly WorldNoise _warp2;
	private readonly WorldNoise _amp;
	private readonly WorldNoise _mount;
	private readonly WorldNoise _civ;
	private readonly float _roadOrigin;

	public WorldGenerator( WorldSettings settings, Biome[] biomes = null )
	{
		Settings = settings;
		Biomes = biomes ?? BiomeLibrary.All;

		int s = settings.Seed;
		_macro = new WorldNoise( s * 31 + 1 );
		_hills = new WorldNoise( s * 31 + 2 );
		_ridges = new WorldNoise( s * 31 + 3 );
		_detail = new WorldNoise( s * 31 + 4 );
		_patches = new WorldNoise( s * 31 + 5 );
		_road = new WorldNoise( s * 31 + 6 );
		_warp = new WorldNoise( s * 31 + 7 );
		_extra = new WorldNoise( s * 31 + 8 );
		_warp2 = new WorldNoise( s * 31 + 9 );
		_amp = new WorldNoise( s * 31 + 10 );
		_mount = new WorldNoise( s * 31 + 11 );
		_civ = new WorldNoise( s * 31 + 12 );

		if ( Biomes.Length > BiomeLibrary.MaxBiomes )
			throw new ArgumentException( $"The terrain shader only has {BiomeLibrary.MaxBiomes} biome slots." );

		BuildRegionTable();
		_roadOrigin = RawRoadY( 0f );
	}

	// ---- Road ------------------------------------------------------------------------------

	private float RawRoadY( float x )
	{
		float m = Settings.RoadMeander;
		return _road.Fbm( x / 60000f, 0.5f, 2 ) * m + _road.Sample( x / 14000f, 7.5f ) * m * 0.15f;
	}

	/// <summary>Sideways (Y) position of the road centre line at a distance X. The road starts at Y = 0.</summary>
	public float RoadCenterY( float x ) => RawRoadY( x ) - _roadOrigin;

	/// <summary>Direction of the road at X, flat on the ground plane.</summary>
	public Vector3 RoadDirection( float x )
	{
		const float e = 32f;
		float dy = RoadCenterY( x + e ) - RoadCenterY( x - e );
		return new Vector3( 2f * e, dy, 0f ).Normal;
	}

	/// <summary>Height of the road surface at X. Uses only the smooth parts of the terrain so the road never gets bumpy.</summary>
	public float RoadHeight( float x )
	{
		float y = RoadCenterY( x );
		var blend = GetBiome( x, y );
		var a = Biomes[blend.A];
		var b = Biomes[blend.B];

		float macro = MacroHeightAt( x, y );
		float hillA = _hills.Fbm( x / a.HillScale, y / a.HillScale, 2 ) * a.HillHeight;
		float hillB = _hills.Fbm( x / b.HillScale, y / b.HillScale, 2 ) * b.HillHeight;

		// Roads are graded: keep only part of the hills so slopes stay drivable.
		return macro + Lerp( hillA, hillB, blend.T ) * 0.35f;
	}

	// ---- Biomes ----------------------------------------------------------------------------

	/// <summary>How many stretches of road have their biome precomputed (4096 x 1.2 km = ~5000 km of road).</summary>
	private const int PrecomputedRegions = 4096;

	private int[] _regionBiomes;

	private void BuildRegionTable()
	{
		int n = Biomes.Length;
		var table = new int[PrecomputedRegions];
		table[0] = 0; // the road always starts in the first biome (Desert)
		for ( int r = 1; r < table.Length; r++ )
		{
			// Always move to a *different* biome than the previous stretch.
			int step = n <= 1 ? 0 : 1 + (int)(WorldNoise.Hash01( Settings.Seed, r, 911 ) * (n - 1)) % (n - 1);
			table[r] = (table[r - 1] + step) % n;
		}
		_regionBiomes = table;
	}

	/// <summary>Biome index for one stretch of road. Never the same biome twice in a row.</summary>
	public int BiomeOfRegion( long region )
	{
		// The start of the road (and anything behind it) is the first biome, so the spawn is never mid-blend.
		if ( region <= 0 ) return 0;
		if ( region < PrecomputedRegions ) return _regionBiomes[region];

		// Beyond the table: still deterministic, just no longer guaranteed to differ from the previous stretch.
		return (int)(WorldNoise.Hash01( Settings.Seed, (int)region, 911 ) * Biomes.Length) % Biomes.Length;
	}

	/// <summary>Which biome(s) a point belongs to. Borders are wavy, not straight lines.</summary>
	public BiomeBlend GetBiome( float x, float y )
	{
		float L = Settings.BiomeLength;
		float half = MathF.Min( Settings.BiomeTransition, L * 0.9f ) * 0.5f;

		// Warp the border so biome edges curve across the landscape.
		float warped = x + _warp.Fbm( x / 25000f, y / 25000f, 2 ) * L * 0.18f;

		long region = (long)MathF.Floor( warped / L );
		float local = warped - region * L;
		int current = BiomeOfRegion( region );

		if ( local < half )
		{
			float t = SmoothStep( -half, half, local );
			return new BiomeBlend( BiomeOfRegion( region - 1 ), current, t );
		}

		if ( local > L - half )
		{
			float t = SmoothStep( L - half, L + half, local );
			return new BiomeBlend( current, BiomeOfRegion( region + 1 ), t );
		}

		return new BiomeBlend( current, current, 0f );
	}

	// ---- Terrain ---------------------------------------------------------------------------

	private float MacroHeightAt( float x, float y ) => _macro.Fbm( x / 45000f, y / 45000f, 3 ) * Settings.MacroHeight;

	/// <summary>
	/// Rolling hills, ridges and gentle detail for one biome. <paramref name="amp"/> scales the hills and ridges
	/// so some stretches are nearly flat and others are rough.
	/// </summary>
	private float BiomeHeight( Biome b, float x, float y, float amp )
	{
		float h = _hills.Fbm( x / b.HillScale, y / b.HillScale, 3 ) * b.HillHeight * amp;

		if ( b.RidgeHeight > 0f )
		{
			// Stretch along Y so dune crests run in long lines across the road.
			float rx = x / b.RidgeScale;
			float ry = y / (b.RidgeScale * b.RidgeStretch);
			h += _ridges.Ridged( rx, ry, 3 ) * b.RidgeHeight * amp;
		}

		h += _detail.Fbm( x / b.DetailScale, y / b.DetailScale, 2 ) * b.DetailHeight;
		return h;
	}

	/// <summary>
	/// Large landforms (mesas, ranges). They only exist in some regions, and never within a few km of the road.
	/// Returns 0..1.
	/// </summary>
	private float MountainShape( float x, float y )
	{
		float region = SmoothStep( 0.40f, 0.72f, _mount.Fbm( x / 70000f, y / 70000f, 2 ) * 0.5f + 0.5f );
		if ( region <= 0f ) return 0f;

		float shape = _mount.Ridged( x / 24000f + 11f, y / 24000f - 5f, 3 );
		return region * shape * shape;
	}

	/// <summary>Natural ground height, ignoring the road.</summary>
	public float NaturalHeight( float x, float y )
	{
		var blend = GetBiome( x, y );

		// Warp the sampling position so features don't line up on a grid or repeat evenly.
		float wx = x + _warp2.Fbm( x / 16000f + 3.1f, y / 16000f, 2 ) * 2800f;
		float wy = y + _warp2.Fbm( x / 16000f, y / 16000f + 8.7f, 2 ) * 2800f;

		// Some stretches are calm, others rough.
		float amp = 0.45f + 1.05f * SmoothStep( -0.35f, 0.45f, _amp.Fbm( x / 34000f, y / 34000f, 2 ) );

		float h = BiomeHeight( Biomes[blend.A], wx, wy, amp );
		float mountain = Biomes[blend.A].MountainHeight;
		if ( blend.T > 0f && blend.B != blend.A )
		{
			h = Lerp( h, BiomeHeight( Biomes[blend.B], wx, wy, amp ), blend.T );
			mountain = Lerp( mountain, Biomes[blend.B].MountainHeight, blend.T );
		}

		if ( mountain > 0f )
		{
			float awayFromRoad = SmoothStep( 2500f, 9000f, MathF.Abs( y - RoadCenterY( x ) ) );
			if ( awayFromRoad > 0f )
				h += MountainShape( wx, wy ) * mountain * awayFromRoad;
		}

		return MacroHeightAt( x, y ) + h;
	}

	/// <summary>0 far from the road, 1 on the road and its shoulders.</summary>
	public float RoadInfluence( float x, float y, out float distance )
	{
		distance = MathF.Abs( y - RoadCenterY( x ) );
		float flat = Settings.RoadWidth * 0.5f + Settings.ShoulderWidth;
		return 1f - SmoothStep( flat, flat + Settings.RoadBlendWidth, distance );
	}

	/// <summary>Final ground height, with the land levelled for the road.</summary>
	public float GetHeight( float x, float y )
	{
		float natural = NaturalHeight( x, y );
		float influence = RoadInfluence( x, y, out _ );
		if ( influence <= 0f ) return natural;

		// Ground sits a little under the asphalt so the road mesh never z-fights with it.
		return Lerp( natural, RoadHeight( x ) - GroundBelowRoad, influence );
	}

	/// <summary>0..1: how settled an area is. Houses cluster where this is high; large empty stretches stay at 0.</summary>
	public float Civilization( float x, float y )
	{
		float n = _civ.Fbm( x / 110000f, y / 110000f, 2 ) * 0.5f + 0.5f;
		return SmoothStep( 0.46f, 0.70f, n );
	}

	/// <summary>0..1: how much plant life an area supports (cacti etc.).</summary>
	public float VegetationDensity( float x, float y )
	{
		return SmoothStep( -0.25f, 0.45f, _extra.Fbm( x / 12000f + 5f, y / 12000f - 9f, 2 ) );
	}

	/// <summary>Share (0..1) of a point that belongs to one biome, taking the blend between biomes into account.</summary>
	public float BiomeWeight( float x, float y, int biomeIndex )
	{
		var blend = GetBiome( x, y );
		float w = 0f;
		if ( blend.A == biomeIndex ) w += 1f - blend.T;
		if ( blend.B == biomeIndex ) w += blend.T;
		return w;
	}

	/// <summary>The biome most of a point belongs to.</summary>
	public int DominantBiome( float x, float y )
	{
		var blend = GetBiome( x, y );
		return blend.T < 0.5f ? blend.A : blend.B;
	}

	/// <summary>How far the levelled ground sits below the top of the asphalt.</summary>
	public const float GroundBelowRoad = 2.5f;

	/// <summary>
	/// Which ground materials show at a point, as weights the terrain shader blends between.
	/// <paramref name="normalZ"/> is the up component of the surface normal (1 = flat).
	/// </summary>
	public TerrainSplat GetSplat( float x, float y, float normalZ )
	{
		var blend = GetBiome( x, y );
		var a = Biomes[blend.A];
		var b = Biomes[blend.B];
		float t = blend.T;

		float altAmount = Lerp( a.AltAmount, b.AltAmount, t );
		float extraAmount = Lerp( a.ExtraAmount, b.ExtraAmount, t );

		// Large-scale patches of the alternate and extra materials.
		float altNoise = SmoothStep( -0.25f, 0.35f, _patches.Fbm( x / 6000f, y / 6000f, 3 ) );
		float extraNoise = SmoothStep( 0.10f, 0.40f, _extra.Fbm( x / 3500f, y / 3500f, 2 ) );

		// Steep ground shows the slope material.
		float slope = 1f - SmoothStep( 0.80f, 0.93f, normalZ );
		float remaining = 1f - slope;

		float extra = extraNoise * extraAmount * remaining;
		float alt = altNoise * altAmount * (remaining - extra);
		float baseW = remaining - extra - alt;

		// Gravel shoulder next to the asphalt (edge wobbles a little so it isn't a ruler line).
		RoadInfluence( x, y, out float dist );
		dist += _patches.Sample( x / 300f, y / 300f ) * 45f;
		float edge = Settings.RoadWidth * 0.5f;   // where the asphalt ends
		float shoulder = 1f - SmoothStep( edge + 10f, edge + Settings.ShoulderMaterialWidth, dist );

		var splat = new TerrainSplat { Base = baseW, Alt = alt, Slope = slope, Shoulder = shoulder };
		splat.AddBiome( blend.A, 1f - t );
		splat.AddBiome( blend.B, t );
		return splat;
	}

	// ---- Helpers ---------------------------------------------------------------------------

	private static float Lerp( float a, float b, float t ) => a + (b - a) * t;
	private static float Clamp01( float v ) => v < 0f ? 0f : (v > 1f ? 1f : v);

	private static float SmoothStep( float edge0, float edge1, float x )
	{
		float t = Clamp01( (x - edge0) / (edge1 - edge0) );
		return t * t * (3f - 2f * t);
	}
}
