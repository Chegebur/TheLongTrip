namespace LongTrip.Gameplay;

/// <summary>
/// Which parts are on the car, and what that means for driving. The car is a body shell with a socket for every part. A part you
/// hold near its socket shows a green ghost there and snaps on when you let go; a part you grab while it is on the car comes off
/// as a loose item. All four wheel sockets take the same wheel.
/// <list type="bullet">
/// <item>Each physics wheel only exists while a wheel is fitted to it: with no wheels the car falls onto its floor.</item>
/// <item>It only runs with an engine and a radiator; the steering wheel has to be on to steer.</item>
/// <item>The handbrake locks the rear wheels (drift while moving) and holds the car still once it has stopped.</item>
/// </list>
/// </summary>
public sealed class CarAssembly : Component
{
	public sealed class Socket
	{
		public string Group;
		public string Display;
		public Vector3 HomeLocal;     // in the "Kit Body" frame (world units)
		public Vector3 SizeLocal;
		public float SnapRadius;
		public GameObject Attached;   // the visuals on the car, null while the part is off
		public bool IsAttached => Attached.IsValid();

		/// <summary>The point the attached container turns about, in the "Kit Body" frame (a door's hinge, the steering hub...).</summary>
		public Vector3 PivotLocal;

		/// <summary>Doors, the hood and the trunk swing: <see cref="Angle"/> eases towards <see cref="TargetAngle"/> (signed degrees).</summary>
		public bool IsHinged;
		public Vector3 Axis;
		public float MaxAngle;
		public float Angle;
		public float TargetAngle;
	}

	/// <summary>How far (world units) from its socket a part still snaps, as a share of its biggest dimension (plus a floor).</summary>
	private const float SnapShare = 0.6f;
	private const float SnapMin = 36f;

	/// <summary>How far a part may be turned from its fitted orientation and still snap, in degrees.</summary>
	private const float SnapAngle = 55f;

	/// <summary>The car leaves the world with these already on it; everything else has to be fitted.</summary>
	// (properties, not static fields: a static field keeps its old contents across an editor hot-reload)
	private static string[] StartsFitted => new[]
	{
		"bumper_f", "bumper_r", "headlight_l", "headlight_r", "taillight_l", "taillight_r", "windshield", "rear_window",
		"seat_l", "seat_r", "steering", "handbrake",   // as if someone had already put them in
	};

