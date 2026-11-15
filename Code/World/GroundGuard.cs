namespace LongTrip.World;

/// <summary>
/// Safety net under the world: no physics object may stay below the ground you can see. Terrain collision is a thin,
/// half-resolution mesh that only exists near the target, so a fast or heavy body (a big car) can tunnel through it, drive
/// into chunks whose collision hasn't been built yet, or sink into a hill the coarse mesh cuts under. The ground height is an
/// exact function of position (the same one the terrain mesh is built from), so every fixed step each dynamic body is checked
/// against it, and anything whose lowest point is under the surface is pushed straight up onto it.
/// </summary>
public sealed class GroundGuard : Component
{
	/// <summary>How far below the visible surface (world units, 1 = 1 inch) a body's lowest point may be before it is lifted.</summary>
	[Property] public float Margin { get; set; } = 3f;

	/// <summary>How far above the surface a lifted body is placed.</summary>
	[Property] public float Clearance { get; set; } = 2f;

	private WorldStreamer _world;
	private readonly List<Rigidbody> _bodies = new();
	private TimeSince _lastScan = 999f;

	/// <summary>The visible ground height at a point: the terrain, or the road where the road covers it.</summary>
	public static float SurfaceHeight( WorldGenerator gen, float x, float y )
	{
		float ground = gen.GetHeight( x, y );
		float halfRoad = gen.Settings.RoadWidth * 0.5f + 40f;   // the asphalt plus its shoulder
		if ( MathF.Abs( y - gen.RoadCenterY( x ) ) <= halfRoad )
			ground = MathF.Max( ground, gen.RoadHeight( x ) );
		return ground;
	}

	protected override void OnFixedUpdate()
	{
		_world ??= Components.Get<WorldStreamer>() ?? Scene.GetAllComponents<WorldStreamer>().FirstOrDefault();
		var gen = _world?.Generator;
		if ( gen is null || !_world.IsReady ) return;

		// bodies come and go (cars spawn, props are thrown), so the list is refreshed a few times a second
		if ( _lastScan > 0.5f )
		{
			_lastScan = 0f;
			_bodies.Clear();
			foreach ( var rb in Scene.GetAllComponents<Rigidbody>() )
				if ( rb.IsValid() && rb.MotionEnabled ) _bodies.Add( rb );
		}

		foreach ( var rb in _bodies )
		{
			if ( !rb.IsValid() ) continue;

			// How far under the visible surface the body is, judged from points that can only be below it when the body is
			// really buried (never from a bounding box, which would flag any car parked on a slope): its origin, and for a car
			// each suspension mount (a wheel's mount is always at least a tyre radius above the ground).
			var position = rb.WorldPosition;
			float deficit = SurfaceHeight( gen, position.x, position.y ) - position.z;
			float lift = deficit > Margin ? deficit + Clearance : 0f;

			var car = rb.GameObject.Components.Get<VehicleController>();
			if ( car is not null )
			{
				foreach ( var wheel in car.Wheels )
				{
					var p = wheel.WorldPosition;
					float d = SurfaceHeight( gen, p.x, p.y ) - p.z;
					if ( d > Margin )
						lift = MathF.Max( lift, d + Clearance + wheel.Radius * Units.MetersToUnits * 0.5f );
				}
			}

			if ( lift <= 0f ) continue;

			rb.WorldPosition = position + Vector3.Up * lift;
			var v = rb.Velocity;
			if ( v.z < 0f ) rb.Velocity = v.WithZ( 0f );
			Log.Info( $"[ground] '{rb.GameObject.Name}' was under the ground, lifted {lift:F1}u" );
		}
	}
}
