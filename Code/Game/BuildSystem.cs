namespace LongTrip.Gameplay;

/// <summary>One kind of building piece, from <c>Assets/data/buildings.json</c>.</summary>
public sealed class BuildPieceDef
{
	public string Id, Name, Model, Layer = "wall";

	/// <summary>Used when <see cref="Fit"/> is empty: one scale on every axis.</summary>
	public float Scale = 1f;

	/// <summary>How many grid cells it fills along the car's forward, left and up axes (before it is turned).</summary>
	public Vector3Int Cells = new( 1, 1, 1 );

	/// <summary>
	/// "xy": stretched to fill its cells exactly along x and y (floors). "yz": width and height fill the cells exactly (walls). "y": the width fills the cells and
	/// the height follows (a wall that does not reach the top of its cells). Empty: <see cref="Scale"/>. Pieces that fill their cells exactly
	/// line up with each other with no gaps and no overlaps.
	/// </summary>
	public string Fit = "";

	/// <summary>How far (inches) it is lifted off the bottom of its cells, so it does not fight the surface it stands on.</summary>
	public float Rest;
}

public static class BuildLibrary
{
	public const string DataPath = "data/buildings.json";

	private sealed class PieceRecord
	{
		public string id { get; set; }
		public string name { get; set; }
		public string model { get; set; }
		public string layer { get; set; } = "wall";
		public float scale { get; set; } = 1f;
		public int[] cells { get; set; }
		public string fit { get; set; }
		public float rest { get; set; }
	}
	private sealed class BuildFile
	{
		public int roofLevel { get; set; } = 4;
		public int[] roofX { get; set; }
		public int[] roofY { get; set; }
		public List<PieceRecord> pieces { get; set; } = new();
	}

	private static List<BuildPieceDef> _all;
	private static int _roof = 4;
	private static (int Min, int Max) _roofX = (4, 9), _roofY = (1, 6);
	public static void Reset() => _all = null;

	public static int RoofLevel { get { Load(); return _roof; } }

	/// <summary>The cells (x from min up to but not including max) that lie over the roof: what a piece can stand on without a nail.</summary>
	public static (int Min, int Max) RoofX { get { Load(); return _roofX; } }
	public static (int Min, int Max) RoofY { get { Load(); return _roofY; } }

	public static IReadOnlyList<BuildPieceDef> All { get { Load(); return _all; } }

	private static void Load()
	{
		if ( _all is not null ) return;
		_all = new List<BuildPieceDef>();
		try
		{
			if ( !FileSystem.Mounted.FileExists( DataPath ) ) return;
			var file = Json.Deserialize<BuildFile>( FileSystem.Mounted.ReadAllText( DataPath ) );
			if ( file is null ) return;
			_roof = file.roofLevel;
			if ( file.roofX is { Length: 2 } ) _roofX = (file.roofX[0], file.roofX[1]);
			if ( file.roofY is { Length: 2 } ) _roofY = (file.roofY[0], file.roofY[1]);
			foreach ( var p in file.pieces )
			{
				if ( string.IsNullOrEmpty( p.model ) ) continue;
				var c = p.cells is { Length: 3 } ? new Vector3Int( Math.Max( 1, p.cells[0] ), Math.Max( 1, p.cells[1] ), Math.Max( 1, p.cells[2] ) ) : new Vector3Int( 1, 1, 1 );
				_all.Add( new BuildPieceDef { Id = p.id ?? p.model, Name = p.name ?? p.id, Model = p.model, Layer = (p.layer ?? "wall").ToLowerInvariant(), Scale = p.scale <= 0f ? 1f : p.scale, Cells = c, Fit = (p.fit ?? "").ToLowerInvariant(), Rest = p.rest } );
			}
		}
		catch ( System.Exception e ) { Log.Warning( $"[build] {DataPath} could not be read ({e.Message})" ); }
	}
}

/// <summary>A piece that has been built: remembers what it is, which cells it holds, and whether a nail is what holds it up.</summary>
public sealed class BuiltPiece : Component
{
	public BuildPieceDef Def { get; set; }
	public Vector3Int Min { get; set; }
	public Vector3Int Size { get; set; }
	public bool Nailed { get; set; }

