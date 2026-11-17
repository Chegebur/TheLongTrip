namespace LongTrip.Gameplay;

/// <summary>One kind of building piece, from <c>Assets/data/buildings.json</c>.</summary>
public sealed class BuildPieceDef
{
	public string Id, Name, Model;

	/// <summary>"floor" (fills one grid square) or "wall" (stands on one edge of a grid square).</summary>
	public string Kind = "wall";

	/// <summary>Walls: how tall, in levels (1 = a full level, 0.5 = a low wall). Floors ignore it.</summary>
	public float Height = 1f;

	/// <summary>How far (inches) it is lifted off the surface it stands on, so it does not fight it.</summary>
	public float Rest;

	/// <summary>What taking the piece apart gives back (item id and count). Empty for now: when pieces cost materials, list them here and in the data file.</summary>
	public List<(string Item, int Count)> Refund = new();

	public bool IsFloor => Kind == "floor";
}

public static class BuildLibrary
{
	public const string DataPath = "data/buildings.json";

	private sealed class PieceRecord
	{
		public string id { get; set; }
		public string name { get; set; }
		public string model { get; set; }
		public string kind { get; set; } = "wall";
		public float height { get; set; } = 1f;
		public float rest { get; set; }
		public List<RefundRecord> refund { get; set; } = new();
	}
	private sealed class RefundRecord { public string item { get; set; } public int count { get; set; } = 1; }
	private sealed class BuildFile
	{
		public float cell { get; set; } = 40f;
		public float[] roofCenter { get; set; }
		public float roofZ { get; set; } = 101.8f;
		public int[] roofCells { get; set; }
		public int margin { get; set; } = 3;
		public int levels { get; set; } = 4;
		public List<PieceRecord> pieces { get; set; } = new();
	}

	private static List<BuildPieceDef> _all;
	public static void Reset() => _all = null;

	/// <summary>The size of one grid square and of one level (inches): a floor is one square, a wall is one square wide and one level tall, so everything is a whole number of squares.</summary>
	public static float Cell { get { Load(); return _cell; } }

	/// <summary>Where the roof's grid starts and ends, in the car body's frame: the middle of the roof, the height of its surface, and how many squares it holds.</summary>
	public static Vector2 RoofCenter { get { Load(); return _roofCenter; } }
	public static float RoofZ { get { Load(); return _roofZ; } }
	public static (int X, int Y) RoofCells { get { Load(); return _roofCells; } }

	/// <summary>Squares beyond the roof where pieces can still be nailed on, and how many levels high one can build.</summary>
	public static int Margin { get { Load(); return _margin; } }
	public static int Levels { get { Load(); return _levels; } }

	public static IReadOnlyList<BuildPieceDef> All { get { Load(); return _all; } }

	private static float _cell = 40f, _roofZ = 101.8f;
	private static Vector2 _roofCenter = new( -21.6f, 10.8f );
	private static (int X, int Y) _roofCells = (3, 3);
	private static int _margin = 3, _levels = 4;

	private static void Load()
	{
		if ( _all is not null ) return;
		_all = new List<BuildPieceDef>();
		try
		{
			if ( !FileSystem.Mounted.FileExists( DataPath ) ) return;
			var file = Json.Deserialize<BuildFile>( FileSystem.Mounted.ReadAllText( DataPath ) );
			if ( file is null ) return;
			_cell = MathF.Max( 8f, file.cell );
			if ( file.roofCenter is { Length: 2 } ) _roofCenter = new Vector2( file.roofCenter[0], file.roofCenter[1] );
			_roofZ = file.roofZ;
			if ( file.roofCells is { Length: 2 } ) _roofCells = (Math.Max( 1, file.roofCells[0] ), Math.Max( 1, file.roofCells[1] ));
			_margin = Math.Max( 0, file.margin );
			_levels = Math.Max( 1, file.levels );
			foreach ( var p in file.pieces )
			{
				if ( string.IsNullOrEmpty( p.model ) ) continue;
				_all.Add( new BuildPieceDef { Id = p.id ?? p.model, Name = p.name ?? p.id, Model = p.model, Kind = (p.kind ?? "wall").ToLowerInvariant(), Height = p.height <= 0f ? 1f : p.height, Rest = p.rest, Refund = (p.refund ?? new()).Where( x => !string.IsNullOrEmpty( x.item ) ).Select( x => (x.item, Math.Max( 1, x.count )) ).ToList() } );
			}
		}
		catch ( System.Exception e ) { Log.Warning( $"[build] {DataPath} could not be read ({e.Message})" ); }
	}
}

