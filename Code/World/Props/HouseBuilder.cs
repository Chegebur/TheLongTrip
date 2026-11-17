using System;
using System.Collections.Generic;
using System.Linq;

namespace LongTrip.World.Props;

/// <summary>A built house: the model (render + collision) plus where its interior lights go.</summary>
public readonly record struct HouseInfo( Model Model, float Width, float Depth, float Height, IReadOnlyList<Vector3> Lights, float LightRadius, IReadOnlyList<FurniturePiece> Furniture );

/// <summary>A downloaded furniture model placed inside a house (house-local position, yaw, scale).</summary>
public readonly record struct FurniturePiece( string Model, Vector3 Position, float Yaw, Vector3 Scale );

/// <summary>
/// Builds procedural houses you can walk into. Variants are cached: each one is a single model shared by every house
/// that uses it, so many houses cost very little.
/// <para>
/// Local space: origin at the centre of the footprint at ground level, +X is the front (door side), +Y is left.
/// Ground-floor plan: living room and kitchen across the front, and behind them either two bedrooms with a bathroom
/// between (single storey) or a hall with a staircase and a kitchen (two storeys, with bedrooms and a bathroom upstairs).
/// </para>
/// </summary>
public static class HouseBuilder
{
	/// <summary>0..11 have shingle roofs, 12..23 the same designs with snow on the roof.</summary>
	public const int VariantCount = 24;
	public const int SnowVariantStart = 12;

	/// <summary>How far the foundation reaches below ground level (hides slope differences).</summary>
	public const float FoundationDepth = 150f;

	private static readonly Dictionary<int, HouseInfo> _cache = new();

	public static int WallOf( int variant ) => variant % 4;

	public static HouseInfo Get( int variant )
	{
		variant = Math.Clamp( variant, 0, VariantCount - 1 );
		// (A cached entry from before a hot-reload can be missing newer fields, so those count as stale too.)
		if ( !_cache.TryGetValue( variant, out var info ) || !info.Model.IsValid() || info.Lights is null || info.Furniture is null )
			_cache[variant] = info = new HouseGen( variant ).Build();
		return info;
	}

	/// <summary>Forget the built models, so the next houses are built fresh (the world calls this when it rebuilds).</summary>
	public static void ClearCache() => _cache.Clear();
}

/// <summary>Generates one house variant.</summary>
internal sealed class HouseGen
{
	// ---- materials -------------------------------------------------------------------------------
	private const string M = "materials/world/buildings/";
	private const string MI = "materials/world/interior/";
	private static readonly string[] WallMaterials =
	{
		M + "wall_red_brick.vmat", M + "wall_concrete_tile.vmat", M + "wall_broken_brick.vmat", M + "wall_cladding.vmat",
	};
	private const string RoofShingle = M + "roof_shingle.vmat";
	private const string RoofSnow = M + "roof_snow.vmat";
	private const string Foundation = M + "foundation.vmat";
	private const string Trim = M + "trim_white.vmat";
	private const string Glass = MI + "glass_window.vmat";
	private const string Flat = MI + "flat_lit.vmat";
	private const string FlatMetal = MI + "flat_metal.vmat";
	// Polyhaven (CC0) materials: laminate planks, floor tiles, plaster painted per room, a ceiling and dark wood for the stairs
	private const string FloorWood = MI + "ph_floor_wood.vmat";
	private const string FloorTile = MI + "ph_floor_tile.vmat";
	private const string StairWood = MI + "ph_stairs_wood.vmat";
	private const string PlasterWhite = MI + "ph_plaster_white.vmat";
	private const string PlasterEdge = MI + "ph_plaster_edge.vmat";
	private const float PlasterUv = 1.7f;   // plaster repeats every ~65 inches

	/// <summary>Each room's paint colour (see <see cref="Wash"/>) stands for the plaster material tinted that colour.</summary>
	private static readonly Dictionary<(byte, byte, byte), string> Plaster = new()
	{
		{ (208, 198, 176), MI + "ph_plaster_living.vmat" }, { (212, 202, 150), MI + "ph_plaster_kitchen.vmat" }, { (150, 172, 194), MI + "ph_plaster_master.vmat" },
		{ (160, 186, 156), MI + "ph_plaster_bed2.vmat" }, { (160, 192, 198), MI + "ph_plaster_bath.vmat" }, { (204, 196, 180), MI + "ph_plaster_hall.vmat" },
		{ (236, 234, 228), PlasterWhite }, { (222, 220, 214), MI + "ph_ceiling.vmat" }, { (200, 192, 176), PlasterEdge },
	};

	private static string PlasterOf( Color32? c, string fallback ) => c is { } v && Plaster.TryGetValue( (v.r, v.g, v.b), out var m ) ? m : fallback;

	// ---- dimensions (inches) -----------------------------------------------------------------------
	private const float T = 7f;          // exterior wall thickness
	private const float PT = 4.5f;       // partition thickness
	private const float Z0 = 6f;         // ground-floor surface height
	private const float H = 108f;        // storey height, floor to floor
	private const float CeilH = 100f;    // floor to ceiling
	private const float SlabT = 8f;      // upper-floor slab thickness
	private const float DoorW = 36f, DoorH = 80f;
	private const int StairSteps = 15;

	private static Color32 Paint( int r, int g, int b, float rough = 0.85f ) => PropMeshBuilder.Flat( r, g, b, rough );

	private readonly int _variant;
	private readonly string _wall;
	private readonly int _style;           // 0 single-storey gable, 1 two-storey gable, 2 flat roof
	private readonly bool _snow;
	private readonly int _floors;
	private readonly Random _style_rng;

	private readonly PropMeshBuilder _b = new() { TileSize = 110f };
	private readonly List<Vector3> _lights = new();
	private readonly List<FurniturePiece> _furn = new();

	// footprint and plan lines
	private float _width, _depth;
	private float _hx, _hy, _ix0, _ix1, _iy0, _iy1;
	private float _xP;                      // partition between front zone and back zone
	private float _yA, _yB;                 // single storey: partitions between back rooms
	private float _yH, _yBt;                // two storey: hall edge, and upstairs bath/bedroom split
	private float _stairW = 40f, _run = 10.5f;
	private const float StairGap = 10f;      // clearance between the stairs and the walls they run along (they are not built into the wall)
	private float _holeX, _holeY;           // stair opening in the upper slab

	private enum Room { Living, Kitchen, Master, Bed2, Bath, Hall, Landing }

	public HouseGen( int variant )
	{
		_variant = variant;
		int design = variant % HouseBuilder.SnowVariantStart;
		_wall = WallMaterials[HouseBuilder.WallOf( variant )];
		_style = (design / 4) % 3;
		_snow = variant >= HouseBuilder.SnowVariantStart;
		_floors = _style == 1 ? 2 : 1;
		_style_rng = new Random( 104729 * (design + 1) );   // the snow variant is the summer variant's twin
	}

	private float S( float lo, float hi ) => lo + (float)_style_rng.NextDouble() * (hi - lo);

	// ============================================================================================
	//  Assembly
	// ============================================================================================

	public HouseInfo Build()
	{
		_width = S( 560f, 700f );     // along Y
		_depth = S( 430f, 520f );     // along X
		_hx = _depth * 0.5f; _hy = _width * 0.5f;
		_ix0 = -_hx + T; _ix1 = _hx - T; _iy0 = -_hy + T; _iy1 = _hy - T;
		_xP = _ix0 + (_ix1 - _ix0) * 0.42f;

		_yA = _iy0 + (_iy1 - _iy0) * 0.36f;
		_yB = _iy0 + (_iy1 - _iy0) * 0.64f;
		_yH = _iy0 + StairGap + _stairW + 64f;
		_yBt = _yH + (_iy1 - _yH) * 0.5f;
		_holeX = StairGap + (StairSteps - 1) * _run;
		_holeY = StairGap + _stairW + 2f;

		float wallTop = Z0 + _floors * H;

		Shell( wallTop );
		Roof( wallTop );
		Floors();
		Partitions();
		if ( _floors == 2 ) Stairs();
		Furnish();
		ExteriorDetails();

		var model = _b.Build( $"House {_variant}" );
		float height = wallTop + (_style == 2 ? 10f : _depth * 0.3f);
		return new HouseInfo( model, _width, _depth, height, _lights, 260f, _furn );
	}