	/// <summary>How far (in cells, in the grid's frame) the piece sits off the middle of its cells: a wall is centred on the edge line of the floor it stands on.</summary>
	public Vector3 Shift { get; set; }
	public int Turn { get; set; }

	public Vector3Int Max => new( Min.x + Size.x, Min.y + Size.y, Min.z + Size.z );
}

/// <summary>
/// Building on the car. Press B near the car to start: the local grid appears, the mouse wheel picks a piece, R turns it, the left mouse places it where
/// the ghost is (green: fine, amber: needs a nail, red: not possible), the right mouse takes the piece you look at away, B or Esc stops.
/// <list type="bullet">
/// <item>Pieces fill their cells exactly, so neighbours meet edge to edge. They are fixed to the car's frame and ride with it.</item>
/// <item>A piece stands on the roof (inside the roof's cells) or on a wall under it. Anything else needs a <b>nail</b> (from a box of nails you carry) and
/// has to touch a piece that is already built: that is how a floor is extended out sideways past the roof.</item>
/// <item>Nothing can be built where you or a loose item is standing, and taking away a piece also drops whatever it was holding up.</item>
/// </list>
/// </summary>
public sealed class BuildSystem : Component
{
	public static bool IsBuilding { get; private set; }
	public static string Hint { get; private set; }

	/// <summary>How close you must be to the car to start building (world units).</summary>
	[Property] public float StartRange { get; set; } = 1100f;

	private BuildGrid _grid;
	private int _selected;
	private int _turn;   // quarter turns
	private Vector3 _shift;   // the snapped piece's offset onto an edge (cells)
	private GameObject _ghost;
	private ModelRenderer _ghostRenderer;
	private Material _ghostMaterial;
	private string _ghostFor;
	private readonly List<BuiltPiece> _built = new();

	private enum Verdict { Ok, NeedsNail, Blocked }

