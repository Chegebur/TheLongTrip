namespace LongTrip.Gameplay;

/// <summary>The numbers the HUD and the inventory show about where and when you are: the clock, your coordinates, the mile along the road and your heading.</summary>
public static class WorldInfo
{
	/// <summary>The game clock: the time of day from GraphicsSettings (it starts at 08:00; a day is 24 real minutes unless changed there).</summary>
	public static string Clock
	{
		get
		{
			// the clock shows the same time of day the sky does (GraphicsSettings drives both)
			var graphics = GraphicsSettings.Instance;
			float minutes = graphics is not null ? graphics.Hour * 60f : 8f * 60f + Time.Now;
			int m = (int)(minutes % 1440f);
			return $"{m / 60:00}:{m % 60:00}";
		}
	}

	/// <summary>Position in metres (x runs along the road), the mile along the road, the heading and the speed.</summary>
	public static string[] Lines( Scene scene )
	{
		var cam = scene.Camera;
		if ( !cam.IsValid() ) return null;

		var p = cam.WorldPosition * Units.UnitsToMeters;
		float mile = p.x / 1609.34f;
		float yaw = cam.WorldRotation.Angles().yaw;
		string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
		// +X is the road's forward ("north"); +Y is to its left ("west")
		string heading = names[((int)MathF.Round( -yaw / 45f ) % 8 + 8) % 8];

		return new[]
		{
			$"{Clock}   {heading}",
			$"Mile {mile:0.00}",
			$"X {p.x:0}  Y {p.y:0}  Z {p.z:0} m",
			$"Seed {scene.GetAllComponents<LongTrip.World.WorldStreamer>().FirstOrDefault()?.Seed}",
		};
	}
}