	// ============================================================================================
	//  Walls with openings
	// ============================================================================================

	private readonly struct Opening
	{
		public readonly float T0, T1, Z0, Z1;
		public Opening( float t0, float t1, float z0, float z1 ) { T0 = t0; T1 = t1; Z0 = z0; Z1 = z1; }
	}

	/// <summary>
	/// A wall from <paramref name="a"/> to <paramref name="b"/> (plan view), seen from the side its normal (d x Z) points to.
	/// <paramref name="oFront"/> and <paramref name="oBack"/> are the offsets of its two faces along that normal.
	/// Openings are cut right through, with reveals, and the wall also gets a collision box per solid piece.
	/// </summary>
	private void Wall( Vector2 a, Vector2 b, float zLo, float zHi, float oFront, float oBack,
		string frontMat, Func<Vector2, Color32?> frontColor, string backMat, Func<Vector2, Color32?> backColor,
		List<Opening> openings, string revealMat, Color32? revealColor, bool collide = true, IEnumerable<float> splits = null )
	{
		var dv = b - a;
		float L = dv.Length;
		var d = dv / L;
		var nOut = new Vector2( d.y, -d.x );
		float thick = oFront - oBack;

		Vector3 P( float t, float z, float o ) => new( a.x + d.x * t + nOut.x * o, a.y + d.y * t + nOut.y * o, z );
		Vector2 Plan( float t, float o ) => new( a.x + d.x * t + nOut.x * o, a.y + d.y * t + nOut.y * o );

		var ts = new SortedSet<float> { 0f, L };
		foreach ( var o in openings ) { ts.Add( o.T0 ); ts.Add( o.T1 ); }
		// room boundaries: a piece of wall must never span two rooms, since each gets one paint colour
		if ( splits is not null )
			foreach ( var s in splits ) if ( s > 0.5f && s < L - 0.5f ) ts.Add( s );
		var list = ts.ToList();

		void Rect( float ta, float tb, float za, float zb )
		{
			if ( zb - za < 0.5f ) return;
			float tm = (ta + tb) * 0.5f;
			var fc = frontColor?.Invoke( Plan( tm, oFront + 3f ) );
			var bc = backColor?.Invoke( Plan( tm, oBack - 3f ) );

			// painted faces become the plaster material in that colour
			string fm = frontMat, bm = backMat;
			float fuv = 1f, buv = 1f;
			if ( frontMat == Flat && PlasterOf( fc, Flat ) is var pf && pf != Flat ) { fm = pf; fc = null; fuv = PlasterUv; }
			if ( backMat == Flat && PlasterOf( bc, Flat ) is var pb && pb != Flat ) { bm = pb; bc = null; buv = PlasterUv; }

			_b.Quad( fm, P( ta, za, oFront ), P( tb, za, oFront ), P( tb, zb, oFront ), P( ta, zb, oFront ), fuv, fc, ta, za );
			_b.Quad( bm, P( tb, za, oBack ), P( ta, za, oBack ), P( ta, zb, oBack ), P( tb, zb, oBack ), buv, bc, L - tb, za );

			if ( collide )
			{
				float len = tb - ta;
				var c = Plan( tm, (oFront + oBack) * 0.5f );
				var size = MathF.Abs( d.x ) > 0.5f ? new Vector3( len, thick, zb - za ) : new Vector3( thick, len, zb - za );
				_b.Collide( new Vector3( c.x, c.y, (za + zb) * 0.5f ), size );
			}
		}

		for ( int i = 0; i < list.Count - 1; i++ )
		{
			float ta = list[i], tb = list[i + 1];
			if ( tb - ta < 0.01f ) continue;
			var op = openings.Cast<Opening?>().FirstOrDefault( o => o.Value.T0 <= ta + 0.001f && o.Value.T1 >= tb - 0.001f );
			if ( op is null ) { Rect( ta, tb, zLo, zHi ); continue; }
			Rect( ta, tb, zLo, op.Value.Z0 );
			Rect( ta, tb, op.Value.Z1, zHi );
		}

		// reveals: the thickness of the wall around each opening
		foreach ( var o in openings )
		{
			// left jamb faces +d, right jamb faces -d, sill faces up, head faces down
			_b.Quad( revealMat, P( o.T0, o.Z0, oFront ), P( o.T0, o.Z0, oBack ), P( o.T0, o.Z1, oBack ), P( o.T0, o.Z1, oFront ), 1f, revealColor );
			_b.Quad( revealMat, P( o.T1, o.Z0, oBack ), P( o.T1, o.Z0, oFront ), P( o.T1, o.Z1, oFront ), P( o.T1, o.Z1, oBack ), 1f, revealColor );
			if ( o.Z0 > zLo + 0.5f )
				_b.Quad( revealMat, P( o.T0, o.Z0, oBack ), P( o.T0, o.Z0, oFront ), P( o.T1, o.Z0, oFront ), P( o.T1, o.Z0, oBack ), 1f, revealColor );
			if ( o.Z1 < zHi - 0.5f )
				_b.Quad( revealMat, P( o.T0, o.Z1, oBack ), P( o.T1, o.Z1, oBack ), P( o.T1, o.Z1, oFront ), P( o.T0, o.Z1, oFront ), 1f, revealColor );
		}
	}

	// ============================================================================================
	//  Rooms: layout lookups
	// ============================================================================================

	private Room RoomAt( float x, float y, int floor )
	{
		bool front = x > _xP;
		if ( _floors == 1 )
		{
			if ( front ) return Room.Living;
			return y < _yA ? Room.Bed2 : y < _yB ? Room.Bath : Room.Master;
		}

		if ( floor == 0 )
		{
			if ( front ) return Room.Living;
			return y < _yH ? Room.Hall : Room.Kitchen;
		}

		if ( front ) return Room.Master;
		return y < _yH ? Room.Landing : y < _yBt ? Room.Bath : Room.Bed2;
	}

	private static Color32 Wash( Room r ) => r switch
	{
		Room.Living => Paint( 208, 198, 176 ),
		Room.Kitchen => Paint( 212, 202, 150 ),
		Room.Master => Paint( 150, 172, 194 ),
		Room.Bed2 => Paint( 160, 186, 156 ),
		Room.Bath => Paint( 160, 192, 198 ),
		_ => Paint( 204, 196, 180 ),
	};

	private bool IsTiled( Room r ) => r == Room.Bath || r == Room.Kitchen;

	// ============================================================================================
	//  Shell: exterior walls, windows, door
	// ============================================================================================

