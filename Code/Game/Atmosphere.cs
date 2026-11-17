using LongTrip.World;

namespace LongTrip.Gameplay;

/// <summary>
/// The look of the world. Fog, sun, ambient light, sky tint and grading are blended smoothly between biomes as you
/// drive, and the camera reacts to speed (a gentle FOV widening and vignette). Everything here is a cheap per-frame
/// parameter change on components the camera and scene already have.
/// </summary>
public sealed class Atmosphere : Component
{
	/// <summary>Everything that differs between biomes.</summary>
	private struct Look
	{
		public Color FogColor;
		public float FogStart, FogEnd, FogExponent;
		public Color Sun;
		public float SunIntensity;
		public Color Ambient;
		public float Saturation, Contrast;
		public Color SkyTint;
	}

	// Index = BiomeLibrary.All index: Desert, Grasslands, Ice Plains, Forest.
	private static readonly Look[] Looks =
	{
		// Desert: warm, dusty haze
		new Look { FogColor = new Color( 0.88f, 0.80f, 0.66f ), FogStart = 4500, FogEnd = 62000, FogExponent = 1.25f,
			Sun = new Color( 1.00f, 0.93f, 0.80f ), SunIntensity = 1.9f, Ambient = new Color( 0.40f, 0.43f, 0.50f ),
			Saturation = 1.14f, Contrast = 1.07f, SkyTint = new Color( 1.00f, 0.97f, 0.90f ) },
		// Grasslands: clear and fresh
		new Look { FogColor = new Color( 0.70f, 0.81f, 0.95f ), FogStart = 5000, FogEnd = 58000, FogExponent = 1.3f,
			Sun = new Color( 1.00f, 0.96f, 0.88f ), SunIntensity = 1.8f, Ambient = new Color( 0.38f, 0.45f, 0.55f ),
			Saturation = 1.16f, Contrast = 1.05f, SkyTint = new Color( 1f, 1f, 1f ) },
		// Ice plains: cold and closer
		new Look { FogColor = new Color( 0.78f, 0.86f, 0.97f ), FogStart = 2500, FogEnd = 42000, FogExponent = 1.1f,
			Sun = new Color( 0.96f, 0.98f, 1.00f ), SunIntensity = 1.55f, Ambient = new Color( 0.42f, 0.50f, 0.62f ),
			Saturation = 1.00f, Contrast = 1.04f, SkyTint = new Color( 0.95f, 0.98f, 1.05f ) },
		// Forest: green-grey air, shorter sight lines, softer light under the canopy
		new Look { FogColor = new Color( 0.62f, 0.72f, 0.66f ), FogStart = 1800, FogEnd = 30000, FogExponent = 1.2f,
			Sun = new Color( 1.00f, 0.95f, 0.80f ), SunIntensity = 1.5f, Ambient = new Color( 0.34f, 0.44f, 0.40f ),
			Saturation = 1.12f, Contrast = 1.06f, SkyTint = new Color( 0.96f, 1.00f, 0.96f ) },
	};

	/// <summary>How quickly the look follows the biome (per second). Biome blends are long, so this is gentle.</summary>
	[Property, Group( "Atmosphere" )] public float Follow { get; set; } = 1.5f;

	private WorldStreamer _world;
	private CarSession _car;
	private CameraComponent _camera;
	private GradientFog _fog;
	private ColorAdjustments _grade;
	private Vignette _vignette;
	private Sharpen _sharpen;
	private DirectionalLight _sun;
	private AmbientLight _ambient;
	private SkyBox2D _sky;

	private Look _current;
	private Rotation _sunHome;
	private float _night;
	private bool _first = true;

	protected override void OnStart()
	{
		_world = Components.Get<WorldStreamer>() ?? Scene.GetAllComponents<WorldStreamer>().FirstOrDefault();
		_car = Components.Get<CarSession>() ?? Scene.GetAllComponents<CarSession>().FirstOrDefault();
		_sun = Scene.GetAllComponents<DirectionalLight>().FirstOrDefault();
		if ( _sun.IsValid() ) _sunHome = _sun.WorldRotation;
		_ambient = Scene.GetAllComponents<AmbientLight>().FirstOrDefault();
		_sky = Scene.GetAllComponents<SkyBox2D>().FirstOrDefault();
		Components.GetOrCreate<Weather>();
		Components.GetOrCreate<Ambience>();
	}

	private void FindCameraEffects()
	{
		_camera = Scene.Camera;
		if ( !_camera.IsValid() ) return;

		var go = _camera.GameObject;
		_fog = go.GetOrAddComponent<GradientFog>();
		_grade = go.GetOrAddComponent<ColorAdjustments>();
		_vignette = go.GetOrAddComponent<Vignette>();
		_sharpen = go.GetOrAddComponent<Sharpen>();

		_vignette.Color = new Color( 0.02f, 0.03f, 0.05f );
		_vignette.Smoothness = 0.85f;
		_vignette.Roundness = 0.9f;
	}

