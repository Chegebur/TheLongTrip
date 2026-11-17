using LongTrip.World;

namespace LongTrip.Gameplay;

/// <summary>
/// One place for the whole look of the game. Select the object this is on (it is added next to the Atmosphere automatically) and tune everything in
/// the inspector while the game runs: sun, ambient and sky brightness, fog, colour grading, bloom, exposure, ambient occlusion, vignette, sharpening,
/// field of view and the brightness of every other light (headlights, flashlight, lamps).
/// <para>
/// The brightness and fog numbers are <b>multipliers on top of each biome's and weather's own look</b> (1 = as designed), so the day/night-free
/// biome blending and the weather keep working while you tune. The rest are direct values. "Reset to defaults" puts everything back.
/// </para>
/// </summary>
public sealed class GraphicsSettings : Component
{
	public static GraphicsSettings Instance { get; private set; }

	// ---- time of day ---------------------------------------------------------------------------------------------------------------

	[Property, Group( "Time of day" ), Range( 0f, 24f ), Title( "Hour (0-24, 12 = noon)" )] public float Hour { get; set; } = 8f;
	[Property, Group( "Time of day" ), Range( 0.5f, 120f ), Title( "Minutes for a whole day" )] public float DayLengthMinutes { get; set; } = 24f;
	[Property, Group( "Time of day" ), Title( "Freeze time" )] public bool FreezeTime { get; set; }
	[Property, Group( "Time of day" ), Range( 0f, 3f ), Title( "Daylight: sunshine" )] public float DaySun { get; set; } = 1.3f;
	[Property, Group( "Time of day" ), Range( 0.5f, 2f ), Title( "Daylight: vibrance" )] public float DayVibrance { get; set; } = 1.2f;
	[Property, Group( "Time of day" ), Range( 0f, 1f ), Title( "Sunset: orange glow" )] public float SunsetGlow { get; set; } = 0.8f;
	[Property, Group( "Time of day" ), Range( 0f, 4f ), Title( "Night: moonlight and ambient (0 = pitch dark)" )] public float NightBrightness { get; set; } = 1f;
	[Property, Group( "Time of day" ), Range( 0f, 1f ), Title( "Night: fog closing in" )] public float NightFog { get; set; } = 0.35f;
	[Property, Group( "Time of day" ), Range( 0f, 4f ), Title( "Night: eyes adjusting (exposure, EV)" )] public float NightExposure { get; set; } = 2.1f;
	[Property, Group( "Time of day" ), Range( 0f, 1f ), Title( "Night: drained colour" )] public float NightDrain { get; set; } = 0.25f;

	/// <summary>Sun height above the horizon in degrees (negative: night).</summary>
	public float SunElevation => MathF.Sin( (Hour - 6f) / 24f * MathF.PI * 2f ) * 68f;

	// ---- light -------------------------------------------------------------------------------------------------------------------

	[Property, Group( "Light" ), Range( 0f, 4f ), Title( "Sun brightness" )] public float SunBrightness { get; set; } = 1f;
	[Property, Group( "Light" ), Title( "Sun tint" )] public Color SunTint { get; set; } = Color.White;
	[Property, Group( "Light" ), Range( 0f, 4f ), Title( "Ambient light" )] public float AmbientBrightness { get; set; } = 1f;
	[Property, Group( "Light" ), Title( "Ambient tint" )] public Color AmbientTint { get; set; } = Color.White;
	[Property, Group( "Light" ), Range( 0f, 4f ), Title( "Sky brightness" )] public float SkyBrightness { get; set; } = 1f;
	[Property, Group( "Light" ), Title( "Sky tint" )] public Color SkyTint { get; set; } = Color.White;
	[Property, Group( "Light" ), Range( 0f, 5f ), Title( "Other lights (headlights, lamps, flashlight)" )] public float OtherLights { get; set; } = 1f;

	// ---- shadows -----------------------------------------------------------------------------------------------------------------

	[Property, Group( "Shadows" ), Title( "Lights in view cast shadows" )] public bool ViewShadows { get; set; } = true;
	[Property, Group( "Shadows" ), Range( 0f, 90f ), Title( "Extra degrees outside the view" )] public float ShadowMargin { get; set; } = 35f;
	[Property, Group( "Shadows" ), Range( 1, 64 ), Title( "Most lights with shadows at once" )] public int MaxShadowedLights { get; set; } = 16;
	[Property, Group( "Shadows" ), Range( 500f, 30000f ), Title( "Shadow distance" )] public float ShadowDistance { get; set; } = 7000f;