	private void Shell( float wallTop )
	{
		float zLo = Z0 - 2f;
		string wall = _wall;

		// openings per exterior wall; t runs along each wall's own direction
		var front = new List<Opening>();       // x = +hx, direction +Y (t = y + hy)
		var back = new List<Opening>();        // x = -hx, direction -Y (t = hy - y)
		var left = new List<Opening>();        // y = +hy, direction -X (t = hx - x)
		var right = new List<Opening>();       // y = -hy, direction +X (t = x + hx)

		float xF = (_xP + _ix1) * 0.5f;        // middle of the front zone
		float xB = (_ix0 + _xP) * 0.5f;        // middle of the back zone

		for ( int f = 0; f < _floors; f++ )
		{
			float z0 = Z0 + f * H;
			float sillLo = z0 + 30f, sillHi = z0 + 82f;

			// front: door on the ground floor, windows either side
			if ( f == 0 )
			{
				front.Add( new Opening( _hy - 21f, _hy + 21f, Z0, Z0 + 84f ) );
				AddWindow( front, _hy - _hy * 0.58f, 50f, sillLo, sillHi );
				AddWindow( front, _hy + _hy * 0.58f, 50f, sillLo, sillHi );
			}
			else
			{
				for ( int i = -1; i <= 1; i++ ) AddWindow( front, _hy + i * _hy * 0.5f, 46f, sillLo, sillHi );
			}

			// back: one window per back room
			if ( _floors == 1 )
			{
				AddWindow( back, _hy - (_iy0 + _yA) * 0.5f, 46f, sillLo, sillHi );
				AddWindow( back, _hy - (_yA + _yB) * 0.5f, 26f, z0 + 58f, z0 + 86f );     // small bathroom window
				AddWindow( back, _hy - (_yB + _iy1) * 0.5f, 50f, sillLo, sillHi );
			}
			else if ( f == 0 )
			{
				AddWindow( back, _hy - (_yH + _iy1) * 0.5f, 52f, sillLo, sillHi );        // kitchen
				AddWindow( back, _hy - (_iy0 + _yH) * 0.5f, 28f, sillLo + 6f, sillHi );    // hall
			}
			else
			{
				AddWindow( back, _hy - (_yBt + _iy1) * 0.5f, 46f, sillLo, sillHi );
				AddWindow( back, _hy - (_yH + _yBt) * 0.5f, 26f, z0 + 58f, z0 + 86f );
				AddWindow( back, _hy - (_iy0 + _yH) * 0.5f, 28f, sillLo, sillHi );
			}

			// sides, centred on each zone. Skip the stair side (right wall, back zone) of a two-storey house.
			AddWindow( left, _hx - xF, 50f, sillLo, sillHi );
			AddWindow( left, _hx - xB, 46f, sillLo, sillHi );
			AddWindow( right, xF + _hx, 50f, sillLo, sillHi );
			if ( _floors == 1 ) AddWindow( right, xB + _hx, 46f, sillLo, sillHi );
		}

		// each exterior wall: texture outside, paint inside. Built one storey at a time so each room gets its own colour.
		var revealPaint = Paint( 240, 238, 232, 0.8f );
		void ExtWall( Vector2 a, Vector2 b, List<Opening> all, float[] splits )
		{
			for ( int f = 0; f < _floors; f++ )
			{
				float lo = f == 0 ? zLo : Z0 + H;
				float hi = f == _floors - 1 ? wallTop : Z0 + H;
				int floor = f;
				var ops = all.Where( o => o.Z0 >= lo - 0.5f && o.Z1 <= hi + 0.5f ).ToList();
				Wall( a, b, lo, hi, 0f, -T, wall, null, Flat, p => Wash( RoomAt( p.x, p.y, floor ) ), ops, Trim, revealPaint, true, splits );
			}
		}

		// where the rooms meet each exterior wall (t is measured along each wall's own direction)
		float[] backSplits = _floors == 1 ? new[] { _hy - _yB, _hy - _yA } : new[] { _hy - _yBt, _hy - _yH };
		float[] sideSplits = { _hx - _xP };            // left wall runs -X
		float[] rightSplits = { _xP + _hx };           // right wall runs +X
		ExtWall( new Vector2( _hx, -_hy ), new Vector2( _hx, _hy ), front, null );
		ExtWall( new Vector2( -_hx, _hy ), new Vector2( -_hx, -_hy ), back, backSplits );
		ExtWall( new Vector2( _hx, _hy ), new Vector2( -_hx, _hy ), left, sideSplits );
		ExtWall( new Vector2( -_hx, -_hy ), new Vector2( _hx, -_hy ), right, rightSplits );

		// glass and frames
		FrameOpenings( new Vector2( _hx, -_hy ), new Vector2( _hx, _hy ), front, 0f, -T );
		FrameOpenings( new Vector2( -_hx, _hy ), new Vector2( -_hx, -_hy ), back, 0f, -T );
		FrameOpenings( new Vector2( _hx, _hy ), new Vector2( -_hx, _hy ), left, 0f, -T );
		FrameOpenings( new Vector2( -_hx, -_hy ), new Vector2( _hx, -_hy ), right, 0f, -T );

		// foundation (below the floor, hides slope differences) and the door
		_b.Box( Foundation, new Vector3( 0, 0, (Z0 - 1f - HouseBuilder.FoundationDepth) * 0.5f ), new Vector3( _depth + 6, _width + 6, Z0 - 1f + HouseBuilder.FoundationDepth ) );
		_b.Collide( new Vector3( 0, 0, (Z0 - 1f - HouseBuilder.FoundationDepth) * 0.5f ), new Vector3( _depth, _width, Z0 - 1f + HouseBuilder.FoundationDepth ) );

		// gable ends and upper wall pieces are added with the roof
	}

	private static void AddWindow( List<Opening> list, float centre, float width, float z0, float z1 )
		=> list.Add( new Opening( centre - width * 0.5f, centre + width * 0.5f, z0, z1 ) );

	/// <summary>Frame, mullions and glass for each window; plain openings for the doors (there are no door leaves).</summary>
	private void FrameOpenings( Vector2 a, Vector2 b, List<Opening> openings, float oFront, float oBack )
	{
		var d = (b - a).Normal;
		var nOut = new Vector2( d.y, -d.x );
		Vector3 P( float t, float z, float o ) => new( a.x + d.x * t + nOut.x * o, a.y + d.y * t + nOut.y * o, z );
		float yaw = MathF.Atan2( d.y, d.x ) * 180f / MathF.PI;

		foreach ( var o in openings )
		{
			bool isDoor = o.Z0 <= Z0 + 0.5f;
			float w = o.T1 - o.T0, h = o.Z1 - o.Z0;
			float tm = (o.T0 + o.T1) * 0.5f, zm = (o.Z0 + o.Z1) * 0.5f;
			const float f = 3.5f;   // frame width

			// frame boards on the outside face
			Frame( P( tm, o.Z1 + f * 0.5f, oFront + 0.8f ), w + 2 * f, f, yaw );                        // head
			if ( !isDoor ) Frame( P( tm, o.Z0 - f * 0.5f - 1.2f, oFront + 2f ), w + 2 * f + 4, f + 1.5f, yaw );   // sill
			Post( P( o.T0 - f * 0.5f, zm, oFront + 0.8f ), f, h + 2 * f, yaw );
			Post( P( o.T1 + f * 0.5f, zm, oFront + 0.8f ), f, h + 2 * f, yaw );

			if ( isDoor )
			{
				// no door: the doorway is just an opening
				continue;
			}

			// mullions
			Frame( P( tm, zm, (oFront + oBack) * 0.5f ), w, 1.6f, yaw );
			Post( P( tm, zm, (oFront + oBack) * 0.5f ), 1.6f, h, yaw );

			// invisible but solid: a closed window you can't climb through
			var gc = P( tm, zm, (oFront + oBack) * 0.5f );
			_b.Collide( gc, MathF.Abs( d.x ) > 0.5f ? new Vector3( w, 2f, h ) : new Vector3( 2f, w, h ) );

			// glass, visible from both sides
			float oc = (oFront + oBack) * 0.5f;
			_b.Quad( Glass, P( o.T0, o.Z0, oc ), P( o.T1, o.Z0, oc ), P( o.T1, o.Z1, oc ), P( o.T0, o.Z1, oc ) );
			_b.Quad( Glass, P( o.T1, o.Z0, oc - 0.3f ), P( o.T0, o.Z0, oc - 0.3f ), P( o.T0, o.Z1, oc - 0.3f ), P( o.T1, o.Z1, oc - 0.3f ) );
		}
	}

	private void Frame( Vector3 centre, float length, float thick, float yaw )
		=> _b.Box( Trim, centre, new Vector3( length, 2.4f, thick ), yaw, true );

	private void Post( Vector3 centre, float thick, float height, float yaw )
		=> _b.Box( Trim, centre, new Vector3( thick, 2.4f, height ), yaw, true );

	// ============================================================================================
	//  Roof
	// ============================================================================================

