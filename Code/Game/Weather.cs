using LongTrip.World;
using LongTrip.World.Props;

namespace LongTrip.Gameplay;

public enum WeatherKind
{
	Clear, Overcast, Fog, Rain, Storm, Dust, Snow, Blizzard,
	// the dense variants (added at the end so saved values keep their meaning)
	HeavyRain, Sandstorm, HeavySnow, DenseFog, HeavyOvercast,
}

/// <summary>
/// The weather. One host decides (a random walk between kinds that suit the biome you are in, a few minutes each) and the kind and its
/// strength are synced, so everyone sees the same sky; the look of it (fog, light, rain and snow streaks, lightning) is then built locally from
/// those two numbers. Offline the local player is the host. <see cref="Atmosphere"/> reads <see cref="Fog"/>, <see cref="Light"/> and the
/// other modifiers below.
/// </summary>
public sealed class Weather : Component
{
	public static Weather Instance { get; private set; }

	/// <summary>Average seconds one kind of weather lasts.</summary>
	[Property, Group( "Weather" )] public float MinSeconds { get; set; } = 90f;
	[Property, Group( "Weather" )] public float MaxSeconds { get; set; } = 240f;

	/// <summary>Switch the weather off (always clear).</summary>
	[Property, Group( "Weather" )] public bool WeatherOn { get; set; } = true;

	/// <summary>Set to force a kind for testing (only if "Force" is on).</summary>
	[Property, Group( "Weather" )] public bool Force { get; set; }
	[Property, Group( "Weather" )] public WeatherKind Forced { get; set; } = WeatherKind.Rain;

	[Sync] public WeatherKind Kind { get; set; } = WeatherKind.Clear;
	[Sync] public float Strength { get; set; } = 1f;

	// smoothed values the rest of the game reads
	public float Fog { get; private set; } = 1f;        // multiplier on fog distance (smaller = thicker)
	public float Light { get; private set; } = 1f;      // multiplier on sunlight
	public float Dim { get; private set; } = 1f;        // multiplier on ambient light
	public float Desaturate { get; private set; }       // 0..1
	public Color Tint { get; private set; } = Color.White;
	public float Flash { get; private set; }            // lightning, 0..1
	public float Wetness { get; private set; }          // 0..1, rain and snow on the ground (for future grip changes)
	public float Wind { get; private set; }

	private struct Target { public float Fog, Light, Dim, Desat, Rain, Snow, Dust, Dense; public Color Tint; }

	/// <summary>The name shown for a kind ("Heavy rain", "Snow storm"...).</summary>
	public static string NameOf( WeatherKind k ) => k switch
	{
		WeatherKind.HeavyRain => "Heavy rain", WeatherKind.Storm => "Thunderstorm", WeatherKind.Dust => "Dust", WeatherKind.Sandstorm => "Sandstorm",
		WeatherKind.HeavySnow => "Heavy snow", WeatherKind.Blizzard => "Snow storm", WeatherKind.DenseFog => "Dense fog", WeatherKind.HeavyOvercast => "Heavy cloud",
		_ => k.ToString(),
	};