	/// <summary>How dark it is, 0 (day) to 1 (night): what street lamps and house lights go by.</summary>
	public static float Darkness
	{
		get
		{
			if ( Instance is null ) return 0f;
			float t = Math.Clamp( (Instance.SunElevation + 6f) / 14f, 0f, 1f );
			return 1f - t * t * (3f - 2f * t);
		}
	}

	// ---- fog ---------------------------------------------------------------------------------------------------------------------

	[Property, Group( "Fog" ), Title( "Fog on" )] public bool FogEnabled { get; set; } = true;
	[Property, Group( "Fog" ), Range( 0.1f, 5f ), Title( "How far you see (x)" )] public float FogDistance { get; set; } = 1f;
	[Property, Group( "Fog" ), Range( 0.1f, 5f ), Title( "Where it starts (x)" )] public float FogStart { get; set; } = 1f;
	[Property, Group( "Fog" ), Range( 0.3f, 3f ), Title( "Fog falloff (x)" )] public float FogFalloff { get; set; } = 1f;
	[Property, Group( "Fog" ), Title( "Fog tint" )] public Color FogTint { get; set; } = Color.White;

	// ---- colour ------------------------------------------------------------------------------------------------------------------

	[Property, Group( "Colour" ), Range( 0f, 2.5f )] public float Saturation { get; set; } = 1f;
	[Property, Group( "Colour" ), Range( 0.3f, 2f )] public float Contrast { get; set; } = 1f;
	[Property, Group( "Colour" ), Range( 0f, 2.5f )] public float Brightness { get; set; } = 1f;
	[Property, Group( "Colour" ), Range( -180f, 180f ), Title( "Hue shift" )] public float HueShift { get; set; } = 0f;

	// ---- post processing ---------------------------------------------------------------------------------------------------------

	[Property, Group( "Bloom" ), Title( "Bloom on" )] public bool BloomEnabled { get; set; } = true;
	[Property, Group( "Bloom" ), Range( 0f, 3f ), Title( "Strength" )] public float BloomStrength { get; set; } = 0.35f;
	[Property, Group( "Bloom" ), Range( 0f, 4f ), Title( "Threshold" )] public float BloomThreshold { get; set; } = 1.4f;

	[Property, Group( "Exposure" ), Range( -3f, 3f ), Title( "Exposure compensation" )] public float Exposure { get; set; } = 0f;

	[Property, Group( "Ambient occlusion" ), Title( "AO on" )] public bool AoEnabled { get; set; } = true;
	[Property, Group( "Ambient occlusion" ), Range( 0f, 4f ), Title( "Intensity" )] public float AoIntensity { get; set; } = 0.5f;
	[Property, Group( "Ambient occlusion" ), Range( 0f, 256f ), Title( "Radius" )] public float AoRadius { get; set; } = 48f;

	[Property, Group( "Camera" ), Range( 40f, 120f ), Title( "Field of view on foot" )] public float FootFov { get; set; } = 78f;
	[Property, Group( "Camera" ), Range( 0f, 1f ), Title( "Vignette standing" )] public float VignetteStanding { get; set; } = 0f;
	[Property, Group( "Camera" ), Range( 0f, 1f ), Title( "Vignette at speed" )] public float VignetteSpeed { get; set; } = 0f;
	[Property, Group( "Camera" ), Range( 0f, 2f ), Title( "Sharpening" )] public float Sharpening { get; set; } = 0f;

	[Button( "Reset to defaults" )]
	public void ResetToDefaults()
	{
		Hour = 8f; DayLengthMinutes = 24f; FreezeTime = false; DaySun = 1.3f; DayVibrance = 1.2f; SunsetGlow = 0.8f; NightBrightness = 1f; NightFog = 0.35f; NightDrain = 0.25f; NightExposure = 2.1f;
		SunBrightness = AmbientBrightness = SkyBrightness = OtherLights = 1f;
		SunTint = AmbientTint = SkyTint = FogTint = Color.White;
		FogEnabled = true; FogDistance = FogStart = FogFalloff = 1f;
		Saturation = Contrast = 1f; Brightness = 1f; HueShift = 0f;
		BloomEnabled = true; BloomStrength = 0.35f; BloomThreshold = 1.4f;
		Exposure = 0f;
		AoEnabled = true; AoIntensity = 0.5f; AoRadius = 48f;
		FootFov = 78f; VignetteStanding = 0f; VignetteSpeed = 0f; Sharpening = 0f;
	}