	protected override void OnUpdate()
	{
		if ( _world is null || !_world.IsReady ) return;
		if ( !_camera.IsValid() ) FindCameraEffects();
		if ( !_camera.IsValid() ) return;

		var gen = _world.Generator;
		var pos = FocusPosition();

		// ---- biome look, blended by the same weights the terrain uses
		var blend = gen.GetBiome( pos.x, pos.y );
		var target = Mix( LookFor( blend.A ), LookFor( blend.B ), blend.T );

		float k = _first ? 1f : 1f - MathF.Exp( -Follow * Time.Delta );
		_first = false;
		_current = _current.FogEnd == 0 ? target : Mix( _current, target, k );
		Apply( Tweak( TimeOfDay( WithWeather( _current ) ) ) );

		// ---- camera: speed-sensitive FOV and vignette
		float speed = _car.IsValid() && _car.Controller.IsValid() ? _car.Controller.SpeedMs : 0f;
		float s = Math.Clamp( speed / 40f, 0f, 1f );

		if ( _vignette.IsValid() )
			_vignette.Intensity = MathX.Lerp( G.VignetteStanding, G.VignetteSpeed, s ) + _night * 0.22f;

		// The chase camera owns the FOV while driving; on foot we ease back to the base value.
		if ( !(_car.IsValid() && _car.IsDriving) )
			_camera.FieldOfView = MathX.Lerp( _camera.FieldOfView, G.FootFov, 1f - MathF.Exp( -4f * Time.Delta ) );
	}

	private Vector3 FocusPosition()
	{
		if ( _car.IsValid() && _car.IsDriving && _car.Car.IsValid() ) return _car.Car.WorldPosition;
		return _camera.WorldPosition;
	}

	/// <summary>The biome look, thickened, dimmed and tinted by the current weather (and flashed by lightning).</summary>
	private static Look WithWeather( Look l )
	{
		var w = Weather.Instance;
		if ( w is null ) return l;

		l.FogEnd *= w.Fog;
		l.FogStart *= MathF.Min( 1f, w.Fog * 1.4f );
		l.FogColor = Color.Lerp( l.FogColor, l.FogColor * w.Tint, 0.6f );
		float lum = (l.FogColor.r + l.FogColor.g + l.FogColor.b) / 3f;
		l.FogColor = Color.Lerp( l.FogColor, new Color( lum, lum, lum ), w.Desaturate * 0.5f ) * MathX.Lerp( 1f, 0.6f, 1f - w.Light );
		l.Sun = l.Sun * w.Tint;
		l.SunIntensity = l.SunIntensity * w.Light + w.Flash * 6f;
		l.Ambient = l.Ambient * w.Dim + Color.White * (w.Flash * 0.35f);
		l.Saturation *= 1f - w.Desaturate;
		l.SkyTint = Color.Lerp( l.SkyTint, l.SkyTint * w.Tint, 0.8f ) * MathX.Lerp( 1f, 0.28f, 1f - w.Light );
		return l;
	}

	private static float Smooth( float a, float b, float x ) { float t = Math.Clamp( (x - a) / (b - a), 0f, 1f ); return t * t * (3f - 2f * t); }

