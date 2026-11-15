namespace LongTrip.Gameplay;

/// <summary>
/// Makes the car behave in multiplayer. One machine simulates the car (whoever drives it; the host when nobody does) and every other
/// machine is a proxy: it only shows what it is told. The usual causes of a wobbly, drifting, stuttering networked car are handled here:
/// <list type="bullet">
/// <item>Exactly one simulator. The car is a network object with ownership that follows the driver, so its physics never run twice and
/// fight over the position; proxies interpolate the owner's transform (the engine's rigidbody proxy keeps velocities so it does not stutter).</item>
/// <item>Nothing physical runs on a proxy: suspension, the drivetrain, the parking brake and the carry-along are all owner-only.</item>
/// <item>Everything the owner's wheels, doors and parts show is synced as a few small values (steer, throttle, wheel spin and travel, which parts are fitted,
/// how far each door is open, fuel) and proxies build the same picture from them, instead of simulating it.</item>
/// <item>The driver's own camera and inputs are never touched on other machines.</item>
/// </list>
/// Put on the car's root. Without a network session it does nothing.
/// </summary>
public sealed class CarNetwork : Component
{
	[Sync] public float Steer { get; set; }
	[Sync] public float Throttle { get; set; }
	[Sync] public bool Handbrake { get; set; }
	[Sync] public float Rpm { get; set; }
	[Sync] public float Fuel { get; set; }

	/// <summary>Spin speed (rad/s) and suspension travel (m) of the four wheels.</summary>
	[Sync] public Vector4 WheelSpin { get; set; }
	[Sync] public Vector4 WheelDrop { get; set; }

	/// <summary>Which detachable parts are fitted (one bit per socket, in <see cref="OldCarAssembler.GroupNames"/> order).</summary>
	[Sync] public long PartMask { get; set; }

	/// <summary>How open each hinged part is: doors (fl, fr, rl, rr) and hood, trunk, handbrake.</summary>
	[Sync] public Vector4 HingesA { get; set; }
	[Sync] public Vector4 HingesB { get; set; }

	private VehicleController _car;
	private CarAssembly _assembly;
	private static readonly string[] HingeOrder = { "door_fl", "door_fr", "door_rl", "door_rr", "hood", "trunk", "handbrake" };

	protected override void OnStart()
	{
		_car = Components.Get<VehicleController>();
		_assembly = Components.Get<CarAssembly>();
		if ( !Networking.IsActive ) return;

		// one network object, taken over by whoever drives; if its owner leaves, the host keeps it
		GameObject.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
		GameObject.Network.SetOrphanedMode( NetworkOrphaned.Host );
		GameObject.Network.Interpolation = true;
		if ( !GameObject.Network.Active ) GameObject.NetworkSpawn();
	}

	/// <summary>The driver takes over the car's simulation (call when sitting down in the driver's seat).</summary>
	public void TakeControl()
	{
		if ( Networking.IsActive && GameObject.Network.Active && IsProxy ) GameObject.Network.TakeOwnership();
	}

	/// <summary>Nobody is driving any more: hand the car back to the host so it keeps being simulated somewhere sensible.</summary>
	public void ReleaseControl()
	{
		if ( Networking.IsActive && GameObject.Network.Active && !IsProxy && !Networking.IsHost ) GameObject.Network.DropOwnership();
	}

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsActive || !_car.IsValid() ) return;

		if ( !IsProxy ) Publish(); else Receive();
	}

	/// <summary>The owner writes what it simulated this tick.</summary>
	private void Publish()
	{
		Steer = _car.Steer;
		Throttle = _car.Throttle;
		Handbrake = _car.Handbrake;
		Rpm = _car.Drivetrain?.Rpm ?? 0f;

		var w = _car.Wheels;
		WheelSpin = new Vector4( Spin( w, 0 ), Spin( w, 1 ), Spin( w, 2 ), Spin( w, 3 ) );
		WheelDrop = new Vector4( Drop( w, 0 ), Drop( w, 1 ), Drop( w, 2 ), Drop( w, 3 ) );

		if ( _assembly is null ) return;
		Fuel = _assembly.Fuel;

		long mask = 0;
		var groups = OldCarAssembler.GroupNames;
		for ( int i = 0; i < groups.Length && i < 62; i++ )
			if ( _assembly.Has_( groups[i] ) ) mask |= 1L << i;
		PartMask = mask;

		var h = new float[8];
		for ( int i = 0; i < HingeOrder.Length; i++ )
			h[i] = _assembly.Sockets.TryGetValue( HingeOrder[i], out var s ) && s.IsAttached ? s.Angle : 0f;
		HingesA = new Vector4( h[0], h[1], h[2], h[3] );
		HingesB = new Vector4( h[4], h[5], h[6], 0f );
	}

	private static float Spin( IReadOnlyList<VehicleWheel> w, int i ) => i < w.Count ? w[i].AngularVelocity : 0f;
	private static float Drop( IReadOnlyList<VehicleWheel> w, int i ) => i < w.Count ? w[i].SuspensionLength : 0f;

	/// <summary>A proxy shows what it was told: steering, wheel positions, which parts are on, how far the doors are open.</summary>
	private void Receive()
	{
		if ( _assembly is not null )
		{
			_assembly.SetFuelFromNetwork( Fuel );
			_assembly.ApplyNetworkMask( PartMask );

			var h = new[] { HingesA.x, HingesA.y, HingesA.z, HingesA.w, HingesB.x, HingesB.y, HingesB.z };
			for ( int i = 0; i < HingeOrder.Length; i++ )
				if ( _assembly.Sockets.TryGetValue( HingeOrder[i], out var s ) && s.IsAttached ) s.TargetAngle = h[i];
		}
	}

	/// <summary>The synced spin and travel of a wheel, for proxies (see <see cref="WheelVisual"/>); null on the machine that simulates it.</summary>
	public (float Spin, float Drop)? WheelState( int index )
	{
		if ( !Networking.IsActive || !IsProxy || index < 0 || index > 3 ) return null;
		return index switch { 0 => (WheelSpin.x, WheelDrop.x), 1 => (WheelSpin.y, WheelDrop.y), 2 => (WheelSpin.z, WheelDrop.z), _ => (WheelSpin.w, WheelDrop.w) };
	}

	protected override void OnUpdate()
	{
		if ( !Networking.IsActive || !IsProxy || !_car.IsValid() ) return;

		// front wheels turn with the synced steering (their own yaw is normally set by the simulation, which is off here)
		float maxAngle = _car.Definition?.MaxSteerAngle ?? 30f;
		foreach ( var wheel in _car.Wheels )
			if ( wheel.IsSteering ) wheel.GameObject.LocalRotation = Rotation.FromYaw( Steer * maxAngle );
	}
}
