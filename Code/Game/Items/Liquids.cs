namespace LongTrip.Gameplay;

/// <summary>One kind of liquid, from <c>Assets/data/liquids.json</c>: what it looks like, how heavy it is, and what drinking it does.</summary>
public sealed class LiquidDef
{
	public string Id, Name;

	/// <summary>Colour (0-255 each), used for the pouring stream.</summary>
	public Color Color = Color.White;

	/// <summary>Kilograms per litre.</summary>
	public float Density = 1f;

	/// <summary>What a litre drunk gives: water and food (points on the 0-100 bars) and what it costs in health (poison).</summary>
	public float Thirst, Food, Toxic;

	/// <summary>Whether it can be poured into a car's tank.</summary>
	public bool Fuel;
}

public static class LiquidLibrary
{
	public const string DataPath = "data/liquids.json";

	private sealed class LiquidRecord
	{
		public string id { get; set; }
		public string name { get; set; }
		public float[] color { get; set; }
		public float density { get; set; } = 1f;
		public float thirst { get; set; }
		public float food { get; set; }
		public float toxic { get; set; }
		public bool fuel { get; set; }
	}
	private sealed class LiquidFile { public List<LiquidRecord> liquids { get; set; } = new(); }

	private static Dictionary<string, LiquidDef> _all;
	public static void Reset() => _all = null;

	private static Dictionary<string, LiquidDef> All
	{
		get
		{
			if ( _all is not null ) return _all;
			_all = new Dictionary<string, LiquidDef>();
			try
			{
				if ( FileSystem.Mounted.FileExists( DataPath ) )
				{
					var file = Json.Deserialize<LiquidFile>( FileSystem.Mounted.ReadAllText( DataPath ) );
					foreach ( var r in file?.liquids ?? new() )
					{
						if ( string.IsNullOrEmpty( r.id ) ) continue;
						var c = r.color is { Length: >= 3 } ? new Color( r.color[0] / 255f, r.color[1] / 255f, r.color[2] / 255f ) : Color.White;
						_all[r.id] = new LiquidDef { Id = r.id, Name = r.name ?? r.id, Color = c, Density = r.density, Thirst = r.thirst, Food = r.food, Toxic = r.toxic, Fuel = r.fuel };
					}
				}
			}
			catch ( System.Exception e ) { Log.Warning( $"[liquids] could not read {DataPath} ({e.Message})" ); }

			if ( _all.Count == 0 ) _all["water"] = new LiquidDef { Id = "water", Name = "Water", Color = new Color( 0.3f, 0.6f, 0.9f ), Thirst = 30f };
			return _all;
		}
	}

	public static LiquidDef Get( string id ) => id is not null && All.TryGetValue( id, out var d ) ? d : null;

	public static string NameOf( string id ) => Get( id )?.Name ?? id ?? "nothing";
}

/// <summary>The rules for moving liquid about.</summary>
public static class Liquids
{
	/// <summary>Why a pour did nothing (null when it worked).</summary>
	public static string LastProblem { get; private set; }

	/// <summary>Pour up to <paramref name="maxLitres"/> from one container into another. The target takes the same liquid, or anything when it is empty. Returns litres moved.</summary>
	public static float Transfer( Item from, Item to, float maxLitres )
	{
		LastProblem = null;
		if ( !from.IsValid() || !to.IsValid() || from == to ) { LastProblem = "Nothing to pour into"; return 0f; }
		var fromCap = from.Definition?.Container;
		var toCap = to.Definition?.Container;
		if ( fromCap is null || toCap is null ) { LastProblem = "That is not a container"; return 0f; }
		if ( from.Litres < 0.0005f || from.Liquid is null ) { LastProblem = "Nothing is left to pour"; return 0f; }
		if ( to.Litres >= 0.0005f && to.Liquid != from.Liquid ) { LastProblem = $"That already holds {LiquidLibrary.NameOf( to.Liquid ).ToLowerInvariant()}: they would mix"; return 0f; }

		float space = (1f - to.Amount) * toCap.Capacity;
		float move = MathF.Min( MathF.Min( maxLitres, from.Litres ), space );
		if ( move <= 0.0005f ) { LastProblem = "It is full"; return 0f; }

		to.Liquid = from.Liquid;
		to.Amount = Math.Clamp( to.Amount + move / toCap.Capacity, 0f, 1f );
		from.Amount = MathF.Max( 0f, from.Amount - move / fromCap.Capacity );
		if ( from.Litres < 0.0005f ) { from.Amount = 0f; from.Liquid = null; }
		to.RefreshMass();
		from.RefreshMass();
		return move;
	}

	/// <summary>Pour fuel into a car's tank. Returns litres moved.</summary>
	public static float FillTank( Item from, CarAssembly car, float maxLitres )
	{
		LastProblem = null;
		var cap = from.Definition?.Container;
		if ( cap is null || from.Litres < 0.0005f || from.Liquid is null ) { LastProblem = "Nothing is left to pour"; return 0f; }
		if ( !(LiquidLibrary.Get( from.Liquid )?.Fuel ?? false) ) { LastProblem = $"The tank only takes fuel, not {LiquidLibrary.NameOf( from.Liquid ).ToLowerInvariant()}"; return 0f; }

		float accepted = car.AddFuel( MathF.Min( maxLitres, from.Litres ) );
		if ( accepted <= 0.0005f ) { LastProblem = "The tank is full"; return 0f; }

		from.Amount = MathF.Max( 0f, from.Amount - accepted / cap.Capacity );
		if ( from.Litres < 0.0005f ) { from.Amount = 0f; from.Liquid = null; }
		from.RefreshMass();
		return accepted;
	}
}

/// <summary>The thin stream of liquid you see while pouring: one stretched cylinder from the container to what it is pouring into.</summary>
public sealed class PourFx
{
	private static Model _model;
	private GameObject _go;
	private ModelRenderer _renderer;

	private static Model StreamModel()
	{
		if ( _model.IsValid() ) return _model;
		var b = new LongTrip.World.Props.PropMeshBuilder();
		b.Cylinder( "materials/world/interior/flat_lit.vmat", new Vector3( 0f, 0f, 0.5f ), 1f, 1f, 8, null, LongTrip.World.Props.PropMeshBuilder.Flat( 255, 255, 255, 0.15f ), true );
		return _model = b.Build( "pour_stream" );
	}

	public void Show( Scene scene, Vector3 from, Vector3 to, Color color )
	{
		if ( !_go.IsValid() )
		{
			_go = scene.CreateObject();
			_go.Name = "Pour stream";
			_go.Flags |= GameObjectFlags.NotSaved;
			_renderer = _go.Components.Create<ModelRenderer>();
			_renderer.Model = StreamModel();
			_renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}

		var along = to - from;
		float length = MathF.Max( 1f, along.Length );
		_go.Enabled = true;
		_go.WorldPosition = from;
		_go.WorldRotation = Rotation.FromToRotation( Vector3.Up, along / length );
		_go.WorldScale = new Vector3( 1.4f, 1.4f, length );
		_renderer.Tint = color;
	}

	public void Hide() { if ( _go.IsValid() ) _go.Enabled = false; }

	public void Destroy() { if ( _go.IsValid() ) _go.Destroy(); _go = null; }
}