	private void Roof( float wallTop )
	{
		float hx = _hx, hy = _hy;
		string roof = _snow ? RoofSnow : RoofShingle;
		var white = Paint( 236, 234, 228, 0.85f );

		if ( _style == 2 )
		{
			// flat roof with a parapet
			const float t = 10f;
			_b.Box( Trim, new Vector3( 0, 0, wallTop + t * 0.5f ), new Vector3( _depth + 12, _width + 12, t ) );
			_b.Quad( roof, new Vector3( -hx, -hy, wallTop + t + 1 ), new Vector3( hx, -hy, wallTop + t + 1 ), new Vector3( hx, hy, wallTop + t + 1 ), new Vector3( -hx, hy, wallTop + t + 1 ) );
			// ceiling of the top floor seen from inside is added in Floors()
			return;
		}

		float rise = _depth * S( 0.26f, 0.32f );
		const float overhang = 20f;
		float ex = hx + overhang, ey = hy + 8f;
		float ridge = wallTop + rise;
		float h1 = rise * overhang / ex;

		// two slopes
		_b.Quad( roof, new Vector3( ex, -ey, wallTop ), new Vector3( ex, ey, wallTop ), new Vector3( 0, ey, ridge ), new Vector3( 0, -ey, ridge ), 1.2f );
		_b.Quad( roof, new Vector3( -ex, ey, wallTop ), new Vector3( -ex, -ey, wallTop ), new Vector3( 0, -ey, ridge ), new Vector3( 0, ey, ridge ), 1.2f );

		// gable ends in wall material
		_b.Quad( _wall, new Vector3( hx, hy, wallTop ), new Vector3( -hx, hy, wallTop ), new Vector3( -hx, hy, wallTop + h1 ), new Vector3( hx, hy, wallTop + h1 ) );
		_b.Triangle( _wall, new Vector3( hx, hy, wallTop + h1 ), new Vector3( -hx, hy, wallTop + h1 ), new Vector3( 0, hy, ridge ) );
		_b.Quad( _wall, new Vector3( -hx, -hy, wallTop ), new Vector3( hx, -hy, wallTop ), new Vector3( hx, -hy, wallTop + h1 ), new Vector3( -hx, -hy, wallTop + h1 ) );
		_b.Triangle( _wall, new Vector3( -hx, -hy, wallTop + h1 ), new Vector3( hx, -hy, wallTop + h1 ), new Vector3( 0, -hy, ridge ) );

		// underside of the eaves
		_b.Quad( Flat, new Vector3( -ex, ey, wallTop - 1f ), new Vector3( ex, ey, wallTop - 1f ), new Vector3( ex, -ey, wallTop - 1f ), new Vector3( -ex, -ey, wallTop - 1f ), 1f, white );
		// fascia boards along the eaves
		_b.Box( Trim, new Vector3( ex, 0, wallTop + 1f ), new Vector3( 2.4f, ey * 2f, 5f ) );
		_b.Box( Trim, new Vector3( -ex, 0, wallTop + 1f ), new Vector3( 2.4f, ey * 2f, 5f ) );

		// chimney
		_b.Box( _wall, new Vector3( -hx * 0.35f, hy * 0.45f, wallTop + rise * 0.55f ), new Vector3( 34, 34, rise * 1.5f + 40 ) );
		_b.Box( Foundation, new Vector3( -hx * 0.35f, hy * 0.45f, wallTop + rise * 1.3f + 22f ), new Vector3( 40, 40, 5 ) );
		if ( _snow )
			_b.Box( RoofSnow, new Vector3( -hx * 0.35f, hy * 0.45f, wallTop + rise * 1.3f + 26f ), new Vector3( 38, 38, 4 ) );

		// the roof is solid for collision (a thin hull of two slopes, so you can't walk through it from outside)
		float slope = MathF.Atan2( rise, ex ) * 180f / MathF.PI;
		float slopeLen = MathF.Sqrt( rise * rise + ex * ex );
		_b.Collide( new Vector3( ex * 0.5f, 0, wallTop + rise * 0.5f ), new Vector3( slopeLen, ey * 2f, 3f ), Rotation.FromAxis( Vector3.Left, slope ) );
		_b.Collide( new Vector3( -ex * 0.5f, 0, wallTop + rise * 0.5f ), new Vector3( slopeLen, ey * 2f, 3f ), Rotation.FromAxis( Vector3.Left, -slope ) );
	}

	// ============================================================================================
	//  Floors, ceilings, upper slab
	// ============================================================================================

	private readonly record struct R( float X0, float X1, float Y0, float Y1 );

	/// <summary>The parts of <paramref name="r"/> left after cutting out <paramref name="hole"/>.</summary>
	private static IEnumerable<R> Minus( R r, R hole )
	{
		float hx0 = Math.Clamp( hole.X0, r.X0, r.X1 ), hx1 = Math.Clamp( hole.X1, r.X0, r.X1 );
		float hy0 = Math.Clamp( hole.Y0, r.Y0, r.Y1 ), hy1 = Math.Clamp( hole.Y1, r.Y0, r.Y1 );
		if ( hx1 - hx0 < 0.01f || hy1 - hy0 < 0.01f ) { yield return r; yield break; }
		if ( hx0 - r.X0 > 0.01f ) yield return new R( r.X0, hx0, r.Y0, r.Y1 );
		if ( r.X1 - hx1 > 0.01f ) yield return new R( hx1, r.X1, r.Y0, r.Y1 );
		if ( hy0 - r.Y0 > 0.01f ) yield return new R( hx0, hx1, r.Y0, hy0 );
		if ( r.Y1 - hy1 > 0.01f ) yield return new R( hx0, hx1, hy1, r.Y1 );
	}

	private void FloorQuad( R r, float z, string mat, Color32? color, bool up )
	{
		float tile = 1.1f;   // 110 inch texture tile -> 100 inch floor boards
		if ( mat == Flat && PlasterOf( color, Flat ) is var painted && painted != Flat ) { mat = painted; color = null; tile = PlasterUv; }
		if ( up )
			_b.Quad( mat, new Vector3( r.X0, r.Y0, z ), new Vector3( r.X1, r.Y0, z ), new Vector3( r.X1, r.Y1, z ), new Vector3( r.X0, r.Y1, z ), tile, color, r.X0, r.Y0 );
		else
			_b.Quad( mat, new Vector3( r.X0, r.Y1, z ), new Vector3( r.X1, r.Y1, z ), new Vector3( r.X1, r.Y0, z ), new Vector3( r.X0, r.Y0, z ), tile, color, r.X0, r.Y0 );
	}

	private void Floors()
	{
		var all = new R( _ix0, _ix1, _iy0, _iy1 );
		var ceil = Paint( 222, 220, 214, 0.9f );

		// ---- ground floor, room by room (the material depends on the room)
		foreach ( var (room, rect) in GroundRooms() )
		{
			string mat = IsTiled( room ) ? FloorTile : FloorWood;
			FloorQuad( rect, Z0, mat, null, true );
			if ( _floors == 1 )
				FloorQuad( rect, Z0 + CeilH, Flat, ceil, false );
		}

		// ---- upper floor
		if ( _floors == 2 )
		{
			var hole = new R( _ix0, _ix0 + _holeX, _iy0, _iy0 + _holeY );
			float zFloor = Z0 + H;
			float zSlabBottom = zFloor - SlabT;

			foreach ( var (room, rect) in UpperRooms() )
			{
				foreach ( var piece in Minus( rect, hole ) )
					FloorQuad( piece, zFloor, IsTiled( room ) ? FloorTile : FloorWood, null, true );
			}
			// underside of the slab = ground-floor ceiling, minus the stairwell
			foreach ( var piece in Minus( all, hole ) )
			{
				FloorQuad( piece, zSlabBottom, Flat, ceil, false );
				_b.Collide( new Vector3( (piece.X0 + piece.X1) * 0.5f, (piece.Y0 + piece.Y1) * 0.5f, zFloor - SlabT * 0.5f ), new Vector3( piece.X1 - piece.X0, piece.Y1 - piece.Y0, SlabT ) );
			}

			// the edge of the slab around the stairwell
			_b.Quad( PlasterEdge, new Vector3( hole.X1, hole.Y0, zSlabBottom ), new Vector3( hole.X1, hole.Y1, zSlabBottom ), new Vector3( hole.X1, hole.Y1, zFloor ), new Vector3( hole.X1, hole.Y0, zFloor ), PlasterUv, null );
			_b.Quad( PlasterEdge, new Vector3( hole.X1, hole.Y1, zSlabBottom ), new Vector3( hole.X0, hole.Y1, zSlabBottom ), new Vector3( hole.X0, hole.Y1, zFloor ), new Vector3( hole.X1, hole.Y1, zFloor ), PlasterUv, null );

			// ceiling of the top floor
			FloorQuad( all, zFloor + CeilH, Flat, ceil, false );
		}
	}