	/// <summary>
	/// Day and night on top of the biome and weather look. Day: stronger, warmer sun and richer colour. Sunset: an orange glow low in the sky.
	/// Night: the sun becomes a faint blue moon, ambient light almost goes out, the fog is dark and closes in, and the colour drains: a dark, quiet,
	/// slightly wrong world where the headlights and flashlight are what you see by.
	/// </summary>
	private Look TimeOfDay( Look l )
	{
		var g = G;
		float e = g.SunElevation;
		float night = 1f - Smooth( -9f, 5f, e);
		float day = Smooth( 8f, 35f, e );
		float glow = MathF.Exp( -MathF.Pow( (e - 4f) / 9f, 2f ) ) * (1f - night * 0.6f) * g.SunsetGlow;
		_night = night;

		// the sun goes round: east to west across the sky, and the moon takes its place at night
		if ( _sun.IsValid() )
		{
			float pitch = Math.Clamp( MathF.Abs( e ), 6f, 72f );
			float yaw = _sunHome.Angles().yaw + (g.Hour - 12f) * 15f + (night > 0.5f ? 180f : 0f);
			_sun.WorldRotation = Rotation.From( pitch, yaw, 0f );
		}

		// daylight: sunnier and more vibrant
		l.SunIntensity *= MathX.Lerp( 1f, g.DaySun, day );
		l.Saturation *= MathX.Lerp( 1f, g.DayVibrance, day );
		l.Contrast *= MathX.Lerp( 1f, 1.05f, day );
		l.Ambient = l.Ambient * MathX.Lerp( 1f, 1.15f, day );

		// sunset and sunrise
		l.Sun = Color.Lerp( l.Sun, new Color( 1f, 0.52f, 0.25f ), glow );
		l.SunIntensity *= MathX.Lerp( 1f, 0.85f, glow );
		l.FogColor = Color.Lerp( l.FogColor, new Color( 0.95f, 0.55f, 0.35f ), glow * 0.55f );
		l.SkyTint = Color.Lerp( l.SkyTint, new Color( 1f, 0.62f, 0.45f ), glow * 0.6f );

		// night
		float nb = g.NightBrightness;
		l.Sun = Color.Lerp( l.Sun, new Color( 0.42f, 0.58f, 1f ), night );
		l.SunIntensity = MathX.Lerp( l.SunIntensity, 0.11f * nb, night );
		l.Ambient = Color.Lerp( l.Ambient, new Color( 0.014f, 0.024f, 0.05f ) * nb, night );
		l.FogColor = Color.Lerp( l.FogColor, new Color( 0.012f, 0.022f, 0.04f ) * MathF.Min( 1f, nb ), night );
		l.FogStart *= MathX.Lerp( 1f, 1f - g.NightFog * 0.8f, night );
		l.FogEnd *= MathX.Lerp( 1f, 1f - g.NightFog * 0.7f, night );
		l.SkyTint = Color.Lerp( l.SkyTint, new Color( 0.012f, 0.02f, 0.05f ) * MathF.Min( 1.5f, nb ), night );
		l.Saturation *= MathX.Lerp( 1f, 1f - g.NightDrain, night );
		l.Contrast *= MathX.Lerp( 1f, 1.12f, night );
		return l;
	}

	private GraphicsSettings G => _graphics.IsValid() ? _graphics : _graphics = Components.GetOrCreate<GraphicsSettings>();
	private GraphicsSettings _graphics;

	/// <summary>The user's graphics tuning (GraphicsSettings) applied on top of the biome and weather look.</summary>
	private Look Tweak( Look l )
	{
		var g = G;
		l.Sun = l.Sun * g.SunTint;
		l.SunIntensity *= g.SunBrightness;
		l.Ambient = l.Ambient * g.AmbientTint * g.AmbientBrightness;
		l.SkyTint = l.SkyTint * g.SkyTint * g.SkyBrightness;
		l.FogColor = l.FogColor * g.FogTint;
		l.FogEnd *= g.FogDistance;
		l.FogStart *= g.FogStart;
		l.FogExponent *= g.FogFalloff;
		l.Saturation *= g.Saturation;
		l.Contrast *= g.Contrast;
		return l;
	}

	private static Look LookFor( int biome ) => Looks[Math.Clamp( biome, 0, Looks.Length - 1 )];

	private static Look Mix( Look a, Look b, float t ) => new()
	{
		FogColor = Color.Lerp( a.FogColor, b.FogColor, t ),
		FogStart = MathX.Lerp( a.FogStart, b.FogStart, t ),
		FogEnd = MathX.Lerp( a.FogEnd, b.FogEnd, t ),
		FogExponent = MathX.Lerp( a.FogExponent, b.FogExponent, t ),
		Sun = Color.Lerp( a.Sun, b.Sun, t ),
		SunIntensity = MathX.Lerp( a.SunIntensity, b.SunIntensity, t ),
		Ambient = Color.Lerp( a.Ambient, b.Ambient, t ),
		Saturation = MathX.Lerp( a.Saturation, b.Saturation, t ),
		Contrast = MathX.Lerp( a.Contrast, b.Contrast, t ),
		SkyTint = Color.Lerp( a.SkyTint, b.SkyTint, t ),
	};

	private void Apply( Look l )
	{
		if ( _fog.IsValid() )
		{
			_fog.Color = l.FogColor;
			_fog.StartDistance = l.FogStart;
			_fog.EndDistance = l.FogEnd;
			_fog.FalloffExponent = l.FogExponent;
		}

		if ( _sun.IsValid() )
		{
			_sun.LightColor = l.Sun;
			_sun.Intensity = l.SunIntensity;
		}

		if ( _ambient.IsValid() ) _ambient.Color = l.Ambient;
		if ( _sky.IsValid() ) _sky.Tint = l.SkyTint;

		if ( _grade.IsValid() )
		{
			_grade.Saturation = l.Saturation;
			_grade.Contrast = l.Contrast;
		}
	}
}
