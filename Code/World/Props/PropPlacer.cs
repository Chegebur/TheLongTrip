using System;
using System.Collections.Generic;

namespace LongTrip.World.Props;

/// <summary>One placed object: which variant, where, and how it's turned and scaled.</summary>
public readonly record struct PropInstance( int Variant, Vector3 Position, float Yaw, float Scale );

/// <summary>
/// Decides where houses and cacti go. Purely a function of the seed and the chunk, so the same world always
/// gets the same props, and it touches no engine objects (safe to call from any thread).
/// </summary>
public static class PropPlacer
{
	// ---- Houses ----------------------------------------------------------------------------

	/// <summary>Houses never closer to the road centre than this (the road and its levelled verge)...</summary>
	private const float HouseMinRoadDistance = 900f;

	/// <summary>...and never further than this, so they're seen from the road.</summary>
	private const float HouseMaxRoadDistance = 4800f;

	public static List<PropInstance> Houses( WorldGenerator gen, Vector2Int chunk, float size )
	{
		var list = new List<PropInstance>();
		int seed = gen.Settings.Seed;
		float x0 = chunk.x * size, y0 = chunk.y * size;
		float midX = x0 + size * 0.5f, midY = y0 + size * 0.5f;

		// Nothing to do for chunks that can't reach the road corridor.
		float roadMid = gen.RoadCenterY( midX );
		if ( MathF.Abs( midY - roadMid ) > size * 0.5f + HouseMaxRoadDistance + 600f ) return list;

		float civ = gen.Civilization( midX, midY );
		float r0 = WorldNoise.Hash01( seed + 101, chunk.x, chunk.y );
		int want = civ > 0.02f ? 1 + (int)(r0 * civ * 3.4f) : (r0 < 0.035f ? 1 : 0);
		if ( want == 0 ) return list;

		float R( int k ) => WorldNoise.Hash01( seed + 1000 + k, chunk.x, chunk.y );

		for ( int a = 0; a < 16 && list.Count < want; a++ )
		{
			float px = x0 + R( a * 7 + 0 ) * size;
			float py = y0 + R( a * 7 + 1 ) * size;

			float roadY = gen.RoadCenterY( px );
			float side = py >= roadY ? 1f : -1f;
			float dist = MathF.Abs( py - roadY );
			if ( dist < HouseMinRoadDistance || dist > HouseMaxRoadDistance ) continue;

			// keep houses apart
			bool tooClose = false;
			foreach ( var other in list )
				if ( (other.Position.x - px) * (other.Position.x - px) + (other.Position.y - py) * (other.Position.y - py) < 1500f * 1500f ) { tooClose = true; break; }
			if ( tooClose ) continue;

			// level ground only: sample the footprint
			float h = gen.GetHeight( px, py );
			float lo = h, hi = h;
			foreach ( var (ox, oy) in Footprint )
			{
				float hh = gen.GetHeight( px + ox, py + oy );
				lo = MathF.Min( lo, hh );
				hi = MathF.Max( hi, hh );
			}
			if ( hi - lo > 26f ) continue;

			// pick a design for the biome
			int biome = gen.DominantBiome( px, py );
			bool snowy = biome == IceBiomeIndex( gen );
			int design = (int)(R( a * 7 + 2 ) * 12f) % 12;
			int variant = snowy ? design + HouseBuilder.SnowVariantStart : design;

			// the front door faces the road
			float yaw = side > 0 ? -90f : 90f;
			yaw += (R( a * 7 + 3 ) - 0.5f) * 20f;

			float scale = 0.94f + R( a * 7 + 4 ) * 0.18f;
			// The floor sits on the HIGHEST ground under the footprint, so the terrain never pokes up through it;
			// the foundation reaches down to hide the gap on the low side. (+3 for the coarser far-away terrain mesh.)
			list.Add( new PropInstance( variant, new Vector3( px, py, hi + 3f ), yaw, scale ) );
		}

		return list;
	}

	private static readonly (float, float)[] Footprint =
	{
		(-260, -220), (260, -220), (-260, 220), (260, 220), (0, -240), (0, 240), (-280, 0), (280, 0),
	};

	private static int IceBiomeIndex( WorldGenerator gen ) => Array.IndexOf( gen.Biomes, BiomeLibrary.IcePlains );

	// ---- Cacti -----------------------------------------------------------------------------

	/// <summary>Cacti keep clear of the road and its verge.</summary>
	private const float CactusMinRoadDistance = 650f;