/// <summary>
/// Where a piece goes on the grid. A floor is <b>one square</b> (X, Y). A wall stands on <b>one edge</b> of a square: Axis 0 runs along X on the line between
/// rows Y-1 and Y, Axis 1 runs along Y on the line between columns X-1 and X. Level is how many levels up (0 = on the roof). Squares are counted from the roof's
/// corner and may be negative (the margin around the roof).
/// </summary>
public readonly record struct Slot( bool Floor, int Axis, int X, int Y, int Level )
{
	public static Slot FloorAt( int x, int y, int level ) => new( true, 0, x, y, level );
	public static Slot WallAt( int axis, int x, int y, int level ) => new( false, axis, x, y, level );
}

/// <summary>A piece that has been built: what it is, where, and whether a nail is what holds it up.</summary>
public sealed class BuiltPiece : Component
{
	public BuildPieceDef Def { get; set; }
	public Slot Slot { get; set; }
	public bool Nailed { get; set; }
}

/// <summary>
/// Building on the car (press B near it; the grid appears). The pieces are one grid square each: a <b>floor</b> fills a square, a <b>wall</b> stands on an edge of a square,
/// and everything is a whole number of squares, so pieces meet exactly. Pieces <b>snap</b> to the square or edge your crosshair passes through, and the ghost shows
/// whether it works: green is fine, amber needs a nail, red says why not. The mouse wheel picks the piece, the left mouse places it, the right mouse takes the
/// piece you look at away, B or Esc stops.
/// <list type="bullet">
/// <item>A floor goes on the roof's squares, on top of walls (a level up), or nailed to a neighbouring piece. A wall goes on any edge of a floor, or the edge of a
/// roof square, or on top of another wall, or nailed to a neighbour. So walls can go on all four sides of a floor and a second wall can stand on the first.</item>
/// <item>Anything that is not held up needs a <b>nail</b> from a box of nails you carry, and has to touch what is already built.</item>
/// <item>Nothing can be built where you or a loose item is, and taking a piece away also drops whatever it was holding up.</item>
/// </list>
/// </summary>
public sealed class BuildSystem : Component
{
	public static bool IsBuilding { get; private set; }
	public static string Hint { get; private set; }

	/// <summary>How close you must be to the car to start building (world units).</summary>
	[Property] public float StartRange { get; set; } = 1100f;

	private BuildGrid _grid;
	private bool _disassemble;
	private ModelRenderer _marked;
	private int _selected;
	private GameObject _ghost;
	private ModelRenderer _ghostRenderer;
	private Material _ghostMaterial;
	private string _ghostFor;
	private readonly Dictionary<Slot, BuiltPiece> _built = new();

	private enum Verdict { Ok, NeedsNail, Blocked }
	private readonly record struct Choice( Slot Slot, Verdict Verdict, string Why );

	private float Cell => BuildLibrary.Cell;
	private float Level => BuildLibrary.Cell;   // one level is as tall as a square is wide

	/// <summary>Where the roof's corner is, in the grid's frame (the grid itself starts a few squares before it, so there is room to nail pieces on past the roof).</summary>
	private Vector3 RoofCorner => new(
		BuildLibrary.RoofCenter.x - BuildLibrary.RoofCells.X * Cell * 0.5f,
		BuildLibrary.RoofCenter.y - BuildLibrary.RoofCells.Y * Cell * 0.5f,
		BuildLibrary.RoofZ );

	protected override void OnStart()
	{
		BuildLibrary.Reset();
		_grid = Components.Get<BuildGrid>();
		if ( _grid.IsValid() )
		{
			_grid.ToggleKey = "";
			_grid.CellSize = Cell;
			int m = BuildLibrary.Margin;
			_grid.Origin = RoofCorner - new Vector3( m * Cell, m * Cell, 0f );
			_grid.Cells = new Vector3Int( BuildLibrary.RoofCells.X + 2 * m, BuildLibrary.RoofCells.Y + 2 * m, BuildLibrary.Levels + 1 );
		}
		_ghostMaterial = Material.Load( "materials/vehicles/ghost.vmat" );
	}

	protected override void OnDestroy() { if ( IsBuilding ) Stop(); }

