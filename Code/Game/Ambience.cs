using LongTrip.World;

namespace LongTrip.Gameplay;

/// <summary>
/// The sound of the world around you: rain on a roof, wind, birds on a fine day, and a low hollow hum at night. Each is a loop whose volume follows
/// the weather, the time of day and the biome, and fades in and out smoothly (a loop only plays while it can be heard). The sounds come from the
/// s&amp;box catalog (see CREDITS.md). Added next to the <see cref="Atmosphere"/>.
/// </summary>
public sealed class Ambience : Component
{
	[Property, Range( 0f, 2f )] public float Master { get; set; } = 1f;

	private sealed class Layer
	{
		public string Sound;
		public SoundHandle Handle;
		public float Volume;
		public float Target;
		public float Gain = 1f;
	}

	private readonly Layer _rain = new() { Sound = Sfx.Rain, Gain = 0.8f };
	private readonly Layer _wind = new() { Sound = Sfx.WindLoop, Gain = 0.7f };
	private readonly Layer _birds = new() { Sound = Sfx.Birds, Gain = 0.55f };
	private readonly Layer _night = new() { Sound = Sfx.NightHum, Gain = 0.5f };
	private readonly Layer _heart = new() { Sound = Sfx.Heartbeat, Gain = 0.9f };
	private PlayerStats _stats;

	private WorldStreamer _world;

	protected override void OnStart() => _world = Scene.GetAllComponents<WorldStreamer>().FirstOrDefault();

	protected override void OnUpdate()
	{
		var weather = Weather.Instance;
		float dark = GraphicsSettings.Darkness;

		float rain = weather?.RainLevel ?? 0f;
		float windiness = weather is null ? 0f : Math.Clamp( (weather.Wind - 0.2f) / 0.8f, 0f, 1f );
		float storm = weather is null ? 0f : (weather.Kind is WeatherKind.Storm or WeatherKind.Blizzard or WeatherKind.Sandstorm or WeatherKind.HeavyRain or WeatherKind.HeavySnow ? 1f : 0f);

		// birds sing on a dry day, in green country
		float green = 0f;
		var cam = Scene.Camera;
		if ( _world is { IsReady: true } && cam.IsValid() )
		{
			var gen = _world.Generator;
			var biome = gen.Biomes[gen.DominantBiome( cam.WorldPosition.x, cam.WorldPosition.y )];
			green = biome == BiomeLibrary.Forest ? 1f : biome == BiomeLibrary.Grasslands ? 0.7f : 0f;
		}

		_rain.Target = rain * (0.45f + 0.55f * storm);
		_wind.Target = 0.12f + windiness * 0.9f;
		_birds.Target = (1f - dark) * (1f - Math.Clamp( rain * 1.4f, 0f, 1f )) * (1f - windiness) * green;
		_night.Target = dark * (1f - rain * 0.5f);

		// a pounding heart when you are badly hurt
		_stats ??= Scene.GetAllComponents<PlayerStats>().FirstOrDefault();
		float health = _stats.IsValid() ? _stats.Health : PlayerStats.Max;
		_heart.Target = health <= 0f ? 0f : Math.Clamp( (35f - health) / 35f, 0f, 1f );

		foreach ( var layer in new[] { _rain, _wind, _birds, _night, _heart } ) Tick( layer );
	}

	private void Tick( Layer layer )
	{
		layer.Volume = MathX.Approach( layer.Volume, layer.Target, Time.Delta * 0.25f );

		if ( layer.Volume < 0.01f )
		{
			if ( layer.Handle is not null ) { layer.Handle.Stop( 0.5f ); layer.Handle = null; }
			return;
		}

		// (re)start the loop when it is wanted and not playing (some sounds are one long take rather than a loop)
		if ( layer.Handle is null || !layer.Handle.IsPlaying ) layer.Handle = Sfx.Loop( layer.Sound );
		if ( layer.Handle is not null ) layer.Handle.Volume = layer.Volume * layer.Gain * Master;
	}

	protected override void OnDisabled()
	{
		foreach ( var layer in new[] { _rain, _wind, _birds, _night, _heart } ) { layer.Handle?.Stop( 0.2f ); layer.Handle = null; }
	}
}
