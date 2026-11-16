namespace LongTrip.Gameplay;

/// <summary>One kind of building piece, from <c>Assets/data/buildings.json</c>.</summary>
public sealed class BuildPieceDef
{
	public string Id, Name, Model, Layer = "wall";
	public float Scale = 1f;
	public Vector3Int Cells = new( 1, 1, 1 );
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
	}
	private sealed class BuildFile { public int roofLevel { get; set; } = 4; public List<PieceRecord> pieces { get; set; } = new(); }

	private static List<BuildPieceDef> _all;
	private static int _roof = 4;
	public static void Reset() => _all = null;

	public static int RoofLevel { get { Load(); return _roof; } }
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
			foreach ( var p in file.pieces )
			{
				if ( string.IsNullOrEmpty( p.model ) ) continue;
				var c = p.cells is { Length: 3 } ? new Vector3Int( Math.Max( 1, p.cells[0] ), Math.Max( 1, p.cells[1] ), Math.Max( 1, p.cells[2] ) ) : new Vector3Int( 1, 1, 1 );
				_all.Add( new BuildPieceDef { Id = p.id ?? p.model, Name = p.name ?? p.id, Model = p.model, Layer = (p.layer ?? "wall").ToLowerInvariant(), Scale = p.scale <= 0f ? 1f : p.scale, Cells = c } );
			}
		}
		catch ( System.Exception e ) { Log.Warning( $"[build] {DataPath} could not be read ({e.Message})" ); }
	}
}

/// <summary>A piece that has been built: remembers what it is and which cells it holds.</summary>
public sealed class BuiltPiece : Component
{
	public BuildPieceDef Def { get; set; }
	public Vector3Int Min { get; set; }
	public Vector3Int Size { get; set; }
}

