namespace LongTrip.Gameplay;

/// <summary>
/// A local building grid: a box of cells that belongs to an object and moves, turns and tilts with it (the car carries one). Cells are
/// addressed in the owner's own frame, so whatever gets built on it stays put on the car however fast it drives. This is the framework only:
/// no buildings yet. It can snap a world point to a cell, find the cell you are aiming at, remember which cells are taken, and show itself
/// (press B near the car).
/// </summary>
public sealed class BuildGrid : Component
{
	/// <summary>Edge of a cell in world units (20 = half a metre).</summary>
	[Property, Group( "Grid" )] public float CellSize { get; set; } = 20f;

	/// <summary>How many cells it has along the owner's forward, left and up axes.</summary>
	[Property, Group( "Grid" )] public Vector3Int Cells { get; set; } = new Vector3Int( 15, 6, 6 );

	/// <summary>Where the grid's minimum corner is, in the owner's frame (world units).</summary>
	[Property, Group( "Grid" )] public Vector3 Origin { get; set; } = new Vector3( -147f, -118f, 0f );

	/// <summary>The object whose frame the grid lives in (the car body). Empty: this object.</summary>
	[Property, Group( "Grid" )] public GameObject Anchor { get; set; }

	/// <summary>Press this key to show or hide the grid.</summary>
	[Property, Group( "Grid" )] public string ToggleKey { get; set; } = "b";

	[Property, Group( "Grid" )] public bool Visible { get; set; }

	private readonly Dictionary<Vector3Int, string> _taken = new();

	/// <summary>The grids that exist right now.</summary>
	public static readonly List<BuildGrid> All = new();

	protected override void OnEnabled() { if ( !All.Contains( this ) ) All.Add( this ); }
	protected override void OnDisabled() => All.Remove( this );

	public Transform Frame => (Anchor.IsValid() ? Anchor : GameObject).WorldTransform.WithScale( 1f );

	// ---- cells ---------------------------------------------------------------------------------------------------------

	public bool InRange( Vector3Int c ) => c.x >= 0 && c.y >= 0 && c.z >= 0 && c.x < Cells.x && c.y < Cells.y && c.z < Cells.z;

	/// <summary>The centre of a cell, in the world.</summary>
	public Vector3 CellToWorld( Vector3Int c ) => Frame.PointToWorld( Origin + new Vector3( c.x + 0.5f, c.y + 0.5f, c.z + 0.5f ) * CellSize );

	/// <summary>The cell a world point is in (it may be outside the grid: check <see cref="InRange"/>).</summary>
	public Vector3Int WorldToCell( Vector3 world )
	{
		var local = (Frame.PointToLocal( world ) - Origin) / CellSize;
		return new Vector3Int( (int)MathF.Floor( local.x ), (int)MathF.Floor( local.y ), (int)MathF.Floor( local.z ) );
	}

	/// <summary>The world position and rotation a thing built in this cell has: its centre, turned with the grid.</summary>
	public Transform CellTransform( Vector3Int c ) => new Transform( CellToWorld( c ), Frame.Rotation );

	public bool IsFree( Vector3Int c ) => InRange( c ) && !_taken.ContainsKey( c );
	public string Occupant( Vector3Int c ) => _taken.TryGetValue( c, out var id ) ? id : null;

	public bool TryOccupy( Vector3Int c, string what )
	{
		if ( !IsFree( c ) ) return false;
		_taken[c] = what;
		return true;
	}

	public bool Release( Vector3Int c ) => _taken.Remove( c );

	public IReadOnlyDictionary<Vector3Int, string> Taken => _taken;

	/// <summary>Walk along a ray and return the first free cell that touches something (an occupied cell or the grid's floor): where a block would go.</summary>
	public bool TryAim( Vector3 origin, Vector3 direction, float maxDistance, out Vector3Int cell )
	{
		cell = default;
		var previous = new Vector3Int( int.MinValue, 0, 0 );
		for ( float d = 0f; d <= maxDistance; d += CellSize * 0.25f )
		{
			var c = WorldToCell( origin + direction * d );
			if ( c == previous ) continue;
			if ( InRange( c ) && !IsFree( c ) )
			{
				// stopped by a built cell: the free cell in front of it
				if ( InRange( previous ) && IsFree( previous ) ) { cell = previous; return true; }
				return false;
			}
			if ( InRange( c ) && c.z == 0 ) { cell = c; return true; }   // the floor of the grid
			previous = c;
		}
		return false;
	}

	/// <summary>The grid whose box contains a world point, or null.</summary>
	public static BuildGrid At( Vector3 world )
	{
		foreach ( var g in All )
			if ( g.IsValid() && g.InRange( g.WorldToCell( world ) ) ) return g;
		return null;
	}

	// ---- showing it ----------------------------------------------------------------------------------------------------------

	protected override void OnUpdate()
	{
		if ( !string.IsNullOrEmpty( ToggleKey ) && Input.Keyboard.Pressed( ToggleKey ) ) Visible = !Visible;
		if ( !Visible ) return;

		var cam = Scene.Camera;
		if ( !cam.IsValid() ) return;

		// only draw the cells near the camera: the grid is big, and lines are not free
		var near = WorldToCell( cam.WorldPosition );
		const int R = 5;
		var frame = Frame;
		var color = new Color( 0.35f, 0.85f, 1f, 0.55f );
		var overlay = Scene.DebugOverlay;

		for ( int z = 0; z <= Cells.z; z++ )
		{
			if ( MathF.Abs( z - near.z ) > R ) continue;
			for ( int x = 0; x <= Cells.x; x++ )
				overlay.Line( Origin + new Vector3( x, 0, z ) * CellSize, Origin + new Vector3( x, Cells.y, z ) * CellSize, color, 0f, frame );
			for ( int y = 0; y <= Cells.y; y++ )
				overlay.Line( Origin + new Vector3( 0, y, z ) * CellSize, Origin + new Vector3( Cells.x, y, z ) * CellSize, color, 0f, frame );
		}

		foreach ( var (c, _) in _taken )
			overlay.Box( new BBox( Origin + new Vector3( c.x, c.y, c.z ) * CellSize, Origin + new Vector3( c.x + 1, c.y + 1, c.z + 1 ) * CellSize ), new Color( 1f, 0.6f, 0.2f, 0.8f ), 0f, frame );

		// the cell you are aiming at
		if ( TryAim( cam.WorldPosition, cam.WorldRotation.Forward, 600f, out var aim ) )
			overlay.Box( new BBox( Origin + new Vector3( aim.x, aim.y, aim.z ) * CellSize, Origin + new Vector3( aim.x + 1, aim.y + 1, aim.z + 1 ) * CellSize ), new Color( 0.4f, 1f, 0.4f, 0.9f ), 0f, frame );
	}
}
