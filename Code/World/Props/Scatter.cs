using System;
using System.Collections.Generic;

namespace LongTrip.World.Props;

/// <summary>One kind of scattered prop, read from <c>Assets/data/scatter.json</c>.</summary>
public sealed class ScatterDefinition
{
	public string Id;
	public string Model;

	/// <summary>"wild" (anywhere off the road), "roadside" (on the verge next to the road) or "civ" (near houses).</summary>
	public string Zone = "wild";

	/// <summary>Biomes it appears in: "desert", "grasslands", "ice". Empty: all.</summary>
	public string[] Biomes = Array.Empty<string>();

	public float Weight = 1f;
	public float MinScale = 1f, MaxScale = 1f;

	/// <summary>"box" (a collision box from the model's bounds), "none", or "upright" (a thin box only as wide as the middle of the model).</summary>
	public string Collide = "box";

	/// <summary>Fall over / lie randomly tilted a little (rocks sunk into the ground).</summary>
	public float Sink = 0f;

	/// <summary>How many degrees it may lean (random pitch and roll).</summary>
	public float Lean = 0f;

	/// <summary>Plants and dry growth: only where the land supports vegetation, thicker the more it does.</summary>
	public bool Vegetation;

	/// <summary>A separate lid: the model that sits on the body (same origin), the one that lies flat beside it, and how often it has been knocked off.</summary>
	public string LidOn, LidOff;
	public float LidOffChance;
}

public readonly record struct ScatterInstance( ScatterDefinition Def, Vector3 Position, float Yaw, float Scale, float Lean );

/// <summary>
/// The world's clutter: rocks, dead trees, shrubs, barrels, tyres, wrecks, lamps... chosen from the data file by zone and biome.
/// Where each one goes is a pure function of the seed and the chunk (the same world always gets the same props).
/// </summary>
public static class ScatterLibrary
{
	public const string DataPath = "data/scatter.json";

	private sealed class Record
	{
		public string id { get; set; }
		public string model { get; set; }
		public string zone { get; set; } = "wild";
		public string[] biomes { get; set; }
		public float weight { get; set; } = 1f;
		public float[] scale { get; set; }
		public string collide { get; set; } = "box";
		public float sink { get; set; }
		public float lean { get; set; }
		public bool vegetation { get; set; }
		public string lidOn { get; set; }
		public string lidOff { get; set; }
		public float lidOffChance { get; set; }
	}

	private sealed class ScatterFile
	{
		public float wildPerChunk { get; set; } = 26f;
		public float roadsidePerChunk { get; set; } = 10f;
		public float civPerHouse { get; set; } = 6f;
		public List<Record> props { get; set; } = new();
	}

	private static List<ScatterDefinition> _all;
	private static float _wild = 26f, _roadside = 10f, _civ = 6f;
	private static readonly Dictionary<string, Model> _models = new();

	public static void Reset() { _all = null; _models.Clear(); }

	public static IReadOnlyList<ScatterDefinition> All { get { if ( _all is null ) Load(); return _all; } }
	public static float WildPerChunk { get { if ( _all is null ) Load(); return _wild; } }
	public static float RoadsidePerChunk { get { if ( _all is null ) Load(); return _roadside; } }

	private static void Load()
	{
		_all = new List<ScatterDefinition>();
		try
		{
			if ( !FileSystem.Mounted.FileExists( DataPath ) ) { Log.Warning( $"[scatter] {DataPath} not found" ); return; }
			var file = Json.Deserialize<ScatterFile>( FileSystem.Mounted.ReadAllText( DataPath ) );
			if ( file is null ) return;
			_wild = file.wildPerChunk; _roadside = file.roadsidePerChunk; _civ = file.civPerHouse;
			foreach ( var r in file.props )
			{
				if ( string.IsNullOrEmpty( r.model ) ) continue;
				_all.Add( new ScatterDefinition
				{
					Id = r.id ?? r.model, Model = r.model, Zone = (r.zone ?? "wild").ToLowerInvariant(),
					Biomes = (r.biomes ?? Array.Empty<string>()).Select( b => b.ToLowerInvariant() ).ToArray(),
					Weight = MathF.Max( 0.01f, r.weight ),
					MinScale = r.scale is { Length: > 0 } ? r.scale[0] : 1f,
					MaxScale = r.scale is { Length: > 1 } ? r.scale[1] : (r.scale is { Length: 1 } ? r.scale[0] : 1f),
					Collide = (r.collide ?? "box").ToLowerInvariant(), Sink = r.sink, Lean = r.lean, Vegetation = r.vegetation, LidOn = r.lidOn, LidOff = r.lidOff, LidOffChance = r.lidOffChance,
				} );
			}
			Log.Info( $"[scatter] {_all.Count} prop kinds loaded" );
		}
		catch ( System.Exception e ) { Log.Warning( $"[scatter] {DataPath} could not be read ({e.Message})" ); }
	}

	public static Model GetModel( ScatterDefinition def ) => GetModel( def.Model );

	public static Model GetModel( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return null;
		if ( _models.TryGetValue( path, out var m ) && m.IsValid() ) return m;
		m = Model.Load( path );
		if ( m is null || m.IsError ) m = null;
		_models[path] = m;
		return m;
	}

	private static string BiomeName( WorldGenerator gen, float x, float y )
	{
		var b = gen.Biomes[gen.DominantBiome( x, y )];
		if ( b == BiomeLibrary.Desert ) return "desert";
		if ( b == BiomeLibrary.IcePlains ) return "ice";
		return "grasslands";
	}