	protected override void OnUpdate()
	{
		if ( !_grid.IsValid() ) return;

		if ( Input.Keyboard.Pressed( "b" ) && !InventoryHud.IsOpen )
		{
			if ( IsBuilding ) Stop();
			else if ( CanStart() ) Start();
			else PlayerGrabber.Instance?.Notify( "Get closer to the car to build" );
		}
		if ( !IsBuilding ) return;
		if ( Input.Keyboard.Pressed( "escape" ) || InventoryHud.IsOpen ) { Stop(); return; }

		var pieces = BuildLibrary.All;
		if ( pieces.Count == 0 ) { Stop(); return; }

		float wheel = Input.MouseWheel.y;
		if ( wheel != 0f ) { _selected = ((_selected - Math.Sign( wheel )) % pieces.Count + pieces.Count) % pieces.Count; Sfx.Play( Sfx.Press ); }

		var def = pieces[_selected];
		var cam = Scene.Camera;
		if ( !cam.IsValid() ) return;

		// X: building <-> taking apart
		if ( Input.Keyboard.Pressed( "x" ) )
		{
			_disassemble = !_disassemble;
			Sfx.Play( Sfx.Press );
			if ( _ghost.IsValid() ) { _ghost.Destroy(); _ghost = null; _ghostFor = null; }
			Mark( null );
		}

		if ( _disassemble )
		{
			var look = Scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + cam.WorldRotation.Forward * 800f ).IgnoreGameObjectHierarchy( Scene.GetAllObjects( true ).FirstOrDefault( g => g.Components.Get<PlayerController>( true ) is not null ) ?? GameObject ).Run();
			var target = look.Hit && look.GameObject.IsValid() ? look.GameObject.Components.Get<BuiltPiece>( FindMode.EverythingInSelfAndParent ) : null;
			Mark( target );
			Hint = target is null ? "TAKING APART   Look at a piece     X: back to building     B: stop"
				: $"TAKING APART   Left mouse: take apart the {target.Def.Name.ToLowerInvariant()}{(target.Nailed ? " (the nail comes back)" : "")}     X: back to building     B: stop";
			if ( Input.Pressed( "Attack1" ) && target is not null ) TakeApart( target );
			return;
		}

		Aim( cam, def, out var choice, out var looking );
		ShowGhost( def, choice );

		int nails = PlayerInventory.Instance?.NailsLeft ?? 0;
		Hint = choice.Verdict switch
		{
			Verdict.Ok => $"{def.Name}   Left mouse: place",
			Verdict.NeedsNail => $"{def.Name}   Left mouse: place with a nail ({nails} left)",
			_ => $"{def.Name}   {choice.Why}",
		};
		Hint += "     Wheel: piece     X: take apart     B: stop";