	protected override void OnStart()
	{
		BuildLibrary.Reset();
		_grid = Components.Get<BuildGrid>();
		if ( _grid.IsValid() )
		{
			// the grid covers the car; its layers are counted up from the roof so the first free layer above the roof is "roof level"
			_grid.ToggleKey = "";
			_grid.Origin = new Vector3( -150f, -60f, 94.3f - BuildLibrary.RoofLevel * _grid.CellSize );
			_grid.Cells = new Vector3Int( 15, 6, BuildLibrary.RoofLevel + 6 );
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
		if ( Input.Keyboard.Pressed( "r" ) ) { _turn = (_turn + 1) % 4; Sfx.Play( Sfx.Press ); }

		var def = pieces[_selected];
		var cam = Scene.Camera;
		if ( !cam.IsValid() ) return;

		Aim( cam, def, out var snap, out var looking );
		var min = snap.Min;
		var size = snap.Size;
		_turn = snap.Turn;   // walls turn themselves to lie along the edge they snap to
		_shift = snap.Shift;
		var (verdict, why) = (snap.Verdict, snap.Why);

		ShowGhost( def, min, size, verdict );

		int nails = PlayerInventory.Instance?.NailsLeft ?? 0;
		Hint = verdict switch
		{
			Verdict.Ok => $"{def.Name}   Left mouse: place",
			Verdict.NeedsNail => $"{def.Name}   Left mouse: place with a nail ({nails} left)",
			_ => $"{def.Name}   {why}",
		};
		Hint += "     Wheel: piece     R: turn     Right mouse: remove     B: stop";

		if ( Input.Pressed( "Attack1" ) )
		{
			if ( verdict == Verdict.Blocked ) { Sfx.Play( Sfx.Deny ); PlayerGrabber.Instance?.Notify( why ); }
			else Place( def, min, size, verdict == Verdict.NeedsNail );
		}
		else if ( Input.Pressed( "Attack2" ) && looking is not null )
		{
			Remove( looking );
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
		Sfx.Play( Sfx.Close );
	}

	// ---- aiming ---------------------------------------------------------------------------------------------------------------

	private Vector3Int FootprintSize( BuildPieceDef def, int turn ) => turn % 2 == 1 ? new Vector3Int( def.Cells.y, def.Cells.x, def.Cells.z ) : def.Cells;

	private readonly record struct Snap( Vector3Int Min, Vector3Int Size, int Turn, Verdict Verdict, string Why, Vector3 Shift );
	private readonly record struct Candidate( Vector3Int Min, int Turn, Vector3 Shift );

	/// <summary>
	/// Where the piece goes: it snaps to what is already built, the way building works in Fortnite. A floor goes edge to edge next to a floor, on the same
	/// tile lattice; a wall stands on the edge of a floor (and turns itself to lie along that edge), continues a wall, or stacks on one; a floor goes on top
	/// of a wall. With nothing built yet it snaps to the roof's own tile lattice. Of everything that fits, the placement nearest to where you look wins.
	/// </summary>
	private void Aim( CameraComponent cam, BuildPieceDef def, out Snap snap, out BuiltPiece looking )
	{
		looking = null;
		var player = Scene.GetAllObjects( true ).FirstOrDefault( g => g.Components.Get<PlayerController>( true ) is not null );
		var tr = Scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + cam.WorldRotation.Forward * 800f )
			.IgnoreGameObjectHierarchy( player ?? GameObject ).Run();

		if ( tr.Hit && tr.GameObject.IsValid() ) looking = tr.GameObject.Components.Get<BuiltPiece>( FindMode.EverythingInSelfAndParent );

		var frame = _grid.Frame;
		float cell = _grid.CellSize;

		// the crosshair ray in cell units, in the grid's own frame
		var o = (frame.PointToLocal( cam.WorldPosition ) - _grid.Origin) / cell;
		var dir = frame.Rotation.Inverse * cam.WorldRotation.Forward;
		float reach = 800f / cell;
		float wall = tr.Hit ? tr.Distance / cell : reach;   // something solid (the car, a built piece) stops the ray here

		Snap? bestHit = null, bestNear = null, bestAny = null;
		float hitT = float.MaxValue, nearD = float.MaxValue, anyD = float.MaxValue;

		foreach ( var c in Candidates( def ) )
		{
			var size = FootprintSize( def, c.Turn );
			var (verdict, why) = Evaluate( def, c.Min, size );
			var candidate = new Snap( c.Min, size, c.Turn, verdict, why, c.Shift );

			var lo = new Vector3( c.Min.x, c.Min.y, c.Min.z );
			var hi = new Vector3( c.Min.x + size.x, c.Min.y + size.y, c.Min.z + size.z );
			bool hits = RayBox( o, dir, lo, hi, out float t ) && t <= reach;

			// closest the ray comes to the middle of the piece (in cells)
			var centre = (lo + hi) * 0.5f;
			float along = MathF.Max( 0f, Vector3.Dot( centre - o, dir ) );
			float miss = (centre - (o + dir * along)).Length;

			float penalty = verdict == Verdict.NeedsNail ? 0.15f : 0f;   // equally good: prefer the spot that needs no nail

			if ( miss + penalty < anyD ) { anyD = miss + penalty; bestAny = candidate; }
			if ( verdict == Verdict.Blocked ) continue;

			// a piece the ray passes through, not hidden behind something solid (a little slack so pieces can be placed against a surface)
			if ( hits && t <= wall + 0.6f && t + penalty < hitT ) { hitT = t + penalty; bestHit = candidate; }
			else if ( miss + penalty < nearD ) { nearD = miss + penalty; bestNear = candidate; }
		}

		// 1. the piece you are looking through, 2. the nearest one to the crosshair (only if close), 3. show the nearest spot in red with the reason
		if ( bestHit is { } h ) snap = h;
		else if ( bestNear is { } n && nearD < 1.2f ) snap = n;
		else snap = bestAny ?? new Snap( new Vector3Int( BuildLibrary.RoofX.Min, BuildLibrary.RoofY.Min, BuildLibrary.RoofLevel ), FootprintSize( def, _turn ), _turn, Verdict.Blocked, "Nowhere to put it", Vector3.Zero );
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

	private int TurnFor( BuildPieceDef def, int parity )
	{
		// floors are square: they keep the turn you gave them; a wall lies one way or the other
		if ( def.Cells.x == def.Cells.y ) return _turn;
		return parity == _turn % 2 ? _turn : (_turn + 1) % 4;
	}

	/// <summary>
	/// The offset a wall has from the middle of its cell so that it is centred on the edge line of the floor beside it: towards the low side of the
	/// cell (<paramref name="high"/> false) or the high side. Only along the wall's thin axis (x when it is not turned, y when it is turned a quarter).
	/// </summary>
	private static Vector3 EdgeShift( BuildPieceDef def, int turn, bool high )
	{
		if ( def.Layer != "wall" ) return Vector3.Zero;
		float d = high ? 0.5f : -0.5f;
		return turn % 2 == 0 ? new Vector3( d, 0f, 0f ) : new Vector3( 0f, d, 0f );
	}

	private IEnumerable<Candidate> Candidates( BuildPieceDef def )
	{
		int roof = BuildLibrary.RoofLevel;
		var rx = BuildLibrary.RoofX;
		var ry = BuildLibrary.RoofY;
		var seen = new HashSet<(int, int, int, int, int)>();
		var found = new List<Candidate>();

		void Add( int x, int y, int z, int turn, Vector3 shift )
		{
			// the same cells with the edge on the other side are different placements for a wall
			int side = shift.x > 0f || shift.y > 0f ? 1 : 0;
			if ( !seen.Add( (x, y, z, turn % 2, side) ) ) return;
			found.Add( new Candidate( new Vector3Int( x, y, z ), turn, shift ) );
		}

		int parities = def.Cells.x == def.Cells.y ? 1 : 2;

		// 1. the roof's own tile lattice (so the first pieces already line up with each other), reaching a tile past the roof for nails
		for ( int p = 0; p < parities; p++ )
		{
			int turn = TurnFor( def, p );
			var size = FootprintSize( def, turn );
			for ( int x = rx.Min - size.x; x < rx.Max + size.x; x += size.x )
			for ( int y = ry.Min - size.y; y < ry.Max + size.y; y += size.y )
			{
				if ( def.Layer == "wall" )
				{
					// on an edge line of the lattice: the line at the low side of the cell, or at the high side (the far edge of the roof)
					Add( x, y, roof, turn, EdgeShift( def, turn, false ) );
					Add( x, y, roof, turn, EdgeShift( def, turn, true ) );
				}
				else Add( x, y, roof, turn, Vector3.Zero );
			}
		}

		// 2. against what is already built
		foreach ( var b in _built )
		{
			if ( !b.IsValid() ) continue;

			for ( int p = 0; p < parities; p++ )
			{
				int turn = TurnFor( def, p );
				var size = FootprintSize( def, turn );
				var low = EdgeShift( def, turn, false );
				var high = EdgeShift( def, turn, true );

				if ( b.Def.Layer == def.Layer )
				{
					// a neighbour of the same kind that lies the same way keeps its edge line; a differently turned one gets either
					Vector3[] shifts = def.Layer == "wall" && b.Turn % 2 == turn % 2 ? new[] { b.Shift } : new[] { low, high };

					foreach ( var sh in shifts )
					{
						// next to it on the same lattice (a piece's width along the edge, so tiles stay in step) ...
						for ( int k = -1; k <= 1; k++ )
						{
							Add( b.Max.x, b.Min.y + k * size.y, b.Min.z, turn, sh );
							Add( b.Min.x - size.x, b.Min.y + k * size.y, b.Min.z, turn, sh );
							Add( b.Min.x + k * size.x, b.Max.y, b.Min.z, turn, sh );
							Add( b.Min.x + k * size.x, b.Min.y - size.y, b.Min.z, turn, sh );
						}

						// ... or stacked on it, for walls
						if ( def.Layer == "wall" ) Add( b.Min.x, b.Min.y, b.Max.z, turn, sh );
					}
				}
				else if ( def.Layer == "wall" && b.Def.Layer == "floor" )
				{
					// a wall stands on the edge of a floor, along that edge, centred on the edge line
					if ( turn % 2 == 0 )
					{
						for ( int k = 0; k * size.y < b.Size.y; k++ )
						{
							Add( b.Min.x, b.Min.y + k * size.y, b.Min.z, turn, low );
							Add( b.Max.x - size.x, b.Min.y + k * size.y, b.Min.z, turn, high );
						}
					}
					else
					{
						for ( int k = 0; k * size.x < b.Size.x; k++ )
						{
							Add( b.Min.x + k * size.x, b.Min.y, b.Min.z, turn, low );
							Add( b.Min.x + k * size.x, b.Max.y - size.y, b.Min.z, turn, high );
						}
					}
				}
				else if ( def.Layer == "floor" && b.Def.Layer == "wall" )
				{
					// a floor goes on the top of a wall, its edge on the wall's edge line
					foreach ( int x in new[] { b.Min.x, b.Max.x - size.x } )
					foreach ( int y in new[] { b.Min.y, b.Max.y - size.y } )
						Add( x, y, b.Max.z, turn, Vector3.Zero );
				}
			}
		}

		return found;
	}

	// ---- the rules -----------------------------------------------------------------------------------------------------------------

	private static bool Overlaps1D( int aMin, int aMax, int bMin, int bMax ) => aMin < bMax && bMin < aMax;

	private static bool OverlapsBox( Vector3Int aMin, Vector3Int aSize, BuiltPiece b )
		=> Overlaps1D( aMin.x, aMin.x + aSize.x, b.Min.x, b.Max.x ) && Overlaps1D( aMin.y, aMin.y + aSize.y, b.Min.y, b.Max.y ) && Overlaps1D( aMin.z, aMin.z + aSize.z, b.Min.z, b.Max.z );

	/// <summary>Do the two boxes touch face to face (sharing a face's area, not just an edge or corner)?</summary>
	private static bool Touches( Vector3Int aMin, Vector3Int aSize, Vector3Int bMin, Vector3Int bMax )
	{
		int ax = aMin.x + aSize.x, ay = aMin.y + aSize.y, az = aMin.z + aSize.z;
		bool ox = Overlaps1D( aMin.x, ax, bMin.x, bMax.x ), oy = Overlaps1D( aMin.y, ay, bMin.y, bMax.y ), oz = Overlaps1D( aMin.z, az, bMin.z, bMax.z );
		bool tx = ax == bMin.x || bMax.x == aMin.x, ty = ay == bMin.y || bMax.y == aMin.y, tz = az == bMin.z || bMax.z == aMin.z;
		return (tx && oy && oz) || (ty && ox && oz) || (tz && ox && oy);
	}

	/// <summary>Does it stand on the roof (all of it inside the roof's cells), or on a wall piece that is itself held up?</summary>
	private bool Supported( Vector3Int min, Vector3Int size, IReadOnlyCollection<BuiltPiece> stable, BuiltPiece self = null )
	{
		int roof = BuildLibrary.RoofLevel;
		var rx = BuildLibrary.RoofX;
		var ry = BuildLibrary.RoofY;

		if ( min.z == roof && min.x >= rx.Min && min.x + size.x <= rx.Max && min.y >= ry.Min && min.y + size.y <= ry.Max ) return true;

		// standing on a wall (the top of a wall touches the bottom of this piece)
		foreach ( var b in stable )
		{
			if ( b == self || b.Def.Layer != "wall" ) continue;
			if ( b.Max.z == min.z && Overlaps1D( min.x, min.x + size.x, b.Min.x, b.Max.x ) && Overlaps1D( min.y, min.y + size.y, b.Min.y, b.Max.y ) ) return true;
		}
		return false;
	}

	private bool InTheWay( Vector3Int min, Vector3Int size )
	{
		float cell = _grid.CellSize;
		var frame = _grid.Frame;
		var lo = _grid.Origin + new Vector3( min.x, min.y, min.z ) * cell - new Vector3( 4f, 4f, 0f );
		var hi = _grid.Origin + new Vector3( min.x + size.x, min.y + size.y, min.z + size.z ) * cell + new Vector3( 4f, 4f, 0f );

		bool Inside( Vector3 world )
		{
			var l = frame.PointToLocal( world );
			return l.x > lo.x && l.x < hi.x && l.y > lo.y && l.y < hi.y && l.z > lo.z && l.z < hi.z;
		}

		var player = Scene.GetAllObjects( true ).FirstOrDefault( g => g.Components.Get<PlayerController>( true ) is not null );
		if ( player.IsValid() && (Inside( player.WorldPosition + Vector3.Up * 6f ) || Inside( player.WorldPosition + Vector3.Up * 40f ) || Inside( player.WorldPosition + Vector3.Up * 70f )) ) return true;

		foreach ( var item in Scene.GetAllComponents<Item>() )
			if ( item.IsValid() && !item.IsHeld && Inside( item.WorldPosition ) ) return true;
		return false;
	}

	private (Verdict, string) Evaluate( BuildPieceDef def, Vector3Int min, Vector3Int size )
	{
		int roof = BuildLibrary.RoofLevel;
		if ( min.z < roof ) return (Verdict.Blocked, "Pieces go on the roof or above");

		for ( int x = 0; x < size.x; x++ )
		for ( int y = 0; y < size.y; y++ )
		for ( int z = 0; z < size.z; z++ )
			if ( !_grid.InRange( new Vector3Int( min.x + x, min.y + y, min.z + z ) ) ) return (Verdict.Blocked, "Out of range");

		foreach ( var b in _built )
			if ( b.IsValid() && b.Def.Layer == def.Layer && OverlapsBox( min, size, b ) ) return (Verdict.Blocked, "Something is already built there");

		if ( InTheWay( min, size ) ) return (Verdict.Blocked, "You or something loose is in the way");

		var stable = _built.Where( b => b.IsValid() ).ToList();
		if ( Supported( min, size, stable ) ) return (Verdict.Ok, null);

		// not held up by anything: a nail can fix it to a piece it touches
		bool touching = stable.Any( b => Touches( min, size, b.Min, b.Max ) );
		if ( !touching ) return (Verdict.Blocked, "Nothing to stand on or to nail it to");
		if ( (PlayerInventory.Instance?.NailsLeft ?? 0) < 1 ) return (Verdict.Blocked, "Nothing holds it up: needs a nail");
		return (Verdict.NeedsNail, null);
	}

	/// <summary>Whatever is no longer held up (its support was taken away) falls apart.</summary>
	private void Prune()
	{
		var stable = new List<BuiltPiece>();
		var rest = _built.Where( b => b.IsValid() ).ToList();

		bool changed = true;
		while ( changed )
		{
			changed = false;
			foreach ( var b in rest.ToList() )
			{
				bool holds = Supported( b.Min, b.Size, stable, b ) || (b.Nailed && stable.Any( s => Touches( b.Min, b.Size, s.Min, s.Max ) ));
				if ( !holds ) continue;
				stable.Add( b );
				rest.Remove( b );
				changed = true;
			}
		}

		foreach ( var fallen in rest )
		{
			_built.Remove( fallen );
			Sfx.Play( Sfx.Wood, fallen.WorldPosition );
			fallen.GameObject.Destroy();
		}
		if ( rest.Count > 0 ) PlayerGrabber.Instance?.Notify( rest.Count == 1 ? "A piece fell off" : $"{rest.Count} pieces fell off" );
	}

	// ---- the pieces ------------------------------------------------------------------------------------------------------------------

	private Vector3 ScaleOf( BuildPieceDef def, Model model )
	{
		var size = model.Bounds.Size;
		float c = _grid.CellSize;
		switch ( def.Fit )
		{
			case "xy":
			{
				float sx = def.Cells.x * c / MathF.Max( 0.01f, size.x ), sy = def.Cells.y * c / MathF.Max( 0.01f, size.y );
				return new Vector3( sx, sy, (sx + sy) * 0.5f );
			}
			case "yz":
			{
				float sy = def.Cells.y * c / MathF.Max( 0.01f, size.y ), sz = def.Cells.z * c / MathF.Max( 0.01f, size.z );
				return new Vector3( sy, sy, sz );
			}
			case "y":
			{
				float sy = def.Cells.y * c / MathF.Max( 0.01f, size.y );
				return new Vector3( sy, sy, sy );
			}
			default: return Vector3.One * def.Scale;
		}
	}

	/// <summary>
	/// Where a piece's pivot goes (in the grid's frame). The models' pivots are not at their middles (some are metres away), so the middle of the
	/// scaled model is what is centred over the piece's cells, standing on the bottom of them, and the pivot goes wherever that puts it.
	/// </summary>
	private Vector3 PieceLocalPosition( BuildPieceDef def, Vector3Int min, Vector3Int size, Model model, int turn, Vector3 shift )
	{
		float cell = _grid.CellSize;
		var scale = model is null ? Vector3.One : ScaleOf( def, model );
		var centre = model is null ? Vector3.Zero : model.Bounds.Center * scale;
		float height = model is null ? 0f : model.Bounds.Size.z * scale.z;
		var middle = _grid.Origin + new Vector3( (min.x + size.x * 0.5f + shift.x) * cell, (min.y + size.y * 0.5f + shift.y) * cell, min.z * cell + height * 0.5f + def.Rest );
		return middle - Rotation.FromYaw( turn * 90f ) * centre;
	}

	private void ShowGhost( BuildPieceDef def, Vector3Int min, Vector3Int size, Verdict verdict )
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
		_ghost.WorldPosition = frame.PointToWorld( PieceLocalPosition( def, min, size, model, _turn, _shift ) );
		_ghost.WorldRotation = frame.Rotation * Rotation.FromYaw( _turn * 90f );
		_ghostRenderer.Tint = verdict switch
		{
			Verdict.Ok => new Color( 0.4f, 1f, 0.5f, 0.7f ),
			Verdict.NeedsNail => new Color( 1f, 0.75f, 0.25f, 0.7f ),
			_ => new Color( 1f, 0.35f, 0.3f, 0.7f ),
		};
	}

	private void Place( BuildPieceDef def, Vector3Int min, Vector3Int size, bool nailed )
	{
		var model = LongTrip.World.Props.ScatterLibrary.GetModel( def.Model );
		if ( model is null ) return;
		if ( nailed && !(PlayerInventory.Instance?.ConsumeNail() ?? false) ) { Sfx.Play( Sfx.Deny ); return; }

		var anchor = _grid.Anchor.IsValid() ? _grid.Anchor : GameObject;
		var go = Scene.CreateObject();
		go.Name = "Built " + def.Name;
		go.Flags |= GameObjectFlags.NotSaved;
		go.SetParent( anchor, false );
		go.LocalPosition = PieceLocalPosition( def, min, size, model, _turn, _shift );
		go.LocalRotation = Rotation.FromYaw( _turn * 90f );
		go.LocalScale = ScaleOf( def, model );

		go.Components.Create<ModelRenderer>().Model = model;
		LongTrip.World.Props.ColliderGenerator.Add( go, model );   // shaped from the piece's real geometry (door and window openings stay open)

		var piece = go.Components.Create<BuiltPiece>();
		piece.Def = def; piece.Min = min; piece.Size = size; piece.Nailed = nailed; piece.Shift = _shift; piece.Turn = _turn;
		_built.Add( piece );
		Sfx.Play( nailed ? Sfx.Metal : Sfx.Wood, go.WorldPosition );
	}

	private void Remove( BuiltPiece piece )
	{
		if ( !_built.Remove( piece ) ) return;
		Sfx.Play( Sfx.Metal, piece.WorldPosition );
		piece.GameObject.Destroy();
		Prune();
	}
}