	protected override void OnEnabled() => Instance = this;
	protected override void OnDestroy() { if ( Instance == this ) Instance = null; }

	private readonly Dictionary<Light, Color> _lightBase = new();
	private TimeUntil _nextShadowUpdate;

	/// <summary>
	/// Lights in front of you cast shadows, and so do those a good way outside the view (a lamp just behind the edge of the screen throws shadows into it,
	/// and turning must not make them pop). Everything else goes without: shadows are what costs. At most <see cref="MaxShadowedLights"/>, nearest first.
	/// </summary>
	private void UpdateShadows( CameraComponent cam )
	{
		if ( !ViewShadows || !_nextShadowUpdate ) return;
		_nextShadowUpdate = 0.15f;

		var forward = cam.WorldRotation.Forward;
		float limit = cam.FieldOfView * 0.6f + ShadowMargin;   // a little more than half the view, for the corners
		var candidates = new List<(Light Light, float Distance, bool InView)>();

		foreach ( var l in Scene.GetAllComponents<Light>() )
		{
			if ( l is DirectionalLight || !l.IsValid() ) continue;
			var to = l.WorldPosition - cam.WorldPosition;
			float d = to.Length;
			bool inView = d < 400f || (d < ShadowDistance && MathF.Acos( Math.Clamp( Vector3.Dot( forward, to / MathF.Max( d, 0.01f ) ), -1f, 1f ) ) * (180f / MathF.PI) < limit);
			candidates.Add( (l, d, inView) );
		}

		candidates.Sort( ( a, b ) => a.Distance.CompareTo( b.Distance ) );
		int shadowed = 0;
		foreach ( var c in candidates )
		{
			bool on = c.InView && shadowed < MaxShadowedLights;
			if ( on ) shadowed++;
			if ( c.Light.Shadows != on ) c.Light.Shadows = on;
		}
	}

	protected override void OnUpdate()
	{
		Instance = this;
		if ( !FreezeTime ) Hour = (Hour + Time.Delta / (DayLengthMinutes * 60f) * 24f) % 24f;

		var cam = Scene.Camera;
		if ( cam.IsValid() )
		{
			var bloom = cam.GameObject.Components.Get<Bloom>( true );
			if ( bloom.IsValid() ) { bloom.Enabled = BloomEnabled; bloom.Strength = BloomStrength; bloom.Threshold = BloomThreshold; }

			var tone = cam.GameObject.Components.Get<Tonemapping>( true );
			if ( tone.IsValid() ) tone.ExposureCompensation = Exposure + Darkness * NightExposure;   // in the dark the eyes open up, like a camera's auto exposure

			var ao = cam.GameObject.Components.Get<AmbientOcclusion>( true );
			if ( ao.IsValid() ) { ao.Enabled = AoEnabled; ao.Intensity = AoIntensity; ao.Radius = (int)AoRadius; }

			var fog = cam.GameObject.Components.Get<GradientFog>( true );
			if ( fog.IsValid() ) fog.Enabled = FogEnabled;

			var grade = cam.GameObject.Components.Get<ColorAdjustments>( true );
			if ( grade.IsValid() ) { grade.Brightness = Brightness; grade.HueRotate = HueShift; }

			// no vignette unless asked for: it darkens the edges of the picture
			var vignette = cam.GameObject.Components.Get<Vignette>( true );
			if ( vignette.IsValid() ) vignette.Enabled = VignetteStanding > 0.001f || VignetteSpeed > 0.001f;

			var sharpen = cam.GameObject.Components.Get<Sharpen>( true );
			if ( sharpen.IsValid() ) { sharpen.Enabled = Sharpening > 0.01f; sharpen.Scale = Sharpening; }   // off by default: sharpening draws outlines around edges
		}

		// every light that is not the sun or the ambient light: scale what it was
		foreach ( var l in Scene.GetAllComponents<Light>() )
		{
			if ( l is DirectionalLight || !l.IsValid() ) continue;
			if ( !_lightBase.TryGetValue( l, out var original ) ) _lightBase[l] = original = l.LightColor;
			l.LightColor = original * OtherLights;
		}
		if ( cam.IsValid() ) UpdateShadows( cam );
		if ( _lightBase.Count > 256 ) _lightBase.Where( kv => !kv.Key.IsValid() ).Select( kv => kv.Key ).ToList().ForEach( k => _lightBase.Remove( k ) );
	}
}