	private IEnumerable<(Room, R)> GroundRooms()
	{
		var front = new R( _xP, _ix1, _iy0, _iy1 );
		if ( _floors == 1 )
		{
			yield return (Room.Living, front);
			yield return (Room.Bed2, new R( _ix0, _xP, _iy0, _yA ));
			yield return (Room.Bath, new R( _ix0, _xP, _yA, _yB ));
			yield return (Room.Master, new R( _ix0, _xP, _yB, _iy1 ));
		}
		else
		{
			yield return (Room.Living, front);
			yield return (Room.Hall, new R( _ix0, _xP, _iy0, _yH ));
			yield return (Room.Kitchen, new R( _ix0, _xP, _yH, _iy1 ));
		}
	}

	private IEnumerable<(Room, R)> UpperRooms()
	{
		yield return (Room.Master, new R( _xP, _ix1, _iy0, _iy1 ));
		yield return (Room.Landing, new R( _ix0, _xP, _iy0, _yH ));
		yield return (Room.Bath, new R( _ix0, _xP, _yH, _yBt ));
		yield return (Room.Bed2, new R( _ix0, _xP, _yBt, _iy1 ));
	}

	// ============================================================================================
	//  Interior partition walls
	// ============================================================================================

	private void Partitions()
	{
		Func<Vector2, Color32?> col0 = p => Wash( RoomAt( p.x, p.y, 0 ) );
		Func<Vector2, Color32?> col1 = p => Wash( RoomAt( p.x, p.y, 1 ) );

		for ( int f = 0; f < _floors; f++ )
		{
			float zLo = Z0 + f * H;
			float zHi = zLo + CeilH;
			var colors = f == 0 ? col0 : col1;
			Opening Door( float centre, float tStart ) => new( centre - tStart - DoorW * 0.5f, centre - tStart + DoorW * 0.5f, zLo, zLo + DoorH );

			// P1: the wall between the front zone and the back zone, along Y at x = xP (t = y - iy0)
			var p1 = new List<Opening>();
			if ( _floors == 1 )
			{
				p1.Add( Door( (_iy0 + _yA) * 0.5f, _iy0 ) );
				p1.Add( Door( (_yA + _yB) * 0.5f, _iy0 ) );
				p1.Add( Door( (_yB + _iy1) * 0.5f, _iy0 ) );
			}
			else if ( f == 0 )
			{
				p1.Add( Door( (_iy0 + StairGap + _stairW + _yH) * 0.5f, _iy0 ) );   // into the hall (beside the stairs)
				p1.Add( Door( (_yH + _iy1) * 0.5f, _iy0 ) );              // into the kitchen
			}
			else
			{
				p1.Add( Door( (_iy0 + _yH) * 0.5f + _stairW * 0.5f, _iy0 ) );   // landing
				p1.Add( Door( (_yBt + _iy1) * 0.5f, _iy0 ) );                    // second bedroom
			}
			var p1Splits = _floors == 1 ? new[] { _yA - _iy0, _yB - _iy0 }
				: f == 0 ? new[] { _yH - _iy0 } : new[] { _yH - _iy0, _yBt - _iy0 };
			Wall( new Vector2( _xP, _iy0 ), new Vector2( _xP, _iy1 ), zLo, zHi, PT * 0.5f, -PT * 0.5f, Flat, colors, Flat, colors, p1, Trim, Paint( 236, 234, 228 ), true, p1Splits );

			// P2: walls between the back rooms, along X (direction +X, t = x - ix0)
			if ( _floors == 1 )
			{
				if ( f == 0 )
				{
					Wall( new Vector2( _ix0, _yA ), new Vector2( _xP, _yA ), zLo, zHi, PT * 0.5f, -PT * 0.5f, Flat, colors, Flat, colors, new List<Opening>(), Trim, null );
					Wall( new Vector2( _ix0, _yB ), new Vector2( _xP, _yB ), zLo, zHi, PT * 0.5f, -PT * 0.5f, Flat, colors, Flat, colors, new List<Opening>(), Trim, null );
				}
			}
			else
			{
				// ground: hall/kitchen divider; upper: landing/bath, bath/bedroom
				var hallDoor = new List<Opening>();
				if ( f == 0 ) hallDoor.Add( new Opening( _xP - _ix0 - 60f, _xP - _ix0 - 60f + DoorW, zLo, zLo + DoorH ) );
				else hallDoor.Add( new Opening( _xP - _ix0 - 60f, _xP - _ix0 - 60f + DoorW, zLo, zLo + DoorH ) );
				Wall( new Vector2( _ix0, _yH ), new Vector2( _xP, _yH ), zLo, zHi, PT * 0.5f, -PT * 0.5f, Flat, colors, Flat, colors, hallDoor, Trim, Paint( 236, 234, 228 ) );
				if ( f == 1 )
					Wall( new Vector2( _ix0, _yBt ), new Vector2( _xP, _yBt ), zLo, zHi, PT * 0.5f, -PT * 0.5f, Flat, colors, Flat, colors, new List<Opening>(), Trim, null );
			}
		}
	}

	// ============================================================================================
	//  Stairs
	// ============================================================================================

	private void Stairs()
	{
		float rise = H / StairSteps;
		var tread = Paint( 150, 104, 66, 0.6f );
		var riser = Paint( 196, 190, 178, 0.7f );

		for ( int k = 1; k < StairSteps; k++ )
		{
			float xa = _ix0 + StairGap + (k - 1) * _run, xb = xa + _run;
			float ya = _iy0 + StairGap, yb = ya + _stairW;
			float top = Z0 + k * rise;
			float height = top - Z0;

			// a step is its riser (facing back down the stairs), its two sides and its tread, as separate faces: the tread is not a second box lying
			// on the first, so no two faces share a plane (that was what flickered). The back is hidden by the next step.
			_b.Quad( PlasterWhite, new Vector3( xa, yb, Z0 ), new Vector3( xa, ya, Z0 ), new Vector3( xa, ya, top ), new Vector3( xa, yb, top ), PlasterUv );
			_b.Quad( PlasterWhite, new Vector3( xb, yb, Z0 ), new Vector3( xa, yb, Z0 ), new Vector3( xa, yb, top ), new Vector3( xb, yb, top ), PlasterUv );
			_b.Quad( PlasterWhite, new Vector3( xa, ya, Z0 ), new Vector3( xb, ya, Z0 ), new Vector3( xb, ya, top ), new Vector3( xa, ya, top ), PlasterUv );
			_b.Quad( StairWood, new Vector3( xa, ya, top ), new Vector3( xb, ya, top ), new Vector3( xb, yb, top ), new Vector3( xa, yb, top ), 1.2f );
			_b.Collide( new Vector3( (xa + xb) * 0.5f, (ya + yb) * 0.5f, Z0 + height * 0.5f ), new Vector3( _run, _stairW, height ) );
		}

		// handrail along the open side of the stairs
		float y = _iy0 + StairGap + _stairW;
		var rail = Paint( 92, 62, 38, 0.6f );
		var a = new Vector3( _ix0 + StairGap, y - 1.5f, Z0 + 34f );
		var b = new Vector3( _ix0 + _holeX, y - 1.5f, Z0 + H - rise + 34f );
		var rot = Rotation.LookAt( (b - a).Normal, Vector3.Up );
		_b.Box( StairWood, (a + b) * 0.5f, new Vector3( (b - a).Length, 2.4f, 2.4f ), rot, true, 1.2f, null );
		for ( int k = 0; k < StairSteps; k += 3 )
		{
			float x = _ix0 + StairGap + 2f + k * _run;
			float top = Z0 + Math.Max( k, 1 ) * rise;
			float railZ = a.z + (b.z - a.z) * ((x - a.x) / (b.x - a.x));
			_b.Box( StairWood, new Vector3( x, y - 1.5f, (top + railZ) * 0.5f ), new Vector3( 2f, 2f, railZ - top ), 0, true, 1.2f, null );
		}

		// guard rail on the upstairs side of the stairwell opening
		float zF = Z0 + H;
		_b.Box( StairWood, new Vector3( _ix0 + _holeX * 0.5f, _iy0 + _holeY + 1f, zF + 33f ), new Vector3( _holeX, 2.4f, 2.4f ), 0, true, 1.2f, null );
		for ( int i = 0; i <= 4; i++ )
			_b.Box( StairWood, new Vector3( _ix0 + _holeX * i / 4f, _iy0 + _holeY + 1f, zF + 17f ), new Vector3( 2f, 2f, 34f ), 0, true, 1.2f, null );
		_b.Collide( new Vector3( _ix0 + _holeX * 0.5f, _iy0 + _holeY + 1f, zF + 22f ), new Vector3( _holeX, 3f, 44f ) );
	}