	private static ScatterDefinition Pick( string zone, string biome, float r )
	{
		float total = 0f;
		foreach ( var d in All )
			if ( d.Zone == zone && (d.Biomes.Length == 0 || Array.IndexOf( d.Biomes, biome ) >= 0) ) total += d.Weight;
		if ( total <= 0f ) return null;

		r *= total;
		ScatterDefinition last = null;
		foreach ( var d in All )
		{
			if ( d.Zone != zone || (d.Biomes.Length > 0 && Array.IndexOf( d.Biomes, biome ) < 0) ) continue;
			last = d;
			r -= d.Weight;
			if ( r <= 0f ) return d;
		}
		return last;
	}

	/// <summary>Everything scattered in one chunk.</summary>
	public static List<ScatterInstance> Place( WorldGenerator gen, Vector2Int chunk, float size, float density )
	{
		var list = new List<ScatterInstance>();
		if ( All.Count == 0 ) return list;

		int seed = gen.Settings.Seed;
		float x0 = chunk.x * size, y0 = chunk.y * size;
		float R( int k ) => WorldNoise.Hash01( seed + 9100 + k, chunk.x, chunk.y );

		// ---- wild: anywhere away from the road, thicker where vegetation would be
		// Things in nature come in groups: a few cluster centres per chunk, everything gathers round them (a boulder with smaller rocks
		// at its feet, a patch of shrubs) and the gaps between stay empty.
		int wild = (int)(WildPerChunk * density);
		int clusters = 3 + (int)(R( 7000 ) * 4f);
		for ( int a = 0; a < wild; a++ )
		{
			int c = a % clusters;
			float cx = x0 + R( 7100 + c * 2 ) * size, cy = y0 + R( 7101 + c * 2 ) * size;
			float ang = R( a * 11 + 0 ) * MathF.PI * 2f;
			float rad = MathF.Pow( R( a * 11 + 1 ), 1.6f ) * (500f + R( 7200 + c ) * 900f);
			float px = cx + MathF.Cos( ang ) * rad, py = cy + MathF.Sin( ang ) * rad;
			if ( px < x0 || px >= x0 + size || py < y0 || py >= y0 + size ) continue;
			float dist = MathF.Abs( py - gen.RoadCenterY( px ) );
			if ( dist < 700f ) continue;
			TryAdd( gen, list, "wild", px, py, R( a * 11 + 2 ), R( a * 11 + 3 ), R( a * 11 + 4 ), R( a * 11 + 5 ), R( a * 11 + 6 ) );
		}

		// ---- roadside: the levelled verge next to the road
		float midX = x0 + size * 0.5f;
		if ( MathF.Abs( gen.RoadCenterY( midX ) - (y0 + size * 0.5f) ) < size * 0.75f )
		{
			int side = (int)(RoadsidePerChunk * density);
			for ( int a = 0; a < side; a++ )
			{
				float px = x0 + R( 500 + a * 11 + 0 ) * size;
				float roadY = gen.RoadCenterY( px );
				float sign = R( 500 + a * 11 + 1 ) < 0.5f ? -1f : 1f;
				float py = roadY + sign * (420f + R( 500 + a * 11 + 2 ) * 380f);
				if ( py < y0 || py >= y0 + size ) continue;
				TryAdd( gen, list, "roadside", px, py, R( 500 + a * 11 + 3 ), R( 500 + a * 11 + 4 ), R( 500 + a * 11 + 5 ), R( 500 + a * 11 + 6 ), R( 500 + a * 11 + 7 ) );
			}
		}

		// ---- near houses: a few bits and pieces around each
		foreach ( var house in PropPlacer.Houses( gen, chunk, size ) )
		{
			for ( int a = 0; a < (int)_civ; a++ )
			{
				float ang = R( 900 + a * 7 ) * MathF.PI * 2f;
				float rad = 330f + R( 901 + a * 7 ) * 520f;
				float px = house.Position.x + MathF.Cos( ang ) * rad, py = house.Position.y + MathF.Sin( ang ) * rad;
				if ( MathF.Abs( py - gen.RoadCenterY( px ) ) < 420f ) continue;
				TryAdd( gen, list, "civ", px, py, R( 902 + a * 7 ), R( 903 + a * 7 ), R( 904 + a * 7 ), R( 905 + a * 7 ), R( 906 + a * 7 ) );
			}
		}

		return list;
	}

	private static void TryAdd( WorldGenerator gen, List<ScatterInstance> list, string zone, float px, float py, float rPick, float rYaw, float rScale, float rLean, float rVeg )
	{
		string biome = BiomeName( gen, px, py );
		var def = Pick( zone, biome, rPick );
		if ( def is null ) return;

		if ( def.Vegetation && rVeg > 0.15f + gen.VegetationDensity( px, py ) * 0.85f ) return;

		float h = gen.GetHeight( px, py );
		if ( MathF.Abs( gen.GetHeight( px + 70f, py ) - h ) > 24f || MathF.Abs( gen.GetHeight( px, py + 70f ) - h ) > 24f ) return;

		foreach ( var o in list )
			if ( (o.Position.x - px) * (o.Position.x - px) + (o.Position.y - py) * (o.Position.y - py) < 90f * 90f ) return;

		float scale = MathX.Lerp( def.MinScale, def.MaxScale, rScale );
		list.Add( new ScatterInstance( def, new Vector3( px, py, h - def.Sink * scale ), rYaw * 360f, scale, (rLean - 0.5f) * 2f * def.Lean ) );
	}
}