/// <summary>
/// Building on the car. Press B near the car to start: the local grid appears, the mouse wheel picks a piece (wooden floors and walls),
/// R turns it, the left mouse places it where the green ghost is, the right mouse takes the piece you are looking at away again, B or Esc
/// stops. Pieces are fixed to the car's frame, so they ride along when it drives, you can walk on the floors (the car carries you), and what is
/// built stays between drives. The first floor goes on the roof; walls need a floor or roof under them, and anything else needs a piece beneath.
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

	protected override void OnStart()
	{
		BuildLibrary.Reset();
		_grid = Components.Get<BuildGrid>();
		if ( _grid.IsValid() )
		{
			// the grid covers the car; its cell layers are counted up from the roof so the first free layer above the roof is "roof level"
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

		Aim( cam, def, out var min, out var size, out bool valid, out var looking );

		ShowGhost( def, min, size, valid );

		Hint = valid ? $"{def.Name}   Left mouse: place" : $"{def.Name}   (cannot go here)";
		Hint += "     Wheel: piece     R: turn     Right mouse: remove     B: stop";

		if ( Input.Pressed( "Attack1" ) )
		{
			if ( valid ) Place( def, min, size );
			else Sfx.Play( Sfx.Deny );
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

	// ---- aiming -----------------------------------------------------------------------------------------------------------

	private (Vector3Int Min, Vector3Int Size) Footprint( BuildPieceDef def, Vector3Int aimed )
	{
		bool swapped = _turn % 2 == 1;
		var size = swapped ? new Vector3Int( def.Cells.y, def.Cells.x, def.Cells.z ) : def.Cells;
		return (new Vector3Int( aimed.x - (size.x - 1) / 2, aimed.y - (size.y - 1) / 2, aimed.z ), size);
	}

	private void Aim( CameraComponent cam, BuildPieceDef def, out Vector3Int min, out Vector3Int size, out bool valid, out BuiltPiece looking )
	{
		looking = null;
		var player = Scene.GetAllObjects( true ).FirstOrDefault( g => g.Components.Get<PlayerController>( true ) is not null );
		var tr = Scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + cam.WorldRotation.Forward * 800f )
			.IgnoreGameObjectHierarchy( player ?? GameObject ).Run();

		if ( tr.Hit && tr.GameObject.IsValid() ) looking = tr.GameObject.Components.Get<BuiltPiece>( FindMode.EverythingInSelfAndParent );

		var point = tr.Hit ? tr.HitPosition + tr.Normal * 3f : cam.WorldPosition + cam.WorldRotation.Forward * 300f;
		var aimed = _grid.WorldToCell( point );
		if ( !tr.Hit || Math.Abs( tr.Normal.z ) < 0.5f ) aimed.z = Math.Max( aimed.z, BuildLibrary.RoofLevel );   // a wall you look at: build beside it at roof level or above

		(min, size) = Footprint( def, aimed );
		valid = CanPlace( def, min, size );
	}

	private bool CanPlace( BuildPieceDef def, Vector3Int min, Vector3Int size )
	{
		int roof = BuildLibrary.RoofLevel;
		if ( min.z < roof ) return false;

		for ( int x = 0; x < size.x; x++ )
		for ( int y = 0; y < size.y; y++ )
		for ( int z = 0; z < size.z; z++ )
		{
			var c = new Vector3Int( min.x + x, min.y + y, min.z + z );
			if ( !_grid.InRange( c ) ) return false;
			if ( _built.Any( b => b.IsValid() && b.Def.Layer == def.Layer && Overlaps( b, c ) ) ) return false;
		}

		// it has to stand on something: the roof, or a piece under it
		if ( min.z == roof ) return true;
		for ( int x = 0; x < size.x; x++ )
		for ( int y = 0; y < size.y; y++ )
		{
			var below = new Vector3Int( min.x + x, min.y + y, min.z - 1 );
			if ( _built.Any( b => b.IsValid() && b.Def.Layer == "wall" && Overlaps( b, below ) ) ) return true;
		}
		return false;
	}

	private static bool Overlaps( BuiltPiece b, Vector3Int c )
		=> c.x >= b.Min.x && c.x < b.Min.x + b.Size.x && c.y >= b.Min.y && c.y < b.Min.y + b.Size.y && c.z >= b.Min.z && c.z < b.Min.z + b.Size.z;

	// ---- placing and removing ----------------------------------------------------------------------------------------------

	/// <summary>Where a piece sits (in the grid's frame): centred over its cells, resting on the bottom of them.</summary>
	private Vector3 PieceLocalPosition( BuildPieceDef def, Vector3Int min, Vector3Int size, Model model )
	{
		float cell = _grid.CellSize;
		float height = model is null ? 0f : model.Bounds.Size.z * def.Scale;
		return _grid.Origin + new Vector3( (min.x + size.x * 0.5f) * cell, (min.y + size.y * 0.5f) * cell, min.z * cell + height * 0.5f );
	}

	private void ShowGhost( BuildPieceDef def, Vector3Int min, Vector3Int size, bool valid )
	{
		var model = ScatterModel( def );
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
		_ghost.WorldScale = def.Scale;
		_ghost.WorldPosition = frame.PointToWorld( PieceLocalPosition( def, min, size, model ) );
		_ghost.WorldRotation = frame.Rotation * Rotation.FromYaw( _turn * 90f );
		_ghostRenderer.Tint = valid ? new Color( 0.4f, 1f, 0.5f, 0.7f ) : new Color( 1f, 0.35f, 0.3f, 0.7f );
	}

	private static Model ScatterModel( BuildPieceDef def ) => LongTrip.World.Props.ScatterLibrary.GetModel( def.Model );

	private void Place( BuildPieceDef def, Vector3Int min, Vector3Int size )
	{
		var model = ScatterModel( def );
		if ( model is null ) return;

		var anchor = _grid.Anchor.IsValid() ? _grid.Anchor : GameObject;
		var go = Scene.CreateObject();
		go.Name = "Built " + def.Name;
		go.Flags |= GameObjectFlags.NotSaved;
		go.SetParent( anchor, false );
		go.LocalPosition = PieceLocalPosition( def, min, size, model );
		go.LocalRotation = Rotation.FromYaw( _turn * 90f );
		go.LocalScale = def.Scale;

		go.Components.Create<ModelRenderer>().Model = model;
		var box = go.Components.Create<BoxCollider>();
		box.Center = model.Bounds.Center;
		box.Scale = model.Bounds.Size;

		var piece = go.Components.Create<BuiltPiece>();
		piece.Def = def; piece.Min = min; piece.Size = size;
		_built.Add( piece );
		Sfx.Play( Sfx.Wood, go.WorldPosition );
	}

	private void Remove( BuiltPiece piece )
	{
		if ( !_built.Remove( piece ) ) return;
		Sfx.Play( Sfx.Metal, piece.WorldPosition );
		piece.GameObject.Destroy();
	}
}