	// ============================================================================================
	//  Furniture
	// ============================================================================================

	/// <summary>A box positioned in a piece's own frame, then placed in the house. <paramref name="offset"/> is relative to the piece origin.</summary>
	private void Fx( Vector2 at, float yaw, float z, Vector3 offset, Vector3 size, Color32 color, bool metal = false )
	{
		var rot = Rotation.FromYaw( yaw );
		var c = rot * offset;
		_b.Box( metal ? FlatMetal : Flat, new Vector3( at.x + c.x, at.y + c.y, z + offset.z ), size, yaw, true, 1f, color );
	}

	private void Solid( Vector2 at, float yaw, float z, Vector3 offset, Vector3 size )
	{
		var rot = Rotation.FromYaw( yaw );
		var c = rot * offset;
		_b.Collide( new Vector3( at.x + c.x, at.y + c.y, z + offset.z ), size, Rotation.FromYaw( yaw ) );
	}

	/// <summary>A pendant lamp hangs from the ceiling at (x, y); <paramref name="z"/> is the light height (10 below the ceiling).</summary>
	private void Light( float x, float y, float z )
	{
		_lights.Add( new Vector3( x, y, z ) );
		Prop( "modern_ceiling_lamp_01", new Vector2( x, y ), 0, z + 10f - 46.2f * 0.6f, 0.6f );
	}

	/// <summary>Place a downloaded model (origin at its base) in house-local space. <paramref name="tall"/> stretches it vertically.</summary>
	private void Prop( string name, Vector2 at, float yaw, float z, float scale = 1f, float tall = 1f )
		=> _furn.Add( new FurniturePiece( $"models/world/furniture/{name}/{name}.vmdl", new Vector3( at.x, at.y, z ), yaw, new Vector3( scale, scale, scale * tall ) ) );

	/// <summary>Whether the rooms get furniture (sofas, beds, kitchens, bathrooms). Off: the houses are empty.</summary>
	private const bool Furnished = false;

	private void Furnish()
	{
		var r = new Random( 31 * (_variant % HouseBuilder.SnowVariantStart) + 7 );
		Color32 Fabric()
		{
			int k = r.Next( 4 );
			return k switch { 0 => Paint( 96, 118, 142, 0.9f ), 1 => Paint( 150, 74, 62, 0.9f ), 2 => Paint( 88, 120, 96, 0.9f ), _ => Paint( 160, 150, 132, 0.9f ) };
		}

		float rx = _ix1, rxP = _xP;

		if ( _floors == 1 )
		{
			// ---------------- living / kitchen across the front
			float yMid = (_iy0 + _iy1) * 0.5f;
			if ( Furnished ) Living( new R( _xP + PT, _ix1, _iy0, _iy1 ), Z0, Fabric(), 0 );
			Light( (_xP + _ix1) * 0.5f, _iy0 + (_iy1 - _iy0) * 0.30f, Z0 + CeilH - 10 );
			Light( (_xP + _ix1) * 0.5f, _iy0 + (_iy1 - _iy0) * 0.72f, Z0 + CeilH - 10 );

			if ( Furnished ) Bedroom( new R( _ix0, _xP - PT, _yB + PT, _iy1 ), Z0, Fabric(), true );
			if ( Furnished ) Bedroom( new R( _ix0, _xP - PT, _iy0, _yA - PT ), Z0, Fabric(), false );
			if ( Furnished ) Bathroom( new R( _ix0, _xP - PT, _yA + PT, _yB - PT ), Z0 );
			Light( (_ix0 + _xP) * 0.5f, (_yB + _iy1) * 0.5f, Z0 + CeilH - 10 );
			Light( (_ix0 + _xP) * 0.5f, (_iy0 + _yA) * 0.5f, Z0 + CeilH - 10 );
			Light( (_ix0 + _xP) * 0.5f, (_yA + _yB) * 0.5f, Z0 + CeilH - 10 );
		}
		else
		{
			// ---------------- ground floor: living in front, kitchen and hall behind
			if ( Furnished ) Living( new R( _xP + PT, _ix1, _iy0, _iy1 ), Z0, Fabric(), 0 );
			if ( Furnished ) Kitchen( new R( _ix0, _xP - PT, _yH + PT, _iy1 ), Z0 );
			Light( (_xP + _ix1) * 0.5f, (_iy0 + _iy1) * 0.5f, Z0 + CeilH - 10 );
			Light( (_ix0 + _xP) * 0.5f, (_yH + _iy1) * 0.5f, Z0 + CeilH - 10 );
			Light( (_ix0 + _xP) * 0.5f, (_iy0 + _yH) * 0.5f, Z0 + CeilH - 10 );

			// ---------------- upstairs
			float zU = Z0 + H;
			if ( Furnished ) Bedroom( new R( _xP + PT, _ix1, _iy0, _iy1 ), zU, Fabric(), true );
			if ( Furnished ) Bedroom( new R( _ix0, _xP - PT, _yBt + PT, _iy1 ), zU, Fabric(), false );
			if ( Furnished ) Bathroom( new R( _ix0, _xP - PT, _yH + PT, _yBt - PT ), zU );
			Light( (_xP + _ix1) * 0.5f, (_iy0 + _iy1) * 0.5f, zU + CeilH - 10 );
			Light( (_ix0 + _xP) * 0.5f, (_yBt + _iy1) * 0.5f, zU + CeilH - 10 );
			Light( (_ix0 + _xP) * 0.5f, (_yH + _yBt) * 0.5f, zU + CeilH - 10 );
			Light( (_ix0 + _xP) * 0.5f, (_iy0 + _yH) * 0.5f, zU + CeilH - 10 );
		}
	}

	// ---- living room: sofa and TV, coffee table, rug, dining set and a kitchen run along the left wall --------------