	private static string[] WheelSockets => new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };

	/// <summary>How big (world units, at the model's natural size) a part must at least be to look at: the handbrake lever is thin.</summary>
	private const float MinLookSize = 0.2f * 39.37f;

	public Dictionary<string, Socket> Sockets { get; } = new();

	private GameObject _body;
	private Rigidbody _rb;
	private VehicleController _controller;
	private EngineAudio _engineAudio;
	private GameObject _ghost;
	private string _ghostGroup;
	private Material _ghostMaterial;
	private bool _parked;

	/// <summary>The wheel settings the car was built with, so a wheel can be switched off and on again.</summary>
	private readonly (float Radius, float Travel, bool Driven, bool Handbrake)[] _wheelStock = new (float, float, bool, bool)[4];

	public GameObject Body => _body;

	public bool HasEngine => Has( "engine" );
	public bool HasRadiator => Has( "radiator" );
	public bool HasSteeringWheel => Has( "steering" );
	public int WheelsOn => WheelSockets.Count( Has );

	/// <summary>The fuel tank holds this many litres.</summary>
	public const float TankCapacity = 60f;

	/// <summary>Litres in the tank. The car starts with a little, and burns it while the throttle is down.</summary>
	public float Fuel { get; private set; } = 15f;

	public bool HasFuel => Fuel > 0.01f;

	/// <summary>True while the player is sitting in the car (the HUD shows the fuel gauge).</summary>
	public bool Occupied { get; set; }

	/// <summary>Pour fuel in. Returns how many litres actually went in (the tank can be full).</summary>
	public float AddFuel( float litres )
	{
		float accepted = MathF.Max( 0f, MathF.Min( litres, TankCapacity - Fuel ) );
		Fuel += accepted;
		return accepted;
	}

	/// <summary>The engine needs its radiator and some fuel; it also needs a driven wheel on the road to go anywhere.</summary>
	public bool CanRun => HasEngine && HasRadiator && HasFuel;

	/// <summary>What is stopping the car from running, in words, or null when nothing is.</summary>
	public string WhatIsMissing()
	{
		var missing = new List<string>();
		if ( !HasEngine ) missing.Add( "an engine" );
		if ( !HasRadiator ) missing.Add( "a radiator" );
		if ( !HasFuel ) missing.Add( "fuel" );
		if ( WheelsOn == 0 ) missing.Add( "wheels" );
		return missing.Count == 0 ? null : "The car needs " + string.Join( ", ", missing );
	}

	private bool Has( string group ) => Sockets.TryGetValue( group, out var s ) && s.IsAttached;

	/// <summary>Measure the sockets from the imported models and fit the parts the car starts with. Call once, right after the car is spawned.</summary>
	public void Init()
	{
		_body = GameObject.Children.FirstOrDefault( c => c.Name == "Kit Body" );
		_rb = Components.Get<Rigidbody>();
		_controller = Components.Get<VehicleController>();
		_engineAudio = Components.Get<EngineAudio>();
		_ghostMaterial = Material.Load( "materials/vehicles/ghost.vmat" );

		for ( int i = 0; i < 4 && _controller is not null && i < _controller.Wheels.Count; i++ )
		{
			var w = _controller.Wheels[i];
			_wheelStock[i] = (w.Radius, w.SuspensionTravel, w.IsDriven, w.HasHandbrake);
		}

		foreach ( var group in OldCarAssembler.GroupNames )
		{
			var size = OldCarAssembler.GroupSize( group );
			Sockets[group] = new Socket
			{
				Group = group,
				Display = OldCarAssembler.InfoFor( group ).Display,
				HomeLocal = OldCarAssembler.HomeLocal( group ),
				SizeLocal = size,
				SnapRadius = MathF.Max( SnapMin, MathF.Max( size.x, MathF.Max( size.y, size.z ) ) * SnapShare ),
				PivotLocal = OldCarAssembler.PivotLocal( group ),
			};

			if ( OldCarAssembler.TryGetHinge( group, out _, out var axis, out var maxAngle ) )
			{
				var s = Sockets[group];
				s.IsHinged = true;
				s.Axis = axis;
				s.MaxAngle = maxAngle;
			}
		}

		// with no wheel fitted a physics wheel does nothing: no suspension holding the body up, no drive, no grip
		for ( int i = 0; i < 4; i++ ) SetWheelActive( i, false );

		foreach ( var group in StartsFitted )
		{
			if ( !Sockets.TryGetValue( group, out var s ) ) continue;
			s.Attached = OldCarAssembler.BuildAttached( Scene, GameObject, group );
		}

		// the handbrake starts pulled on
		SetHandbrake( true, instant: true );

		UpdateEngineSound();
	}

	// ---- handbrake lever ------------------------------------------------------------------------------------

	/// <summary>The lever counts as pulled once it is past about 45% of its travel.</summary>
	public bool HandbrakeOn => Sockets.TryGetValue( "handbrake", out var s ) && s.IsAttached && s.MaxAngle != 0f && s.TargetAngle / s.MaxAngle > 0.45f;

	/// <summary>Pull the handbrake lever on or push it off (it eases there, unless <paramref name="instant"/>).</summary>
	public void SetHandbrake( bool on, bool instant = false )
	{
		if ( !Sockets.TryGetValue( "handbrake", out var s ) || !s.IsAttached ) return;
		s.TargetAngle = on ? s.MaxAngle : 0f;
		if ( !instant ) return;
		s.Angle = s.TargetAngle;
		s.Attached.LocalRotation = Rotation.FromAxis( s.Axis, s.Angle );
	}

	/// <summary>You get in through the driver's door: it has to be off or swung well open.</summary>
	public bool CanEnter()
	{
		if ( !Sockets.TryGetValue( "door_fl", out var door ) || !door.IsAttached ) return true;
		return door.MaxAngle != 0f && door.TargetAngle / door.MaxAngle > 0.3f;
	}

	/// <summary>Swing the driver's door open (when getting out).</summary>
	public void OpenDriverDoor()
	{
		if ( Sockets.TryGetValue( "door_fl", out var door ) && door.IsAttached ) door.TargetAngle = door.MaxAngle * 0.85f;
	}

	/// <summary>Switch one of the four physics wheels on (a wheel is fitted) or off (nothing there to carry the car).</summary>
	private void SetWheelActive( int index, bool active )
	{
		if ( _controller is null || index >= _controller.Wheels.Count ) return;
		var w = _controller.Wheels[index];
		var stock = _wheelStock[index];

		w.Radius = active ? stock.Radius : 0.01f;
		w.SuspensionTravel = active ? stock.Travel : 0f;   // together these give the ground trace no length: it never touches anything
		w.IsDriven = active && stock.Driven;
		w.HasHandbrake = active && stock.Handbrake;
	}

	private void UpdateEngineSound()
	{
		if ( _engineAudio.IsValid() ) _engineAudio.Enabled = CanRun;
	}

	protected override void OnFixedUpdate()
	{
		if ( Sockets.Count == 0 || !_controller.IsValid() || !_rb.IsValid() ) return;

		// Parking brake: with the handbrake on, a stopped car on its wheels stays put instead of creeping or rolling off a slope.
		// (It is not held while the player is on the throttle, so a handbrake turn still starts.)
		bool onWheels = _controller.Wheels.Any( w => w.IsGrounded );
		bool park = _controller.Handbrake && onWheels && _controller.SpeedMs < 0.6f && _controller.Throttle < 0.05f;
		// the engine burns fuel while the throttle is down
		if ( CanRun && _controller.Throttle > 0.05f )
			Fuel = MathF.Max( 0f, Fuel - (0.004f + 0.035f * _controller.Throttle) * Time.Delta );

		if ( _engineAudio.IsValid() && _engineAudio.Enabled != CanRun ) _engineAudio.Enabled = CanRun;   // the engine dies when the tank runs dry

		if ( park != _parked )
		{
			_parked = park;
			if ( park ) { _rb.Velocity = Vector3.Zero; _rb.AngularVelocity = Vector3.Zero; }
			_rb.MotionEnabled = !park;
		}
	}

	protected override void OnUpdate()
	{
		// doors, hood and trunk ease towards where the player is dragging them, so they swing smoothly
		float k = 1f - MathF.Exp( -16f * Time.Delta );
		foreach ( var s in Sockets.Values )
		{
			if ( !s.IsHinged || !s.IsAttached ) continue;
			if ( MathF.Abs( s.TargetAngle - s.Angle ) < 0.01f ) continue;
			s.Angle = MathX.Lerp( s.Angle, s.TargetAngle, k );
			s.Attached.LocalRotation = Rotation.FromAxis( s.Axis, s.Angle );
		}
	}

	// ---- what the driver can do -----------------------------------------------------------------------------

	/// <summary>The player's inputs as the car allows them: no engine or radiator means no throttle, no steering wheel means no steering.</summary>
	public DriveInputs Filter( DriveInputs raw )
	{
		if ( !CanRun ) raw.MoveForward = 0f;
		if ( !HasSteeringWheel ) raw.Steer = 0f;
		raw.Handbrake |= HandbrakeOn;   // the lever you pulled stays on until you push it back
		raw.ShiftUp = raw.ShiftDown = false;
		return raw;
	}

	// ---- swinging parts ------------------------------------------------------------------------------------

	/// <summary>Where the swinging edge of a door, hood or trunk is (the point you grab), in the container's frame.</summary>
	private Vector3 HandleInContainer( Socket s )
	{
		// the handbrake lever is held at its top
		if ( s.Group == "handbrake" ) return s.HomeLocal - s.PivotLocal + new Vector3( 0f, 0f, s.SizeLocal.z * 0.5f );

		float half = s.SizeLocal.x * 0.5f;
		float edge = s.Group == "hood" ? half : -half;   // a door's and the trunk's free edge is at the back, the hood's at the front
		return s.HomeLocal - s.PivotLocal + new Vector3( edge, 0f, 0f );
	}

	public Vector3 PivotWorld( Socket s ) => s.Attached.WorldPosition;
	public Vector3 AxisWorld( Socket s ) => s.Attached.Parent.WorldRotation * s.Axis;
	public Vector3 HandleWorld( Socket s ) => s.Attached.WorldTransform.PointToWorld( HandleInContainer( s ) );

	/// <summary>Turn a hinged part by a number of degrees, within its range.</summary>
	public void SwingBy( Socket s, float degrees )
	{
		if ( !s.IsHinged || !s.IsAttached ) return;
		float lo = MathF.Min( 0f, s.MaxAngle ), hi = MathF.Max( 0f, s.MaxAngle );
		s.TargetAngle = Math.Clamp( s.TargetAngle + degrees, lo, hi );
	}

	public Vector3 SocketWorld( Socket s ) => _body.WorldTransform.PointToWorld( s.HomeLocal );

	// ---- snapping -----------------------------------------------------------------------------------------

	/// <summary>Can this held part go on the car right now? True when it's close to a free socket it fits and turned about the right way up.</summary>
	public bool TrySnap( CarPartItem item, out Socket socket )
	{
		socket = null;
		if ( !_body.IsValid() ) return false;

		float best = float.MaxValue;
		foreach ( var s in Sockets.Values )
		{
			if ( s.IsAttached || OldCarAssembler.ItemGroupFor( s.Group ) != item.Group ) continue;

			float distance = (item.WorldPosition - SocketWorld( s )).Length;
			if ( distance > s.SnapRadius || distance >= best ) continue;

			// Orientation: the part is modelled in the car's frame, so it fits when its axes line up with the car's.
			// Wheels are round, so they may spin about their axle (and are the same either way round): only the axle counts.
			var carRot = _body.WorldRotation;
			var itemRot = item.WorldRotation;
			float angle;
			if ( s.Group.StartsWith( "wheel_" ) )
			{
				float d = MathF.Abs( Vector3.Dot( itemRot.Left, carRot.Left ) );
				angle = MathF.Acos( Math.Clamp( d, 0f, 1f ) ) * 180f / MathF.PI;
			}
			else
			{
				var delta = carRot.Inverse * itemRot;
				angle = 2f * MathF.Acos( Math.Clamp( MathF.Abs( delta.w ), 0f, 1f ) ) * 180f / MathF.PI;
			}

			if ( angle > SnapAngle ) continue;
			best = distance;
			socket = s;
		}

		return socket is not null;
	}

	/// <summary>Show a translucent green copy of the part on its socket, or hide it with null.</summary>
	public void ShowGhost( Socket socket )
	{
		if ( socket is null ) { HideGhost(); return; }
		if ( _ghost.IsValid() && _ghostGroup == socket.Group ) return;

		HideGhost();
		bool wheel = socket.Group.StartsWith( "wheel_" );
		var geometry = OldCarAssembler.GetGeometry( wheel ? "wheel_fl" : socket.Group.StartsWith( "seat_" ) ? "seat_l" : socket.Group );
		if ( geometry is null || !_body.IsValid() ) return;

		_ghost = Scene.CreateObject();
		_ghost.Name = "Ghost " + socket.Group;
		_ghost.SetParent( _body, false );
		_ghost.Flags |= GameObjectFlags.NotSaved;
		_ghostGroup = socket.Group;

		// a wheel's model is the front left one: move it to this socket's hub, turned round on the right-hand side;
		// a seat's model is the left seat: moved across to this seat's place
		var shift = Vector3.Zero;
		if ( wheel )
		{
			_ghost.LocalPosition = OldCarAssembler.HubLocal( socket.Group );
			_ghost.LocalRotation = OldCarAssembler.IsRightSide( socket.Group ) ? Rotation.FromYaw( 180f ) : Rotation.Identity;
			shift = -OldCarAssembler.HubLocal( "wheel_fl" );
		}
		else if ( socket.Group.StartsWith( "seat_" ) )
		{
			shift = OldCarAssembler.SeatShift( socket.Group );
		}

		for ( int i = 0; i < geometry.Models.Length; i++ )
		{
			var go = Scene.CreateObject();
			go.SetParent( _ghost, false );
			go.LocalPosition = shift;
			go.LocalScale = Vector3.One * OldCarAssembler.Scale;
			var renderer = go.Components.Create<ModelRenderer>();
			renderer.Model = geometry.Models[i];
			if ( _ghostMaterial is not null ) renderer.MaterialOverride = _ghostMaterial;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}
	}

	public void HideGhost()
	{
		if ( _ghost.IsValid() ) _ghost.Destroy();
		_ghost = null;
		_ghostGroup = null;
	}

	// ---- attach / detach ----------------------------------------------------------------------------------

	/// <summary>Put a held part on the car: its loose item is removed and the visuals appear on the socket.</summary>
	public bool Attach( CarPartItem item, Socket s )
	{
		if ( s is null || s.IsAttached || OldCarAssembler.ItemGroupFor( s.Group ) != item.Group ) return false;

		s.Attached = OldCarAssembler.BuildAttached( Scene, GameObject, s.Group );
		if ( !s.IsAttached ) return false;
		s.Angle = s.TargetAngle = 0f;   // a part is fitted closed

		int wheel = Array.IndexOf( WheelSockets, s.Group );
		if ( wheel >= 0 ) SetWheelActive( wheel, true );
		UpdateEngineSound();

		HideGhost();
		item.Unweld();
		item.GameObject.Destroy();
		Log.Info( $"[assembly] {s.Display} attached ({WheelsOn}/4 wheels, engine {HasEngine}, radiator {HasRadiator})" );
		return true;
	}

	/// <summary>Take a part off the car. It becomes a loose item where it was, which is returned (the grabber picks it up).</summary>
	public CarPartItem Detach( string group )
	{
		if ( !Sockets.TryGetValue( group, out var s ) || !s.IsAttached ) return null;

		// the loose part appears exactly where the attached one is, swung open or not
		var worldPos = s.Attached.WorldTransform.PointToWorld( s.HomeLocal - s.PivotLocal );
		var worldRot = s.Attached.WorldRotation;
		bool wheel = group.StartsWith( "wheel_" );
		if ( wheel )
		{
			worldPos = s.Attached.WorldPosition;   // the wheel's hub
			if ( OldCarAssembler.IsRightSide( group ) ) worldRot *= Rotation.FromYaw( 180f );   // the model on this side was turned round
		}

		s.Attached.Destroy();
		s.Attached = null;
		s.Angle = s.TargetAngle = 0f;

		int index = Array.IndexOf( WheelSockets, group );
		if ( index >= 0 ) SetWheelActive( index, false );
		UpdateEngineSound();

		var item = CarPartItem.Spawn( Scene, OldCarAssembler.ItemGroupFor( group ), worldPos, worldRot );
		Log.Info( $"[assembly] {s.Display} removed ({WheelsOn}/4 wheels, engine {HasEngine}, radiator {HasRadiator})" );
		return item;
	}

	// ---- picking parts that are on the car ---------------------------------------------------------------

	/// <summary>The nearest attached part a ray passes through (their boxes in the car's frame), for grabbing a part off the car.</summary>
	public bool RaycastAttached( Vector3 origin, Vector3 direction, float maxDistance, out Socket hit, out float distance )
	{
		hit = null;
		distance = maxDistance;
		if ( !_body.IsValid() ) return false;

		foreach ( var s in Sockets.Values )
		{
			if ( !s.IsAttached ) continue;

			// test in the part's own frame, so a swung-open door can still be looked at
			var local = s.Attached.WorldTransform;
			var o = local.PointToLocal( origin );
			var d = local.NormalToLocal( direction );

			// small parts (the handbrake lever) get a bigger box to look at, so they can actually be aimed at
			float minLook = MinLookSize * OldCarAssembler.Scale;
			var half = new Vector3( MathF.Max( s.SizeLocal.x, minLook ), MathF.Max( s.SizeLocal.y, minLook ), MathF.Max( s.SizeLocal.z, minLook ) ) * 0.5f;
			var rel = o - (s.HomeLocal - s.PivotLocal);
			float tMin = 0f, tMax = distance;
			bool ok = true;
			for ( int axis = 0; axis < 3 && ok; axis++ )
			{
				float oa = rel[axis], da = d[axis], h = half[axis];
				if ( MathF.Abs( da ) < 1e-6f )
				{
					if ( MathF.Abs( oa ) > h ) ok = false;
					continue;
				}
				float t1 = (-h - oa) / da, t2 = (h - oa) / da;
				if ( t1 > t2 ) (t1, t2) = (t2, t1);
				tMin = MathF.Max( tMin, t1 );
				tMax = MathF.Min( tMax, t2 );
				if ( tMin > tMax ) ok = false;
			}

			if ( ok && tMin < distance )
			{
				distance = tMin;
				hit = s;
			}
		}

		return hit is not null;
	}
}