	private static Target TargetFor( WeatherKind k, float s ) => k switch
	{
		WeatherKind.Overcast => new Target { Fog = 0.8f, Light = 0.55f, Dim = 0.85f, Desat = 0.25f * s, Tint = new Color( 0.85f, 0.9f, 1f ) },
		WeatherKind.Fog => new Target { Fog = MathX.Lerp( 0.4f, 0.05f, s ), Light = 0.7f, Dim = 0.95f, Desat = 0.35f * s, Tint = new Color( 0.9f, 0.93f, 0.98f ) },
		WeatherKind.Rain => new Target { Fog = MathX.Lerp( 0.6f, 0.22f, s ), Light = 0.4f, Dim = 0.75f, Desat = 0.35f, Rain = s, Tint = new Color( 0.75f, 0.82f, 0.95f ) },
		WeatherKind.Storm => new Target { Fog = 0.16f, Light = 0.18f, Dim = 0.55f, Desat = 0.5f, Rain = 1f, Tint = new Color( 0.62f, 0.7f, 0.85f ) },
		WeatherKind.Dust => new Target { Fog = MathX.Lerp( 0.45f, 0.05f, s ), Light = 0.65f, Dim = 0.95f, Desat = 0.1f, Dust = s, Tint = new Color( 1f, 0.82f, 0.55f ) },
		WeatherKind.Snow => new Target { Fog = MathX.Lerp( 0.6f, 0.25f, s ), Light = 0.6f, Dim = 0.95f, Desat = 0.15f, Snow = s, Tint = new Color( 0.92f, 0.96f, 1f ) },
		WeatherKind.Blizzard => new Target { Fog = 0.045f, Light = 0.45f, Dim = 0.9f, Desat = 0.3f, Snow = 1f, Tint = new Color( 0.88f, 0.93f, 1f ) },
		WeatherKind.HeavyOvercast => new Target { Fog = 0.55f, Light = 0.3f, Dim = 0.7f, Desat = 0.4f, Tint = new Color( 0.75f, 0.8f, 0.9f ) },
		WeatherKind.DenseFog => new Target { Fog = 0.025f, Light = 0.55f, Dim = 0.95f, Desat = 0.45f, Tint = new Color( 0.88f, 0.9f, 0.95f ) },
		WeatherKind.HeavyRain => new Target { Fog = 0.1f, Light = 0.25f, Dim = 0.65f, Desat = 0.45f, Rain = 1f, Dense = 1.6f, Tint = new Color( 0.68f, 0.76f, 0.92f ) },
		WeatherKind.Sandstorm => new Target { Fog = 0.022f, Light = 0.5f, Dim = 0.9f, Desat = 0.1f, Dust = 1f, Dense = 1.6f, Tint = new Color( 1f, 0.72f, 0.4f ) },
		WeatherKind.HeavySnow => new Target { Fog = 0.1f, Light = 0.5f, Dim = 0.95f, Desat = 0.2f, Snow = 1f, Dense = 1.6f, Tint = new Color( 0.9f, 0.95f, 1f ) },
		_ => new Target { Fog = 1f, Light = 1f, Dim = 1f, Tint = Color.White },
	};

	private float _rain, _snow, _dust, _dense = 1f;
	private TimeUntil _next = 45f;   // the first minute is clear
	private TimeUntil _nextFlash = 5f;
	private readonly System.Random _random = new();

	private WorldStreamer _world;
	private CarSession _car;

	protected override void OnStart()
	{
		Instance = this;
		_world = Scene.GetAllComponents<WorldStreamer>().FirstOrDefault();
		_car = Scene.GetAllComponents<CarSession>().FirstOrDefault();
	}

	protected override void OnEnabled() => Instance = this;
	protected override void OnDestroy() { if ( Instance == this ) Instance = null; _streaks.Clear(); }

	private bool IsHost => !Networking.IsActive || Networking.IsHost;

	private Vector3 Focus => _car.IsValid() && _car.IsDriving && _car.Car.IsValid() ? _car.Car.WorldPosition : (Scene.Camera.IsValid() ? Scene.Camera.WorldPosition : Vector3.Zero);

	protected override void OnUpdate()
	{
		if ( _world is null || !_world.IsReady || !Scene.Camera.IsValid() ) return;

		// ---- the host picks the weather
		if ( IsHost )
		{
			if ( !WeatherOn ) { Kind = WeatherKind.Clear; Strength = 1f; }
			else if ( Force ) { Kind = Forced; Strength = 1f; }
			else if ( _next )
			{
				Choose();
				_next = MathX.Lerp( MinSeconds, MaxSeconds, (float)_random.NextDouble() );
			}
		}

		// ---- ease towards the target
		var t = TargetFor( Kind, Strength );
		float k = 1f - MathF.Exp( -0.5f * Time.Delta );
		Fog = MathX.Lerp( Fog, t.Fog, k );
		Light = MathX.Lerp( Light, t.Light, k );
		Dim = MathX.Lerp( Dim, t.Dim, k );
		Desaturate = MathX.Lerp( Desaturate, t.Desat, k );
		Tint = Color.Lerp( Tint, t.Tint, k );
		_rain = MathX.Lerp( _rain, t.Rain, k );
		_snow = MathX.Lerp( _snow, t.Snow, k );
		_dust = MathX.Lerp( _dust, t.Dust, k );
		_dense = MathX.Lerp( _dense, t.Dense > 0f ? t.Dense : 1f, k );
		Wetness = MathX.Lerp( Wetness, MathF.Max( _rain, 0f ), 0.1f * Time.Delta * (_rain > Wetness ? 1f : 0.2f) );
		Wind = MathX.Lerp( Wind, Kind is WeatherKind.Storm or WeatherKind.Blizzard or WeatherKind.Dust or WeatherKind.Sandstorm or WeatherKind.HeavyRain or WeatherKind.HeavySnow ? 1f : 0.2f, k );

		// ---- lightning in a storm: a quick double flash
		if ( Kind == WeatherKind.Storm && _nextFlash )
		{
			Flash = 1f;
			_nextFlash = 4f + (float)_random.NextDouble() * 12f;
		}
		Flash = MathX.Approach( Flash, 0f, Time.Delta * (Flash > 0.5f ? 4f : 2.2f) );

		UpdateStreaks();
	}