	private void Living( R room, float z, Color32 fabric, int unused )
	{
		float depth = room.X1 - room.X0;
		float yc = (room.Y0 + room.Y1) * 0.5f;
		var wood = Paint( 128, 86, 52, 0.6f );
		var dark = Paint( 36, 36, 40, 0.4f );

		// rug under the seating area
		float seatX = room.X0 + depth * 0.5f;
		float seatY = room.Y0 + (room.Y1 - room.Y0) * 0.27f;
		_b.Box( Flat, new Vector3( seatX, seatY, z + 0.4f ), new Vector3( 120f, 90f, 0.8f ), 0, false, 1f, Paint( 150, 64, 56, 0.95f ) );

		// sofa, back to the front wall, facing the TV on the partition
		var sofaAt = new Vector2( room.X1 - 30f, seatY );
		Sofa( sofaAt, 180f, z, fabric );

		// coffee table
		var ct = new Vector2( seatX - 4f, seatY );
		Prop( "modern_coffee_table_01", ct, 90, z );
		Solid( ct, 0, z, new Vector3( 0, 0, 8 ), new Vector3( 24, 47, 16 ) );

		// TV on a stand against the partition
		var tv = new Vector2( room.X0 + 11f, seatY );
		Prop( "painted_wooden_cabinet", new Vector2( tv.x + 1f, tv.y ), 0, z );
		Prop( "Television_01", new Vector2( tv.x + 1f, tv.y ), 0, z + 46.5f );
		Solid( tv, 0, z, new Vector3( 0, 0, 23 ), new Vector3( 24, 47, 46 ) );

		// shelf unit beside it
		var shelf = new Vector2( room.X0 + 8f, room.Y0 + 18f );
		Prop( "wooden_display_shelves_01", new Vector2( shelf.x + 1f, shelf.y + 6f ), 0, z );
		Solid( shelf, 0, z, new Vector3( 0, 6, 30 ), new Vector3( 15, 43, 61 ) );

		// dining table with four chairs, kitchen run along the left wall
		float dy = room.Y1 - 58f;
		var table = new Vector2( room.X0 + depth * 0.45f, dy );
		Prop( "dining_table", table, 0, z, 0.8f );
		Solid( table, 0, z, new Vector3( 0, 0, 14 ), new Vector3( 44, 72, 28 ) );
		foreach ( var (sx, sy, yaw) in new[] { (-30f, -16f, 0f), (-30f, 16f, 0f), (30f, -16f, 180f), (30f, 16f, 180f) } )
			Chair( new Vector2( table.x + sx, table.y + sy ), yaw, z, wood );

		// kitchen counter run along the left wall
		float cx0 = room.X0 + 10f, cx1 = room.X1 - 70f;   // the run stops 26 short: the stove stands between it and the fridge
		var counterFront = new Vector2( 0, 0 );
		float cy = room.Y1 - 14f;
		for ( float x = cx0; x < cx1 - 1f; x += 24f )
		{
			var at = new Vector2( x + 12f, cy );
			Fx( at, 0, z, new Vector3( 0, 0, 17 ), new Vector3( 23.4f, 26, 34 ), Paint( 224, 220, 210, 0.7f ) );
			Fx( at, 0, z, new Vector3( 0, -13.2f, 17 ), new Vector3( 22, 0.6f, 31 ), Paint( 168, 128, 88, 0.6f ) );   // cabinet door
			Fx( at, 0, z, new Vector3( 0, -13.8f, 25 ), new Vector3( 7, 0.8f, 1.2f ), Paint( 190, 190, 196, 0.3f ), true );
		}
		float len = cx1 - cx0;
		Fx( new Vector2( (cx0 + cx1) * 0.5f, cy ), 0, z, new Vector3( 0, -0.8f, 35 ), new Vector3( len, 28, 2.2f ), Paint( 70, 72, 80, 0.35f ) );   // worktop
		Solid( new Vector2( (cx0 + cx1) * 0.5f, cy ), 0, z, new Vector3( 0, 0, 18 ), new Vector3( len, 28, 36 ) );
		// sink and hob
		Fx( new Vector2( cx0 + len * 0.3f, cy ), 0, z, new Vector3( 0, -1, 35.6f ), new Vector3( 20, 16, 1.4f ), Paint( 190, 192, 198, 0.2f ), true );
		Fx( new Vector2( cx0 + len * 0.3f, cy ), 0, z, new Vector3( 0, 5, 42 ), new Vector3( 2, 2, 12 ), Paint( 196, 198, 204, 0.2f ), true );
		Prop( "electric_stove", new Vector2( cx1 + 13f, cy ), -90, z, 1.05f );
		Solid( new Vector2( cx1 + 13f, cy ), 0, z, new Vector3( 0, 0, 18 ), new Vector3( 24, 26, 36 ) );
		Prop( "vintage_microwave", new Vector2( cx0 + len * 0.66f, cy - 1f ), -90, z + 36.2f, 0.6f );
		// upper cabinets
		for ( float x = cx0; x < cx1 - len * 0.4f; x += 24f )
			Fx( new Vector2( x + 12f, cy + 5f ), 0, z, new Vector3( 0, 0, 68 ), new Vector3( 23.4f, 13, 28 ), Paint( 214, 208, 196, 0.7f ) );
		// fridge at the end of the run
		var fridge = new Vector2( cx1 + 40f, cy - 1f );
		Fx( fridge, 0, z, new Vector3( 0, 0, 33 ), new Vector3( 30, 28, 66 ), Paint( 214, 216, 220, 0.3f ), true );
		Fx( fridge, 0, z, new Vector3( 0, -14.4f, 33 ), new Vector3( 28.4f, 0.6f, 0.8f ), Paint( 40, 42, 48, 0.5f ) );
		Fx( fridge, 0, z, new Vector3( 11f, -15f, 44 ), new Vector3( 1.2f, 1.2f, 14 ), Paint( 180, 182, 188, 0.2f ), true );
		Solid( fridge, 0, z, new Vector3( 0, 0, 33 ), new Vector3( 30, 28, 66 ) );
	}

	private void Kitchen( R room, float z )
	{
		// a compact kitchen for the two-storey plan: counter along the back wall, fridge, small table
		float yc = (room.Y0 + room.Y1) * 0.5f;
		float len = (room.Y1 - room.Y0) - 50f;
		var wood = Paint( 128, 86, 52, 0.6f );
		for ( float y = room.Y0 + 10f; y < room.Y1 - 66f; y += 24f )
		{
			var at = new Vector2( room.X0 + 14f, y + 12f );
			Fx( at, 0, z, new Vector3( 0, 0, 17 ), new Vector3( 26, 23.4f, 34 ), Paint( 224, 220, 210, 0.7f ) );
			Fx( at, 0, z, new Vector3( 14f, 0, 17 ), new Vector3( 0.6f, 22, 31 ), Paint( 168, 128, 88, 0.6f ) );
		}
		float cy0 = room.Y0 + 10f, cy1 = room.Y1 - 66f;
		Prop( "electric_stove", new Vector2( room.X0 + 13f, cy1 + 13f ), 0, z, 1.05f );
		Solid( new Vector2( room.X0 + 13f, cy1 + 13f ), 0, z, new Vector3( 0, 0, 18 ), new Vector3( 26, 24, 36 ) );
		Fx( new Vector2( room.X0 + 13f, (cy0 + cy1) * 0.5f ), 0, z, new Vector3( 0, 0, 35 ), new Vector3( 28, cy1 - cy0, 2.2f ), Paint( 70, 72, 80, 0.35f ) );
		Solid( new Vector2( room.X0 + 13f, (cy0 + cy1) * 0.5f ), 0, z, new Vector3( 0, 0, 18 ), new Vector3( 28, cy1 - cy0, 36 ) );
		Fx( new Vector2( room.X0 + 13f, cy0 + (cy1 - cy0) * 0.35f ), 0, z, new Vector3( 0, 0, 35.8f ), new Vector3( 16, 20, 1.4f ), Paint( 190, 192, 198, 0.2f ), true );   // sink
		Prop( "vintage_microwave", new Vector2( room.X0 + 13f, cy0 + (cy1 - cy0) * 0.75f ), 0, z + 36.2f, 0.6f );
		var fridge = new Vector2( room.X0 + 16f, room.Y1 - 18f );
		Fx( fridge, 0, z, new Vector3( 0, 0, 33 ), new Vector3( 28, 30, 66 ), Paint( 214, 216, 220, 0.3f ), true );
		Solid( fridge, 0, z, new Vector3( 0, 0, 33 ), new Vector3( 28, 30, 66 ) );

		var table = new Vector2( room.X1 - 50f, yc );
		Prop( "dining_table", table, 90, z, 0.6f );
		Solid( table, 0, z, new Vector3( 0, 0, 14 ), new Vector3( 53, 31, 21 ) );
		Chair( new Vector2( table.x - 33f, table.y ), 0, z, wood );
		Chair( new Vector2( table.x + 33f, table.y ), 180, z, wood );
	}