		if ( Input.Pressed( "Attack1" ) )
		{
			if ( choice.Verdict == Verdict.Blocked ) { Sfx.Play( Sfx.Deny ); PlayerGrabber.Instance?.Notify( choice.Why ); }
			else Place( def, choice.Slot, choice.Verdict == Verdict.NeedsNail );
		}
	}

	private bool CanStart()
	{
		var cam = Scene.Camera;
		return cam.IsValid() && (cam.WorldPosition - WorldPosition).Length < StartRange;
	}

	private void Start()
	{
		IsBuilding = true;
		_grid.Visible = true;
		Sfx.Play( Sfx.Open );
	}

	private void Stop()
	{
		IsBuilding = false;
		Hint = null;
		if ( _grid.IsValid() ) _grid.Visible = false;
		if ( _ghost.IsValid() ) _ghost.Destroy();
		_ghost = null; _ghostFor = null;
		_disassemble = false;
		Mark( null );
		Sfx.Play( Sfx.Close );
	}

	// ---- geometry of a slot ------------------------------------------------------------------------------------------------------------

	/// <summary>The box a slot fills, in the grid frame (inches). A wall's box is as thick as <paramref name="thickness"/>.</summary>
	private (Vector3 Lo, Vector3 Hi) Bounds( BuildPieceDef def, Slot s, float thickness )
	{
		var o = RoofCorner;
		float z0 = o.z + s.Level * Level;
		if ( s.Floor )
			return (new Vector3( o.x + s.X * Cell, o.y + s.Y * Cell, z0 ), new Vector3( o.x + (s.X + 1) * Cell, o.y + (s.Y + 1) * Cell, z0 + 6f ));

		float h = Level * (def?.Height ?? 1f);
		return s.Axis == 0
			? (new Vector3( o.x + s.X * Cell, o.y + s.Y * Cell - thickness * 0.5f, z0 ), new Vector3( o.x + (s.X + 1) * Cell, o.y + s.Y * Cell + thickness * 0.5f, z0 + h ))
			: (new Vector3( o.x + s.X * Cell - thickness * 0.5f, o.y + s.Y * Cell, z0 ), new Vector3( o.x + s.X * Cell + thickness * 0.5f, o.y + (s.Y + 1) * Cell, z0 + h ));
	}

	private bool InGrid( Slot s )
	{
		int m = BuildLibrary.Margin;
		var (rx, ry) = BuildLibrary.RoofCells;
		if ( s.Level < 0 || s.Level >= BuildLibrary.Levels ) return false;
		if ( s.Floor ) return s.X >= -m && s.X < rx + m && s.Y >= -m && s.Y < ry + m;
		// a wall's edge: its squares on both sides are in the grid
		return s.Axis == 0 ? s.X >= -m && s.X < rx + m && s.Y >= -m && s.Y <= ry + m : s.X >= -m && s.X <= rx + m && s.Y >= -m && s.Y < ry + m;
	}

	private bool OnRoof( int x, int y ) => x >= 0 && y >= 0 && x < BuildLibrary.RoofCells.X && y < BuildLibrary.RoofCells.Y;

	/// <summary>The two squares a wall's edge lies between.</summary>
	private static (int X, int Y) SquareBefore( Slot s ) => s.Axis == 0 ? (s.X, s.Y - 1) : (s.X - 1, s.Y);
	private static (int X, int Y) SquareAfter( Slot s ) => (s.X, s.Y);

	/// <summary>The four edges of a square.</summary>
	private static IEnumerable<(int Axis, int X, int Y)> EdgesOf( int x, int y )
	{
		yield return (0, x, y); yield return (0, x, y + 1);
		yield return (1, x, y); yield return (1, x + 1, y);
	}

	private static readonly (int, int)[] Neighbours = { (1, 0), (-1, 0), (0, 1), (0, -1) };

	private bool HasFloor( int x, int y, int level ) => _built.ContainsKey( Slot.FloorAt( x, y, level ) );
	private bool HasFullWall( int axis, int x, int y, int level ) => _built.TryGetValue( Slot.WallAt( axis, x, y, level ), out var b ) && b.Def.Height >= 1f;

	// ---- the rules ---------------------------------------------------------------------------------------------------------------------

	private bool Supported( Slot s )
	{
		if ( s.Floor )
		{
			if ( s.Level == 0 ) return OnRoof( s.X, s.Y );
			// on top of a full wall of the level below, along any of its four edges
			foreach ( var (axis, x, y) in EdgesOf( s.X, s.Y ) ) if ( HasFullWall( axis, x, y, s.Level - 1 ) ) return true;
			return false;
		}

		// a wall: on a floor next to it, on the roof, or on another wall
		var (bx, by) = SquareBefore( s ); var (ax, ay) = SquareAfter( s );
		if ( HasFloor( bx, by, s.Level ) || HasFloor( ax, ay, s.Level ) ) return true;
		if ( s.Level == 0 && (OnRoof( bx, by ) || OnRoof( ax, ay )) ) return true;
		return s.Level > 0 && HasFullWall( s.Axis, s.X, s.Y, s.Level - 1 );
	}

	/// <summary>Does it touch something already built (so a nail can fix it there)?</summary>
	private bool Touches( Slot s )
	{
		if ( s.Floor )
		{
			// a floor next to it, or a wall on one of its edges, on the same level
			foreach ( var (dx, dy) in Neighbours ) if ( HasFloor( s.X + dx, s.Y + dy, s.Level ) ) return true;
			foreach ( var (axis, x, y) in EdgesOf( s.X, s.Y ) ) if ( _built.ContainsKey( Slot.WallAt( axis, x, y, s.Level ) ) ) return true;
			return false;
		}

		// a wall: the floors beside it, a wall meeting its ends, or a wall below it
		var (bx, by) = SquareBefore( s ); var (ax, ay) = SquareAfter( s );
		foreach ( var (fx, fy) in new[] { (bx, by), (ax, ay) } )
		{
			if ( HasFloor( fx, fy, s.Level ) ) return true;
			foreach ( var (axis, x, y) in EdgesOf( fx, fy ) ) if ( (axis != s.Axis || x != s.X || y != s.Y) && _built.ContainsKey( Slot.WallAt( axis, x, y, s.Level ) ) ) return true;
		}
		return s.Level > 0 && _built.ContainsKey( Slot.WallAt( s.Axis, s.X, s.Y, s.Level - 1 ) );
	}

	private (Verdict, string) Evaluate( BuildPieceDef def, Slot s )
	{
		if ( !InGrid( s ) ) return (Verdict.Blocked, "Out of range");
		if ( _built.ContainsKey( s ) ) return (Verdict.Blocked, "Something is already built there");
		if ( InTheWay( def, s ) ) return (Verdict.Blocked, "You or something loose is in the way");

		if ( Supported( s ) ) return (Verdict.Ok, null);
		if ( !Touches( s ) ) return (Verdict.Blocked, s.Floor ? "A floor goes on the roof, on a wall, or next to a floor" : "A wall goes on a floor's edge, on the roof or on a wall");
		if ( (PlayerInventory.Instance?.NailsLeft ?? 0) < 1 ) return (Verdict.Blocked, "Nothing holds it up: needs a nail");
		return (Verdict.NeedsNail, null);
	}

	private bool InTheWay( BuildPieceDef def, Slot s )
	{
		var (lo, hi) = Bounds( def, s, 10f );
		if ( s.Floor ) hi.z = lo.z + 18f;   // a floor claims the space your feet would be in
		var frame = _grid.Frame;

		bool Inside( Vector3 world )
		{
			var l = frame.PointToLocal( world );
			return l.x > lo.x && l.x < hi.x && l.y > lo.y && l.y < hi.y && l.z > lo.z && l.z < hi.z;
		}

		var player = Scene.GetAllObjects( true ).FirstOrDefault( g => g.Components.Get<PlayerController>( true ) is not null );
		if ( player.IsValid() && (Inside( player.WorldPosition + Vector3.Up * 6f ) || Inside( player.WorldPosition + Vector3.Up * 40f ) || Inside( player.WorldPosition + Vector3.Up * 70f )) ) return true;

		foreach ( var item in Scene.GetAllComponents<Item>() )
			if ( item.IsValid() && !item.IsHeld && !item.IsWelded && Inside( item.SeenPosition ) ) return true;
		return false;
	}

	/// <summary>Whatever is no longer held up (its support was taken away) falls apart.</summary>
	private void Prune()
	{
		var stable = new HashSet<Slot>();
		var rest = _built.Values.Where( b => b.IsValid() ).ToList();

		bool changed = true;
		while ( changed )
		{
			changed = false;
			foreach ( var b in rest.ToList() )
			{
				bool holds = SupportedBy( b.Slot, stable ) || (b.Nailed && TouchesAny( b.Slot, stable ));
				if ( !holds ) continue;
				stable.Add( b.Slot );
				rest.Remove( b );
				changed = true;
			}
		}

		foreach ( var fallen in rest )
		{
			_built.Remove( fallen.Slot );
			Sfx.Play( Sfx.Wood, fallen.WorldPosition );
			fallen.GameObject.Destroy();
		}
		if ( rest.Count > 0 ) PlayerGrabber.Instance?.Notify( rest.Count == 1 ? "A piece fell off" : $"{rest.Count} pieces fell off" );
	}

	// support computed against the pieces known to stand (the ones that are not themselves falling)
	private bool SupportedBy( Slot s, HashSet<Slot> stable )
	{
		if ( s.Floor )
		{
			if ( s.Level == 0 ) return OnRoof( s.X, s.Y );
			foreach ( var (axis, x, y) in EdgesOf( s.X, s.Y ) ) if ( stable.Contains( Slot.WallAt( axis, x, y, s.Level - 1 ) ) && HasFullWall( axis, x, y, s.Level - 1 ) ) return true;
			return false;
		}

		var (bx, by) = SquareBefore( s ); var (ax, ay) = SquareAfter( s );
		if ( stable.Contains( Slot.FloorAt( bx, by, s.Level ) ) || stable.Contains( Slot.FloorAt( ax, ay, s.Level ) ) ) return true;
		if ( s.Level == 0 && (OnRoof( bx, by ) || OnRoof( ax, ay )) ) return true;
		return s.Level > 0 && stable.Contains( Slot.WallAt( s.Axis, s.X, s.Y, s.Level - 1 ) ) && HasFullWall( s.Axis, s.X, s.Y, s.Level - 1 );
	}

	private bool TouchesAny( Slot s, HashSet<Slot> stable )
	{
		if ( s.Floor )
		{
			foreach ( var (dx, dy) in Neighbours ) if ( stable.Contains( Slot.FloorAt( s.X + dx, s.Y + dy, s.Level ) ) ) return true;
			foreach ( var (axis, x, y) in EdgesOf( s.X, s.Y ) ) if ( stable.Contains( Slot.WallAt( axis, x, y, s.Level ) ) ) return true;
			return false;
		}
		var (bx, by) = SquareBefore( s ); var (ax, ay) = SquareAfter( s );
		foreach ( var (fx, fy) in new[] { (bx, by), (ax, ay) } )
		{
			if ( stable.Contains( Slot.FloorAt( fx, fy, s.Level ) ) ) return true;
			foreach ( var (axis, x, y) in EdgesOf( fx, fy ) ) if ( (axis != s.Axis || x != s.X || y != s.Y) && stable.Contains( Slot.WallAt( axis, x, y, s.Level ) ) ) return true;
		}
		return s.Level > 0 && stable.Contains( Slot.WallAt( s.Axis, s.X, s.Y, s.Level - 1 ) );
	}

	// ---- aiming: snap to the square or edge the crosshair passes through ----------------------------------------------------------------

	private IEnumerable<Slot> Candidates( BuildPieceDef def )
	{
		int m = BuildLibrary.Margin;
		var (rx, ry) = BuildLibrary.RoofCells;
		for ( int level = 0; level < BuildLibrary.Levels; level++ )
		{
			if ( def.IsFloor )
			{
				for ( int x = -m; x < rx + m; x++ ) for ( int y = -m; y < ry + m; y++ ) yield return Slot.FloorAt( x, y, level );
			}
			else
			{
				for ( int x = -m; x < rx + m; x++ ) for ( int y = -m; y <= ry + m; y++ ) yield return Slot.WallAt( 0, x, y, level );
				for ( int x = -m; x <= rx + m; x++ ) for ( int y = -m; y < ry + m; y++ ) yield return Slot.WallAt( 1, x, y, level );
			}
		}
	}

	private void Aim( CameraComponent cam, BuildPieceDef def, out Choice choice, out BuiltPiece looking )
	{
		looking = null;
		var player = Scene.GetAllObjects( true ).FirstOrDefault( g => g.Components.Get<PlayerController>( true ) is not null );
		var tr = Scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + cam.WorldRotation.Forward * 800f ).IgnoreGameObjectHierarchy( player ?? GameObject ).Run();
		if ( tr.Hit && tr.GameObject.IsValid() ) looking = tr.GameObject.Components.Get<BuiltPiece>( FindMode.EverythingInSelfAndParent );

		var frame = _grid.Frame;
		var o = frame.PointToLocal( cam.WorldPosition );
		var dir = frame.Rotation.Inverse * cam.WorldRotation.Forward;
		float reach = 800f;
		float wall = tr.Hit ? tr.Distance : reach;   // something solid stops the ray here

		bool haveHit = false, haveNear = false, haveAny = false;
		Choice bestHit = default, bestNear = default, bestAny = default;
		float hitScore = float.MaxValue, nearD = float.MaxValue, anyD = float.MaxValue;

		foreach ( var slot in Candidates( def ) )
		{
			// the volume you can aim at: a floor's square, or a band around a wall's edge that is generous enough to hit easily
			var (lo, hi) = Bounds( def, slot, slot.Floor ? 0f : Cell * 0.55f );
			if ( slot.Floor ) { lo.z -= 4f; hi.z += 10f; }

			bool hits = RayBox( o, dir, lo, hi, out float t ) && t <= reach;
			var centre = (lo + hi) * 0.5f;
			float along = MathF.Max( 0f, Vector3.Dot( centre - o, dir ) );
			float miss = (centre - (o + dir * along)).Length;

			if ( !haveAny || miss < anyD ) { anyD = miss; bestAny = new Choice( slot, Verdict.Blocked, null ); haveAny = true; }
			if ( !hits && miss > Cell * 1.1f ) continue;

			var (verdict, why) = Evaluate( def, slot );
			if ( verdict == Verdict.Blocked )
			{
				if ( miss <= anyD ) bestAny = new Choice( slot, verdict, why );
				continue;
			}

			// the more exactly the ray goes through the line of a wall's edge, the better (it separates the two edges of one square)
			float lateral = 0f;
			if ( hits && !slot.Floor )
			{
				var p = o + dir * t;
				var edge = Bounds( def, slot, 0f );
				lateral = slot.Axis == 0 ? MathF.Abs( p.y - edge.Lo.y ) : MathF.Abs( p.x - edge.Lo.x );
			}

			float penalty = (verdict == Verdict.NeedsNail ? 0.1f : 0f) * Cell;   // equally good: prefer the spot that needs no nail
			if ( hits && t <= wall + 0.6f * Cell )
			{
				float score = t + lateral * 0.6f + penalty;
				if ( score < hitScore ) { hitScore = score; bestHit = new Choice( slot, verdict, why ); haveHit = true; }
			}
			else if ( miss + penalty < nearD ) { nearD = miss + penalty; bestNear = new Choice( slot, verdict, why ); haveNear = true; }
		}

		if ( haveHit ) choice = bestHit;
		else if ( haveNear && nearD < Cell * 0.9f ) choice = bestNear;
		else
		{
			// nothing works here: show the nearest spot in red with the reason
			var (verdict, why) = Evaluate( def, bestAny.Slot );
			choice = new Choice( bestAny.Slot, verdict == Verdict.Ok ? Verdict.Blocked : verdict, why ?? "Look at the roof or at what is built" );
		}
	}

	/// <summary>Slab test: does the ray cross the box, and at what distance along it?</summary>
	private static bool RayBox( Vector3 o, Vector3 d, Vector3 lo, Vector3 hi, out float tNear )
	{
		float t0 = 0f, t1 = float.MaxValue;
		tNear = 0f;
		for ( int i = 0; i < 3; i++ )
		{
			float oi = i == 0 ? o.x : i == 1 ? o.y : o.z;
			float di = i == 0 ? d.x : i == 1 ? d.y : d.z;
			float l = i == 0 ? lo.x : i == 1 ? lo.y : lo.z;
			float h = i == 0 ? hi.x : i == 1 ? hi.y : hi.z;
			if ( MathF.Abs( di ) < 1e-6f )
			{
				if ( oi < l || oi > h ) return false;
				continue;
			}
			float a = (l - oi) / di, b = (h - oi) / di;
			if ( a > b ) (a, b) = (b, a);
			t0 = MathF.Max( t0, a );
			t1 = MathF.Min( t1, b );
			if ( t0 > t1 ) return false;
		}
		tNear = t0;
		return true;
	}

	// ---- the pieces --------------------------------------------------------------------------------------------------------------------

	/// <summary>How a piece is scaled to fit its slot exactly: a floor to the square, a wall to the edge's length and its height.</summary>
	private Vector3 ScaleOf( BuildPieceDef def, Model model )
	{
		var size = model.Bounds.Size;
		if ( def.IsFloor )
		{
			float sx = Cell / MathF.Max( 0.01f, size.x ), sy = Cell / MathF.Max( 0.01f, size.y );
			return new Vector3( sx, sy, (sx + sy) * 0.5f );
		}
		float sl = Cell / MathF.Max( 0.01f, size.y );                           // length along the edge
		float sh = Level * def.Height / MathF.Max( 0.01f, size.z );             // height
		return new Vector3( sl, sl, sh );
	}

	/// <summary>The quarter turns a piece needs: walls run along their edge; the models' long side is Y, so a wall along X is turned a quarter.</summary>
	private static int TurnOf( Slot s ) => !s.Floor && s.Axis == 0 ? 1 : 0;

	/// <summary>
	/// Where a piece's pivot goes (in the grid's frame). The models' pivots are not at their middles, so the middle of the scaled model is put exactly on its slot:
	/// the middle of the square (a floor), or the middle of the edge (a wall, centred on the edge line), standing on the bottom of its level.
	/// </summary>
	private Vector3 PieceLocalPosition( BuildPieceDef def, Slot s, Model model )
	{
		var scale = ScaleOf( def, model );
		var centre = model.Bounds.Center * scale;
		float height = model.Bounds.Size.z * scale.z;
		var o = RoofCorner;
		var rot = Rotation.FromYaw( TurnOf( s ) * 90f );

		Vector3 middle;
		if ( s.Floor ) middle = new Vector3( o.x + (s.X + 0.5f) * Cell, o.y + (s.Y + 0.5f) * Cell, o.z + s.Level * Level + height * 0.5f + def.Rest );
		else if ( s.Axis == 0 ) middle = new Vector3( o.x + (s.X + 0.5f) * Cell, o.y + s.Y * Cell, o.z + s.Level * Level + height * 0.5f + def.Rest );
		else middle = new Vector3( o.x + s.X * Cell, o.y + (s.Y + 0.5f) * Cell, o.z + s.Level * Level + height * 0.5f + def.Rest );
		return middle - rot * centre;
	}

	private void ShowGhost( BuildPieceDef def, Choice choice )
	{
		var model = LongTrip.World.Props.ScatterLibrary.GetModel( def.Model );
		if ( model is null ) return;

		if ( !_ghost.IsValid() || _ghostFor != def.Id )
		{
			if ( _ghost.IsValid() ) _ghost.Destroy();
			_ghost = Scene.CreateObject();
			_ghost.Name = "Build ghost";
			_ghost.Flags |= GameObjectFlags.NotSaved;
			_ghostRenderer = _ghost.Components.Create<ModelRenderer>();
			_ghostRenderer.Model = model;
			if ( _ghostMaterial is not null ) _ghostRenderer.MaterialOverride = _ghostMaterial;
			_ghostRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_ghostFor = def.Id;
		}

		var frame = _grid.Frame;
		_ghost.WorldScale = ScaleOf( def, model );
		_ghost.WorldPosition = frame.PointToWorld( PieceLocalPosition( def, choice.Slot, model ) );
		_ghost.WorldRotation = frame.Rotation * Rotation.FromYaw( TurnOf( choice.Slot ) * 90f );
		_ghostRenderer.Tint = choice.Verdict switch
		{
			Verdict.Ok => new Color( 0.4f, 1f, 0.5f, 0.7f ),
			Verdict.NeedsNail => new Color( 1f, 0.75f, 0.25f, 0.7f ),
			_ => new Color( 1f, 0.35f, 0.3f, 0.7f ),
		};
	}

	private void Place( BuildPieceDef def, Slot slot, bool nailed )
	{
		var model = LongTrip.World.Props.ScatterLibrary.GetModel( def.Model );
		if ( model is null ) return;
		if ( nailed && !(PlayerInventory.Instance?.ConsumeNail() ?? false) ) { Sfx.Play( Sfx.Deny ); return; }

		var anchor = _grid.Anchor.IsValid() ? _grid.Anchor : GameObject;
		var go = Scene.CreateObject();
		go.Name = "Built " + def.Name;
		go.Flags |= GameObjectFlags.NotSaved;
		go.SetParent( anchor, false );
		go.LocalPosition = PieceLocalPosition( def, slot, model );
		go.LocalRotation = Rotation.FromYaw( TurnOf( slot ) * 90f );
		go.LocalScale = ScaleOf( def, model );

		go.Components.Create<ModelRenderer>().Model = model;
		LongTrip.World.Props.ColliderGenerator.Add( go, model );   // shaped from the piece's real geometry

		var piece = go.Components.Create<BuiltPiece>();
		piece.Def = def; piece.Slot = slot; piece.Nailed = nailed;
		_built[slot] = piece;
		Sfx.Play( nailed ? Sfx.Metal : Sfx.Wood, go.WorldPosition );
	}

	/// <summary>Tint the piece you are about to take apart (null: none).</summary>
	private void Mark( BuiltPiece piece )
	{
		var renderer = piece.IsValid() ? piece.Components.Get<ModelRenderer>() : null;
		if ( renderer == _marked ) return;
		if ( _marked.IsValid() ) _marked.Tint = Color.White;
		_marked = renderer;
		if ( _marked.IsValid() ) _marked.Tint = new Color( 1f, 0.55f, 0.5f );
	}

	/// <summary>Take a piece apart: what it cost comes back to you (into your pockets, or onto the floor if they are full), and a nail it was held with too.</summary>
	private void TakeApart( BuiltPiece piece )
	{
		var def = piece.Def;
		bool nailed = piece.Nailed;
		Mark( null );
		Remove( piece );

		var inventory = PlayerInventory.Instance;
		var cam = Scene.Camera;
		void Give( string id, float amount )
		{
			var stored = new ItemContainer.Stored( id, amount, null );
			if ( inventory is not null && inventory.TryAdd( stored ) ) return;
			if ( cam.IsValid() ) ItemLibrary.Spawn( Scene, id, cam.WorldPosition + cam.WorldRotation.Forward * 70f, Rotation.Identity, amount );
		}

		foreach ( var (item, count) in def.Refund ) for ( int i = 0; i < count; i++ ) Give( item, 1f );
		if ( nailed )
		{
			float uses = MathF.Max( 1f, ItemLibrary.Get( PlayerInventory.NailId )?.Uses ?? 1 );
			Give( PlayerInventory.NailId, 1f / uses );
		}
	}

	private void Remove( BuiltPiece piece )
	{
		if ( !_built.Remove( piece.Slot ) ) return;
		Sfx.Play( Sfx.Metal, piece.WorldPosition );
		piece.GameObject.Destroy();
		Prune();
	}
}
