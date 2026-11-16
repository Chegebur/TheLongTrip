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

		Aim( cam, def, out var min, out var size, out var looking );
		var (verdict, why) = Evaluate( def, min, size );

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

	private Vector3Int FootprintSize( BuildPieceDef def ) => _turn % 2 == 1 ? new Vector3Int( def.Cells.y, def.Cells.x, def.Cells.z ) : def.Cells;

	/// <summary>
	/// Where the piece would go. The pointer is where its footprint is centred; when you look at the side of something built, the piece is put against that
	/// face (touching, not overlapping); looking at the top of something puts it on top.
	/// </summary>
	private void Aim( CameraComponent cam, BuildPieceDef def, out Vector3Int min, out Vector3Int size, out BuiltPiece looking )
	{
		looking = null;
		var player = Scene.GetAllObjects( true ).FirstOrDefault( g => g.Components.Get<PlayerController>( true ) is not null );
		var tr = Scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + cam.WorldRotation.Forward * 800f )
			.IgnoreGameObjectHierarchy( player ?? GameObject ).Run();

		if ( tr.Hit && tr.GameObject.IsValid() ) looking = tr.GameObject.Components.Get<BuiltPiece>( FindMode.EverythingInSelfAndParent );

		size = FootprintSize( def );
		var frame = _grid.Frame;
		float cell = _grid.CellSize;

		var point = tr.Hit ? tr.HitPosition : cam.WorldPosition + cam.WorldRotation.Forward * 300f;
		var lp = (frame.PointToLocal( point ) - _grid.Origin) / cell;                 // in cells, fractional
		var normal = tr.Hit ? frame.NormalToLocal( tr.Normal ) : Vector3.Up;

		float roofZ = BuildLibrary.RoofLevel;
		float zf = MathF.Floor( lp.z + 0.02f );

		// centred on the pointer
		int mx = (int)MathF.Round( lp.x - size.x * 0.5f );
		int my = (int)MathF.Round( lp.y - size.y * 0.5f );
		int mz = (int)zf;

		if ( tr.Hit )
		{
			bool top = normal.z > 0.6f;
			if ( !top )
			{
				// the side of something: the piece goes against that face
				if ( MathF.Abs( normal.x ) >= MathF.Abs( normal.y ) ) mx = normal.x > 0f ? (int)MathF.Round( lp.x ) : (int)MathF.Round( lp.x ) - size.x;
				else my = normal.y > 0f ? (int)MathF.Round( lp.y ) : (int)MathF.Round( lp.y ) - size.y;
				mz = (int)MathF.Floor( lp.z + 0.02f );
			}
			else
			{
				// the top of something: on it (the point is on the surface, so the layer is the one above it)
				mz = (int)MathF.Round( lp.z );
			}
		}

		min = new Vector3Int( mx, my, Math.Max( mz, (int)roofZ ) );
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

	/// <summary>Where a piece sits (in the grid's frame): centred over its cells, standing on the bottom of them.</summary>
	private Vector3 PieceLocalPosition( BuildPieceDef def, Vector3Int min, Vector3Int size, Model model )
	{
		float cell = _grid.CellSize;
		float height = model is null ? 0f : model.Bounds.Size.z * ScaleOf( def, model ).z;
		return _grid.Origin + new Vector3( (min.x + size.x * 0.5f) * cell, (min.y + size.y * 0.5f) * cell, min.z * cell + height * 0.5f + def.Rest );
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
		_ghost.WorldPosition = frame.PointToWorld( PieceLocalPosition( def, min, size, model ) );
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
		go.LocalPosition = PieceLocalPosition( def, min, size, model );
		go.LocalRotation = Rotation.FromYaw( _turn * 90f );
		go.LocalScale = ScaleOf( def, model );

		go.Components.Create<ModelRenderer>().Model = model;
		var box = go.Components.Create<BoxCollider>();
		box.Center = model.Bounds.Center;
		box.Scale = model.Bounds.Size;

		var piece = go.Components.Create<BuiltPiece>();
		piece.Def = def; piece.Min = min; piece.Size = size; piece.Nailed = nailed;
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