	// ---- bedrooms -------------------------------------------------------------------------------------------------

	private void Bedroom( R room, float z, Color32 bedding, bool master )
	{
		float yc = (room.Y0 + room.Y1) * 0.5f;
		var wood = Paint( 120, 82, 50, 0.6f );
		var white = Paint( 236, 234, 228, 0.9f );

		// bed with its head against the back wall of the room (smallest x)
		// a day bed with its long side (and back rail) against the back wall
		float bs = master ? 1.18f : 1f;
		var bed = new Vector2( room.X0 + 18.5f * bs + 3f, yc );
		Prop( "vintage_day_bed", bed, 0, z, bs );
		Solid( bed, 0, z, new Vector3( 0, 0, 13 ), new Vector3( 34 * bs, 78 * bs, 26 ) );
		float w = 78f * bs;

		// bedside tables
		foreach ( int side in new[] { -1, 1 } )
		{
			var ns = new Vector2( room.X0 + 11f, yc + side * (w * 0.5f + 13f) );
			Prop( "painted_wooden_nightstand", ns, 0, z );
			Solid( ns, 0, z, new Vector3( 0, 0, 12 ), new Vector3( 20, 20, 24 ) );
		}

		// wardrobe along the far wall (a tall version of the cabinet)
		var wd = new Vector2( room.X1 - 13.5f, room.Y1 - 40f );
		Prop( "painted_wooden_cabinet", wd, 180, z, 1f, 1.6f );
		Solid( wd, 0, z, new Vector3( 0, 0, 37 ), new Vector3( 24, 47, 74 ) );

		// rug
		_b.Box( Flat, new Vector3( room.X0 + 70f, yc, z + 0.4f ), new Vector3( 60f, w + 24f, 0.8f ), 0, false, 1f, master ? Paint( 188, 168, 130, 0.95f ) : Paint( 140, 160, 180, 0.95f ) );
	}

	// ---- bathroom -------------------------------------------------------------------------------------------------

	private void Bathroom( R room, float z )
	{
		float width = room.Y1 - room.Y0;
		var white = Paint( 242, 242, 240, 0.25f );
		var chrome = Paint( 200, 202, 208, 0.2f );

		// bath along the back wall: a hollow tub made of four sides and an inner floor
		float tubL = Math.Min( 66f, width - 4f );
		var tub = new Vector2( room.X0 + 16f, room.Y0 + tubL * 0.5f + 2f );
		Fx( tub, 0, z, new Vector3( 0, 0, 9 ), new Vector3( 30, tubL, 18 ), white );
		Fx( tub, 0, z, new Vector3( 0, 0, 18.4f ), new Vector3( 22.4f, tubL - 8f, 0.8f ), Paint( 150, 190, 210, 0.15f ) );   // water-blue basin
		Fx( tub, 0, z, new Vector3( 12f, 0, 22 ), new Vector3( 2, 3, 6 ), chrome, true );
		Solid( tub, 0, z, new Vector3( 0, 0, 9 ), new Vector3( 30, tubL, 18 ) );

		// toilet against the back wall on the other side
		var wc = new Vector2( room.X0 + 12f, room.Y1 - 16f );
		Fx( wc, 0, z, new Vector3( 3, 0, 8 ), new Vector3( 16, 13, 16 ), white );
		Fx( wc, 0, z, new Vector3( -4, 0, 22 ), new Vector3( 6, 13, 12 ), white );
		Fx( wc, 0, z, new Vector3( 4, 0, 16.6f ), new Vector3( 15, 12, 1.6f ), Paint( 230, 230, 226, 0.4f ) );
		Solid( wc, 0, z, new Vector3( 0, 0, 12 ), new Vector3( 22, 14, 24 ) );

		// basin and mirror on the side wall nearest the door
		var sink = new Vector2( room.X1 - 22f, room.Y1 - 12f );
		Fx( sink, 0, z, new Vector3( 0, 0, 16 ), new Vector3( 20, 24, 32 ), Paint( 236, 232, 222, 0.7f ) );
		Fx( sink, 0, z, new Vector3( 0, 0, 33 ), new Vector3( 22, 26, 2 ), white );
		Fx( sink, 0, z, new Vector3( 0, 0, 33.8f ), new Vector3( 14, 16, 0.6f ), Paint( 170, 190, 204, 0.15f ) );
		Fx( sink, 0, z, new Vector3( -7f, 0, 39 ), new Vector3( 1.6f, 1.6f, 8 ), chrome, true );
		Fx( new Vector2( room.X1 - 6f, room.Y1 - 2f ), 0, z, new Vector3( 0, 0, 58 ), new Vector3( 24, 1.2f, 26 ), Paint( 210, 224, 232, 0.05f ), true );   // mirror
		Solid( sink, 0, z, new Vector3( 0, 0, 17 ), new Vector3( 20, 24, 34 ) );
	}

	// ---- pieces used by several rooms -----------------------------------------------------------------------------

	private void Sofa( Vector2 at, float yaw, float z, Color32 fabric )
	{
		// built facing +X in its own frame, then turned by yaw
		// the model's back is 19 behind its origin; the caller gives the back-wall position, so nudge forward
		var shift = Rotation.FromYaw( yaw ) * new Vector3( 8f, 0, 0 );
		Prop( "sofa_02", new Vector2( at.x + shift.x, at.y + shift.y ), yaw, z );
		Solid( at, yaw, z, new Vector3( 0, 0, 14 ), new Vector3( 32, 72, 28 ) );
	}

	private void Chair( Vector2 at, float yaw, float z, Color32 wood )
	{
		var shift = Rotation.FromYaw( yaw ) * new Vector3( 1.2f, 0, 0 );
		Prop( "painted_wooden_chair_01", new Vector2( at.x + shift.x, at.y + shift.y ), yaw, z );
		Solid( at, yaw, z, new Vector3( 0, 0, 17 ), new Vector3( 15, 15, 34 ) );
	}

	// ============================================================================================
	//  Outside details
	// ============================================================================================

	private void ExteriorDetails()
	{
		var white = Trim;
		float wallTop = Z0 + _floors * H;

		// corner boards
		foreach ( var (cx, cy) in new[] { (_hx, _hy), (_hx, -_hy), (-_hx, _hy), (-_hx, -_hy) } )
			_b.Box( white, new Vector3( cx, cy, (wallTop + Z0) * 0.5f ), new Vector3( 5f, 5f, wallTop - Z0 ), 0, false );

		// front step and a small porch landing
		_b.Box( Foundation, new Vector3( _hx + 22f, 0, -12.5f ), new Vector3( 44f, 70f, 35f ), 0, true );   // reaches down to the ground on a slope
		_b.Collide( new Vector3( _hx + 22f, 0, -12.5f ), new Vector3( 44f, 70f, 35f ) );

		// a lamp beside the door
		_b.Box( FlatMetal, new Vector3( _hx + 3f, 36f, Z0 + 70f ), new Vector3( 4f, 5f, 8f ), 0, true, 1f, Paint( 40, 40, 44, 0.4f ) );
		_b.Box( Flat, new Vector3( _hx + 3.5f, 36f, Z0 + 70f ), new Vector3( 3f, 3.6f, 6f ), 0, true, 1f, Paint( 255, 232, 170, 0.3f ) );
	}
}