	/// <summary>Pick the next weather from what suits the biome here.</summary>
	private void Choose()
	{
		var focus = Focus;
		var gen = _world.Generator;
		var biome = gen.Biomes[gen.DominantBiome( focus.x, focus.y )];

		(WeatherKind, float)[] options;
		if ( biome == BiomeLibrary.Desert )
			options = new[] { (WeatherKind.Clear, 6f), (WeatherKind.Dust, 2.5f), (WeatherKind.Overcast, 1.5f), (WeatherKind.Fog, 0.4f), (WeatherKind.Storm, 0.4f), (WeatherKind.Sandstorm, 1.2f), (WeatherKind.HeavyOvercast, 0.5f) };
		else if ( biome == BiomeLibrary.Forest )
			options = new[] { (WeatherKind.Clear, 3.5f), (WeatherKind.Overcast, 2.5f), (WeatherKind.Rain, 3.5f), (WeatherKind.Storm, 1f), (WeatherKind.Fog, 2.2f), (WeatherKind.HeavyRain, 1.8f), (WeatherKind.HeavyOvercast, 1f), (WeatherKind.DenseFog, 1f) };
		else if ( biome == BiomeLibrary.IcePlains )
			options = new[] { (WeatherKind.Clear, 3f), (WeatherKind.Snow, 4f), (WeatherKind.Blizzard, 1.5f), (WeatherKind.Overcast, 2f), (WeatherKind.Fog, 1f), (WeatherKind.HeavySnow, 1.5f), (WeatherKind.DenseFog, 0.5f) };
		else
			options = new[] { (WeatherKind.Clear, 4f), (WeatherKind.Overcast, 2.5f), (WeatherKind.Rain, 3f), (WeatherKind.Storm, 1f), (WeatherKind.Fog, 1.5f), (WeatherKind.HeavyRain, 1.6f), (WeatherKind.HeavyOvercast, 1f), (WeatherKind.DenseFog, 0.5f) };

		float total = options.Sum( o => o.Item2 );
		float r = (float)_random.NextDouble() * total;
		foreach ( var (kind, w) in options )
		{
			r -= w;
			if ( r > 0f ) continue;
			Kind = kind;
			Strength = 0.5f + (float)_random.NextDouble() * 0.5f;
			Log.Info( $"[weather] {NameOf( Kind )} ({Strength:F2})" );
			return;
		}
	}

	// ---- rain, snow and dust drawn as thin streaks around the camera --------------------------------------------------------

	private const int MaxStreaks = 800;
	private const float Radius = 1100f, Height = 900f;

	private sealed class Streak { public GameObject Go; public ModelRenderer Renderer; public Vector3 Offset; public float Speed; public float Drift; }
	private readonly List<Streak> _streaks = new();
	private GameObject _streakRoot;
	private Model _rainModel, _flakeModel, _dustModel;

