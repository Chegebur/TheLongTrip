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

	[Property, Group( "Time of day" ), Range( 0f, 24f ), Title( "Hour (0-24, 12 = noon)" )] public float Hour { get; set; } = 9f;
	[Property, Group( "Time of day" ), Range( 0.5f, 120f ), Title( "Minutes for a whole day" )] public float DayLengthMinutes { get; set; } = 24f;
	[Property, Group( "Time of day" ), Title( "Freeze time" )] public bool FreezeTime { get; set; }
	[Property, Group( "Time of day" ), Range( 0f, 3f ), Title( "Daylight: sunshine" )] public float DaySun { get; set; } = 1.3f;
	[Property, Group( "Time of day" ), Range( 0.5f, 2f ), Title( "Daylight: vibrance" )] public float DayVibrance { get; set; } = 1.2f;
	[Property, Group( "Time of day" ), Range( 0f, 1f ), Title( "Sunset: orange glow" )] public float SunsetGlow { get; set; } = 0.8f;
	[Property, Group( "Time of day" ), Range( 0f, 4f ), Title( "Night: moonlight and ambient (0 = pitch dark)" )] public float NightBrightness { get; set; } = 1f;
	[Property, Group( "Time of day" ), Range( 0f, 1f ), Title( "Night: fog closing in" )] public float NightFog { get; set; } = 0.65f;
	[Property, Group( "Time of day" ), Range( 0f, 1f ), Title( "Night: drained colour" )] public float NightDrain { get; set; } = 0.45f;

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
	[Property, Group( "Ambient occlusion" ), Range( 0f, 4f ), Title( "Intensity" )] public float AoIntensity { get; set; } = 0.9f;
	[Property, Group( "Ambient occlusion" ), Range( 0f, 256f ), Title( "Radius" )] public float AoRadius { get; set; } = 96f;

	[Property, Group( "Camera" ), Range( 40f, 120f ), Title( "Field of view on foot" )] public float FootFov { get; set; } = 78f;
	[Property, Group( "Camera" ), Range( 0f, 1f ), Title( "Vignette standing" )] public float VignetteStanding { get; set; } = 0.16f;
	[Property, Group( "Camera" ), Range( 0f, 1f ), Title( "Vignette at speed" )] public float VignetteSpeed { get; set; } = 0.34f;
	[Property, Group( "Camera" ), Range( 0f, 2f ), Title( "Sharpening" )] public float Sharpening { get; set; } = 0.25f;

	[Button( "Reset to defaults" )]
	public void ResetToDefaults()
	{
		Hour = 9f; DayLengthMinutes = 24f; FreezeTime = false; DaySun = 1.3f; DayVibrance = 1.2f; SunsetGlow = 0.8f; NightBrightness = 1f; NightFog = 0.65f; NightDrain = 0.45f;
		SunBrightness = AmbientBrightness = SkyBrightness = OtherLights = 1f;
		SunTint = AmbientTint = SkyTint = FogTint = Color.White;
		FogEnabled = true; FogDistance = FogStart = FogFalloff = 1f;
		Saturation = Contrast = 1f; Brightness = 1f; HueShift = 0f;
		BloomEnabled = true; BloomStrength = 0.35f; BloomThreshold = 1.4f;
		Exposure = 0f;
		AoEnabled = true; AoIntensity = 0.9f; AoRadius = 96f;
		FootFov = 78f; VignetteStanding = 0.16f; VignetteSpeed = 0.34f; Sharpening = 0.25f;
	}

	protected override void OnEnabled() => Instance = this;
	protected override void OnDestroy() { if ( Instance == this ) Instance = null; }

	private readonly Dictionary<Light, Color> _lightBase = new();

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
			if ( tone.IsValid() ) tone.ExposureCompensation = Exposure;

			var ao = cam.GameObject.Components.Get<AmbientOcclusion>( true );
			if ( ao.IsValid() ) { ao.Enabled = AoEnabled; ao.Intensity = AoIntensity; ao.Radius = (int)AoRadius; }

			var fog = cam.GameObject.Components.Get<GradientFog>( true );
			if ( fog.IsValid() ) fog.Enabled = FogEnabled;

			var grade = cam.GameObject.Components.Get<ColorAdjustments>( true );
			if ( grade.IsValid() ) { grade.Brightness = Brightness; grade.HueRotate = HueShift; }

			var sharpen = cam.GameObject.Components.Get<Sharpen>( true );
			if ( sharpen.IsValid() ) sharpen.Scale = Sharpening;
		}

		// every light that is not the sun or the ambient light: scale what it was
		foreach ( var l in Scene.GetAllComponents<Light>() )
		{
			if ( l is DirectionalLight || !l.IsValid() ) continue;
			if ( !_lightBase.TryGetValue( l, out var original ) ) _lightBase[l] = original = l.LightColor;
			l.LightColor = original * OtherLights;
		}
		if ( _lightBase.Count > 256 ) _lightBase.Where( kv => !kv.Key.IsValid() ).Select( kv => kv.Key ).ToList().ForEach( k => _lightBase.Remove( k ) );
	}
}
