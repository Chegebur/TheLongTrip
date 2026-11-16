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

	/// <summary>Base field of view on foot. (When driving, the chase camera widens it with speed.)</summary>
	[Property, Group( "Camera" )] public float FootFov { get; set; } = 78f;

	/// <summary>Vignette strength at a standstill and at full speed.</summary>
	[Property, Group( "Camera" )] public Vector2 VignetteRange { get; set; } = new Vector2( 0.16f, 0.34f );

	/// <summary>Sharpening scale (a touch of crispness against the soft look of anti-aliasing).</summary>
	[Property, Group( "Camera" )] public float SharpenScale { get; set; } = 0.25f;

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
	private bool _first = true;

	protected override void OnStart()
	{
		_world = Components.Get<WorldStreamer>() ?? Scene.GetAllComponents<WorldStreamer>().FirstOrDefault();
		_car = Components.Get<CarSession>() ?? Scene.GetAllComponents<CarSession>().FirstOrDefault();
		_sun = Scene.GetAllComponents<DirectionalLight>().FirstOrDefault();
		_ambient = Scene.GetAllComponents<AmbientLight>().FirstOrDefault();
		_sky = Scene.GetAllComponents<SkyBox2D>().FirstOrDefault();
		Components.GetOrCreate<Weather>();
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
		_sharpen.Scale = SharpenScale;
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
		Apply( WithWeather( _current ) );

		// ---- camera: speed-sensitive FOV and vignette
		float speed = _car.IsValid() && _car.Controller.IsValid() ? _car.Controller.SpeedMs : 0f;
		float s = Math.Clamp( speed / 40f, 0f, 1f );

		if ( _vignette.IsValid() )
			_vignette.Intensity = MathX.Lerp( VignetteRange.x, VignetteRange.y, s );

		// The chase camera owns the FOV while driving; on foot we ease back to the base value.
		if ( !(_car.IsValid() && _car.IsDriving) )
			_camera.FieldOfView = MathX.Lerp( _camera.FieldOfView, FootFov, 1f - MathF.Exp( -4f * Time.Delta ) );
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