	private void EnsureModels()
	{
		if ( _rainModel.IsValid() ) return;
		const string flat = "materials/world/interior/flat_lit.vmat";
		var b = new PropMeshBuilder();
		b.Box( flat, new Vector3( 0, 0, 0 ), new Vector3( 0.55f, 0.55f, 46f ), 0f, true, 1f, PropMeshBuilder.Flat( 190, 205, 225, 0.2f ) );
		_rainModel = b.Build( "weather_rain" );
		b = new PropMeshBuilder();
		b.Box( flat, Vector3.Zero, new Vector3( 2.4f, 2.4f, 2.4f ), 0f, true, 1f, PropMeshBuilder.Flat( 245, 248, 255, 0.9f ) );
		_flakeModel = b.Build( "weather_flake" );
		b = new PropMeshBuilder();
		b.Box( flat, Vector3.Zero, new Vector3( 4f, 4f, 2f ), 0f, true, 1f, PropMeshBuilder.Flat( 214, 176, 120, 0.9f ) );
		_dustModel = b.Build( "weather_dust" );
	}

	private void UpdateStreaks()
	{
		float intensity = MathF.Max( _rain, MathF.Max( _snow, _dust ) );
		var cam = Scene.Camera;
		int wanted = (int)(MaxStreaks * Math.Clamp( intensity, 0f, 1f ) * _dense * (_dust > 0.05f ? 0.8f : 1f));

		if ( wanted <= 0 && _streaks.Count == 0 ) return;
		EnsureModels();

		if ( !_streakRoot.IsValid() )
		{
			_streakRoot = new GameObject( true, "Weather Streaks" );
			_streakRoot.Flags |= GameObjectFlags.NotSaved;
			_streaks.Clear();
		}

		var model = _rain >= _snow && _rain >= _dust ? _rainModel : _snow >= _dust ? _flakeModel : _dustModel;
		float fall = model == _rainModel ? 2300f : model == _flakeModel ? 160f : 60f;
		fall *= 1f + (_dense - 1f) * 0.4f;
		float sideways = (model == _dustModel ? 1400f : model == _rainModel ? 300f : 220f) * (0.4f + Wind);
		var windDir = new Vector3( 0.8f, 0.6f, 0f ).Normal;

		while ( _streaks.Count < wanted )
		{
			var go = new GameObject( true, "Streak" );
			go.Parent = _streakRoot;
			go.Flags |= GameObjectFlags.NotSaved;
			var r = go.AddComponent<ModelRenderer>();
			r.RenderType = ModelRenderer.ShadowRenderType.Off;
			_streaks.Add( new Streak { Go = go, Renderer = r, Offset = RandomOffset(), Speed = 0.7f + (float)_random.NextDouble() * 0.6f, Drift = (float)_random.NextDouble() * 6.28f } );
		}
		while ( _streaks.Count > wanted )
		{
			var s = _streaks[^1];
			_streaks.RemoveAt( _streaks.Count - 1 );
			if ( s.Go.IsValid() ) s.Go.Destroy();
		}

		var origin = cam.WorldPosition;
		var velocity = windDir * sideways + Vector3.Down * fall;
		var rot = Rotation.LookAt( velocity.Normal, Vector3.Up ) * Rotation.From( 90f, 0f, 0f );   // the box's long (up) axis points along the fall

		foreach ( var s in _streaks )
		{
			if ( !s.Go.IsValid() ) continue;
			var v = velocity * s.Speed;
			if ( model == _flakeModel ) v += new Vector3( MathF.Sin( Time.Now * 1.3f + s.Drift ), MathF.Cos( Time.Now * 1.1f + s.Drift ), 0f ) * 60f;
			s.Offset += v * Time.Delta;

			// wrapped around the camera so it is always raining where you look
			if ( s.Offset.z < -Height * 0.35f || s.Offset.Length > Radius * 1.4f ) s.Offset = RandomOffset( true );

			if ( s.Renderer.Model != model ) s.Renderer.Model = model;
			s.Go.WorldPosition = origin + s.Offset;
			s.Go.WorldRotation = model == _rainModel ? rot : Rotation.Identity;
		}
	}

	private Vector3 RandomOffset( bool top = false )
	{
		float a = (float)_random.NextDouble() * MathF.PI * 2f;
		float d = MathF.Sqrt( (float)_random.NextDouble() ) * Radius;
		float z = top ? Height * 0.65f : (float)_random.NextDouble() * Height - Height * 0.35f;
		return new Vector3( MathF.Cos( a ) * d, MathF.Sin( a ) * d, z );
	}
}