	public static List<PropInstance> Cacti( WorldGenerator gen, Vector2Int chunk, float size, int maxCount )
	{
		var list = new List<PropInstance>();
		if ( maxCount <= 0 ) return list;

		int seed = gen.Settings.Seed;
		int desert = Array.IndexOf( gen.Biomes, BiomeLibrary.Desert );
		if ( desert < 0 ) return list;

		float x0 = chunk.x * size, y0 = chunk.y * size;
		float midX = x0 + size * 0.5f, midY = y0 + size * 0.5f;
		if ( gen.BiomeWeight( midX, midY, desert ) < 0.05f
			&& gen.BiomeWeight( x0, y0, desert ) < 0.05f
			&& gen.BiomeWeight( x0 + size, y0 + size, desert ) < 0.05f ) return list;

		float R( int k ) => WorldNoise.Hash01( seed + 5000 + k, chunk.x, chunk.y );

		// Cacti grow in groups: pick a few cluster centres, then scatter around them.
		int clusters = 2 + (int)(R( 0 ) * 3f);
		var centres = new List<Vector2>();
		for ( int c = 0; c < clusters; c++ )
			centres.Add( new Vector2( x0 + R( 10 + c * 2 ) * size, y0 + R( 11 + c * 2 ) * size ) );

		int attempts = maxCount * 3;
		for ( int a = 0; a < attempts && list.Count < maxCount; a++ )
		{
			var centre = centres[a % centres.Count];
			float angle = R( 100 + a * 3 ) * MathF.PI * 2f;
			float radius = MathF.Sqrt( R( 101 + a * 3 ) ) * 1100f;
			float px = centre.x + MathF.Cos( angle ) * radius;
			float py = centre.y + MathF.Sin( angle ) * radius;

			// stay inside this chunk so neighbours never double up
			if ( px < x0 || px >= x0 + size || py < y0 || py >= y0 + size ) continue;

			if ( gen.BiomeWeight( px, py, desert ) < 0.7f ) continue;
			if ( R( 102 + a * 3 ) > 0.25f + gen.VegetationDensity( px, py ) * 0.75f ) continue;
			if ( MathF.Abs( py - gen.RoadCenterY( px ) ) < CactusMinRoadDistance ) continue;

			// not on steep slopes
			float h = gen.GetHeight( px, py );
			if ( MathF.Abs( gen.GetHeight( px + 80f, py ) - h ) > 22f || MathF.Abs( gen.GetHeight( px, py + 80f ) - h ) > 22f ) continue;

			bool tooClose = false;
			foreach ( var other in list )
				if ( (other.Position.x - px) * (other.Position.x - px) + (other.Position.y - py) * (other.Position.y - py) < 110f * 110f ) { tooClose = true; break; }
			if ( tooClose ) continue;

			float pick = R( 103 + a * 3 );
			int variant = PickCactus( pick );
			float scale = 0.85f + R( 104 + a * 3 ) * 0.5f;
			float yaw = R( 105 + a * 3 ) * 360f;
			list.Add( new PropInstance( variant, new Vector3( px, py, h - 3f ), yaw, scale ) );
		}

		return list;
	}

	private static float[] _cactusCumulative;

	/// <summary>
	/// Mostly saguaros, then prickly pears, then a few barrels. Within a kind the lighter models are a little
	/// more common than the heavy multi-armed ones. Models under 14 inches tall are too small to notice and are skipped.
	/// </summary>
	private static int PickCactus( float r )
	{
		if ( _cactusCumulative is null )
		{
			float KindShare( CactusKind k ) => k switch { CactusKind.Saguaro => 0.55f, CactusKind.Pear => 0.32f, _ => 0.13f };

			var all = CactusModels.All;
			var weights = new float[all.Length];
			foreach ( CactusKind kind in Enum.GetValues( typeof( CactusKind ) ) )
			{
				int eligible = 0;
				for ( int i = 0; i < all.Length; i++ ) if ( all[i].Kind == kind && all[i].Height >= 14f ) eligible++;
				for ( int i = 0; i < all.Length; i++ )
				{
					if ( all[i].Kind != kind || all[i].Height < 14f ) continue;
					// heavy plants (up to ~8k triangles) are lovely but rare: they cost the most to draw
					float lightBias = Math.Clamp( 1500f / all[i].Triangles, 0.12f, 1.6f );
					weights[i] = KindShare( kind ) / eligible * lightBias;
				}
			}

			var c = new float[all.Length];
			float sum = 0;
			for ( int i = 0; i < c.Length; i++ ) { sum += weights[i]; c[i] = sum; }
			for ( int i = 0; i < c.Length; i++ ) c[i] /= sum;
			_cactusCumulative = c;
		}

		for ( int i = 0; i < _cactusCumulative.Length; i++ )
			if ( r <= _cactusCumulative[i] ) return i;
		return _cactusCumulative.Length - 1;
	}
}
