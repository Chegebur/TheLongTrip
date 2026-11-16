using LongTrip.World;

namespace LongTrip.Gameplay;

/// <summary>
/// Puts the base car (the vendored Vehicle Physics Kit hatch) on the road and lets the player get in and out.
/// Walk up to it and press Use (E). While driving the world streams around the car and the chase camera is on.
/// </summary>
public sealed class CarSession : Component
{
	/// <summary>How far down the road the car starts, in world units.</summary>
	[Property] public float SpawnDistance { get; set; } = 1100f;

	/// <summary>How close the player must be to get in, in world units (1 unit = 1 inch).</summary>
	[Property] public float EnterRange { get; set; } = 420f;

	/// <summary>You can only get out below this speed (m/s).</summary>
	[Property] public float MaxExitSpeed { get; set; } = 4f;

	public GameObject Car { get; private set; }
	public VehicleController Controller { get; private set; }
	public bool IsDriving { get; private set; }

	private WorldStreamer _world;
	private GameObject _playerObject;
	private CameraComponent _camera;
	private VehicleCamera _chase;
	private CockpitCamera _cockpit;
	private ICarCabin _interior;
	private CarAssembly _assembly;
	private TimeSince _lastWarning = 999f;
	private bool _grabberAdded;

	/// <summary>The seat the player is in: "seat_l" (driver) or "seat_r" (passenger).</summary>
	private string _seat = "seat_l";

	/// <summary>True while sitting in a seat that is not the driver's: you ride along and cannot drive.</summary>
	public bool IsPassenger => IsDriving && _seat != "seat_l";

	/// <summary>Driving starts in first person from the driver's seat. Press View (C) to swap to the chase camera.</summary>
	private bool _cockpitView = true;

	protected override void OnStart()
	{
		_world = Components.Get<WorldStreamer>() ?? Scene.GetAllComponents<WorldStreamer>().FirstOrDefault();
		_camera = Scene.Camera;
	}

	/// <summary>
	/// The player can be switched off (held by the world streamer until the ground exists, or hidden while
	/// driving), and a disabled object is invisible to <c>GetAllComponents</c>, so search every object.
	/// </summary>
	private GameObject FindPlayer()
	{
		foreach ( var go in Scene.GetAllObjects( false ) )
		{
			if ( go.Components.Get<PlayerController>( true ) is not null )
				return go;
		}
		return null;
	}

	protected override void OnUpdate()
	{
		if ( _world is null || !_world.IsReady ) return;
		if ( !_playerObject.IsValid() ) _playerObject = FindPlayer();

		// the player can pick up car parts (F) with stretchy arms
		if ( _playerObject.IsValid() && !_grabberAdded )
		{
			_playerObject.GetOrAddComponent<PlayerGrabber>();
			_grabberAdded = true;
		}

		if ( !Car.IsValid() )
		{
			TrySpawnCar();
			return;
		}

		if ( !IsDriving )
		{
			// A parked car must not react to the keys the player is pressing on foot.
			Controller.InputOverride = new DriveInputs { Handbrake = true };

			if ( _playerObject.IsValid() && Input.Pressed( "Use" ) && (_playerObject.WorldPosition - Car.WorldPosition).Length < EnterRange )
			{
				// you get in on whichever side of the car you are standing
				var local = Car.WorldTransform.PointToLocal( _playerObject.WorldPosition );
				Enter( local.y >= 0f ? "seat_l" : "seat_r" );
			}
		}
		else
		{
			// the car only does what its parts allow: no engine or radiator, no throttle; no steering wheel, no steering
			if ( _assembly is not null )
			{
				// only the driver drives; a passenger just rides (the handbrake lever still holds the car)
				var raw = IsPassenger ? new DriveInputs() : DriveInputs.SampleDeviceInputs();
				Controller.InputOverride = _assembly.Filter( raw );

				if ( Input.Keyboard.Pressed( "x" ) ) SwitchSeat();

				if ( !IsPassenger && MathF.Abs( raw.MoveForward ) > 0.1f && _assembly.WhatIsMissing() is { } problem && _lastWarning > 4f )
				{
					_lastWarning = 0f;
					PlayerGrabber.Instance?.Notify( problem );
				}
			}

			if ( Input.Pressed( "View" ) )
			{
				_cockpitView = !_cockpitView;
				ApplyView();
			}

			if ( Input.Pressed( "Use" ) && Controller.SpeedMs < MaxExitSpeed )
				Exit();
		}
	}

	/// <summary>Switch between the driver's-eye camera and the chase camera.</summary>
	private void ApplyView()
	{
		if ( _cockpit.IsValid() ) _cockpit.Enabled = _cockpitView;
		if ( _chase.IsValid() ) _chase.Enabled = !_cockpitView;
		_interior?.SetDriverVisible( !_cockpitView );
	}

	private void TrySpawnCar()
	{
		var gen = _world.Generator;
		float x = SpawnDistance;
		var dir = gen.RoadDirection( x );

		// In the right-hand lane, facing down the road.
		var pos = new Vector3( x, gen.RoadCenterY( x ) - 75f, gen.RoadHeight( x ) );
		if ( !_world.HasCollisionAt( pos ) ) return; // wait until the ground under it exists

		var def = CarDefinitions.OldSedan;
		pos.z += VehicleFactory.SeatHeightM( def ) * Units.MetersToUnits;

		Car = PartKitFactory.Spawn( Scene, def, pos, Rotation.LookAt( dir ) );
		if ( !Car.IsValid() ) return;

		Car.Name = "Base Car";
		Car.Flags |= GameObjectFlags.NotSaved;

		// the car is a moving base: things standing on it ride along, and it carries a local building grid (no buildings yet)
		Car.AddComponent<MovingBase>();
		Car.AddComponent<CarNetwork>();
		var grid = Car.AddComponent<BuildGrid>();
		grid.Anchor = Car.Children.FirstOrDefault( c => c.Name == "Kit Body" );
		Car.AddComponent<BuildSystem>();
		Controller = Car.Components.Get<VehicleController>();
		Controller.InputOverride = new DriveInputs { Handbrake = true };

		// the cabin, working gauges and a seated driver
		_interior = OldCarAssembler.IsOldCar( def ) ? Car.AddComponent<OldCarInterior>() : Car.AddComponent<CarInterior>();

		if ( OldCarAssembler.IsOldCar( def ) )
		{
			// the car starts as a bare shell on blocks; its parts lie nearby and have to be fitted
			_assembly = Car.AddComponent<CarAssembly>();
			_assembly.Init();
			SpawnLooseParts( gen, pos );
		}
	}

	/// <summary>
	/// Lay every detachable part of the car out in a grid on the roadside ahead of it, close together for now (proper spawn
	/// spots come later). They drop onto the ground and sit there until picked up.
	/// </summary>
	private void SpawnLooseParts( WorldGenerator gen, Vector3 carPosition )
	{
		// the parts the car is missing: four (universal) wheels, the engine, the radiator, the four doors, the hood and the
		// trunk lid. (The seats and the steering wheel are already in place, as if someone had fitted them.)
		string[] groups =
		{
			"wheel", "wheel", "wheel", "wheel", "engine", "radiator",
			"hood", "trunk", "door_fl", "door_fr", "door_rl", "door_rr",
		};
		const int columns = 6;
		const float spacing = 170f;

		float baseX = carPosition.x + 900f;
		var random = new Random( 4242 );
		for ( int i = 0; i < groups.Length; i++ )
		{
			float x = baseX + (i % columns) * spacing;
			float y = gen.RoadCenterY( x ) - 330f - (i / columns) * spacing;
			float ground = GroundGuard.SurfaceHeight( gen, x, y );

			var size = OldCarAssembler.GroupSize( groups[i] );
			var position = new Vector3( x, y, ground + MathF.Max( size.x, MathF.Max( size.y, size.z ) ) * 0.5f + 12f );
			CarPartItem.Spawn( Scene, groups[i], position, Rotation.FromYaw( random.Next( 360 ) ) );
		}

		// and the loot from the item data file (food, drink, fuel, weapons, barrels...), in rows beside the parts
		ItemLibrary.Reset();
		int n = 0;
		foreach ( var (id, count) in ItemLibrary.Loot )
		{
			for ( int c = 0; c < count; c++, n++ )
			{
				float x = baseX + (n % 9) * 75f;
				float y = gen.RoadCenterY( x ) - 330f + 190f + (n / 9) * 75f;
				float ground = GroundGuard.SurfaceHeight( gen, x, y );
				ItemLibrary.Spawn( Scene, id, new Vector3( x, y, ground + 40f ), Rotation.FromYaw( random.Next( 360 ) ) );
			}
		}
	}

	private PlayerController _seatedController;
	private Rigidbody _seatedBody;
	private readonly List<Collider> _seatedColliders = new();

	/// <summary>
	/// Put the player's own character in the driver's seat. They stay in the world (nothing is switched off): the character
	/// controller just stops walking, its body is carried by the car, and it sits. So the player can still look at parts, swing
	/// doors and the handbrake lever, pick things up and take parts off or put them on while driving.
	/// </summary>
	private void SeatPlayer()
	{
		PlaceInSeat( _seat );
		SetSitting( true );
		if ( _interior is OldCarInterior old ) old.Occupant = _playerObject;
	}

	private void PlaceInSeat( string seat )
	{
		_seat = seat;
		var kitBody = Car.Children.FirstOrDefault( c => c.Name == "Kit Body" );
		_seatedController = _playerObject.Components.Get<PlayerController>( true );
		_seatedBody = _playerObject.Components.Get<Rigidbody>( true );

		// the controller (and the capsule it makes) is switched off while seated, and comes back, rebuilt, when the player gets out
		if ( _seatedController.IsValid() ) _seatedController.Enabled = false;
		// while seated the character is just a child of the car: no physics body or collision of its own, so nothing pushes it off the seat
		if ( _seatedBody.IsValid() ) { _seatedBody.Velocity = Vector3.Zero; _seatedBody.MotionEnabled = false; _seatedBody.Enabled = false; }
		_seatedColliders.Clear();
		foreach ( var c in _playerObject.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
			if ( c.Enabled ) { _seatedColliders.Add( c ); c.Enabled = false; }

		if ( kitBody.IsValid() )
		{
			_playerObject.SetParent( kitBody, false );
			_playerObject.LocalPosition = (OldCarAssembler.PelvisFor( seat ) + OldCarAssembler.SitOffsetM) * 39.37f;
			_playerObject.LocalRotation = Rotation.Identity;
		}
	}

	/// <summary>Slide over to the other front seat without getting out (the seat has to be fitted).</summary>
	private void SwitchSeat()
	{
		string other = _seat == "seat_l" ? "seat_r" : "seat_l";
		if ( _assembly is not null && !_assembly.HasSeat( other ) ) { PlayerGrabber.Instance?.Notify( "There is no seat there" ); return; }

		PlaceInSeat( other );
		if ( _cockpit.IsValid() && _interior is not null ) _cockpit.EyeM = EyeForSeat();
		PlayerGrabber.Instance?.Notify( other == "seat_l" ? "Driver's seat" : "Passenger seat" );
	}

	private Vector3 EyeForSeat() => _interior is OldCarInterior ? OldCarAssembler.EyeFor( _seat ) : _interior.EyeM;

	private void SetSitting( bool sitting )
	{
		var renderer = _seatedController.IsValid() ? _seatedController.Renderer : null;
		renderer ??= _playerObject.Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		if ( !renderer.IsValid() ) return;

		renderer.Set( "b_grounded", true );
		renderer.Set( "sit", sitting ? 1 : 0 );
		renderer.Set( "sit_offset_height", sitting ? 4f : 0f );
	}

	private void Enter( string seat )
	{
		_seat = seat;

		// you get in through the door on that side, which has to be open (or gone), and the seat has to be there
		if ( _assembly is not null && !_assembly.HasSeat( seat ) )
		{
			PlayerGrabber.Instance?.Notify( seat == "seat_l" ? "The driver's seat is missing" : "The passenger seat is missing" );
			return;
		}
		if ( _assembly is not null && !_assembly.CanEnter( OldCarAssembler.DoorFor( seat ) ) )
		{
			PlayerGrabber.Instance?.Notify( seat == "seat_l" ? "Open the driver's door first" : "Open the passenger door first" );
			return;
		}

		IsDriving = true;
		if ( seat == "seat_l" ) Car.Components.Get<CarNetwork>()?.TakeControl();   // the driver simulates the car
		if ( _assembly is not null ) _assembly.Occupied = true;
		SeatPlayer();
		Controller.InputOverride = null; // live keyboard / gamepad (filtered by the car's parts every frame)

		_camera = Scene.Camera ?? _camera;
		_chase = _camera.GameObject.GetOrAddComponent<VehicleCamera>();
		_chase.Target = Controller;
		if ( Controller.Definition is { } def && OldCarAssembler.IsOldCar( def ) )
		{
			// a bigger car needs the chase camera pulled back and raised by the same factor
			float k = OldCarAssembler.SizeFactor;
			_chase.Distance = 6.5f * k;
			_chase.Height = 2.2f * k;
			_chase.MinDistance = 3f * k;
			_chase.MaxDistance = 14f * k;
		}

		_cockpit = _camera.GameObject.GetOrAddComponent<CockpitCamera>();
		_cockpit.Car = Car;
		_cockpit.Body = _interior?.Body;
		if ( _interior is not null ) _cockpit.EyeM = EyeForSeat();
		_cockpit.Controller = Controller;

		_camera.ZNear = 4f; // the dashboard is close to the eye
		_cockpitView = _cockpit.Body.IsValid();   // no cabin (blockout fallback) means chase view
		ApplyView();

		_world.Target = Car;
	}

	private void Exit()
	{
		IsDriving = false;
		Controller.InputOverride = new DriveInputs { Handbrake = true };
		Car.Components.Get<CarNetwork>()?.ReleaseControl();

		if ( _chase.IsValid() ) _chase.Enabled = false;
		if ( _cockpit.IsValid() ) _cockpit.Enabled = false;
		_interior?.SetDriverVisible( true );
		if ( _camera.IsValid() ) _camera.ZNear = 8f;
		Mouse.Visibility = MouseVisibility.Auto;

		// The driver's door swings open, the handbrake goes on, and the player steps out on the driver's side.
		if ( _assembly is not null ) _assembly.Occupied = false;
		_assembly?.OpenDoor( OldCarAssembler.DoorFor( _seat ) );
		_assembly?.SetHandbrake( true );

		var side = _seat == "seat_r" ? Car.WorldRotation.Right : Car.WorldRotation.Left;
		_playerObject.SetParent( null, true );
		_playerObject.WorldPosition = Car.WorldPosition + side * 150f + Vector3.Up * 40f;
		_playerObject.WorldRotation = Rotation.FromYaw( Car.WorldRotation.Angles().yaw );
		if ( _seatedBody.IsValid() ) { _seatedBody.Enabled = true; _seatedBody.MotionEnabled = true; _seatedBody.Velocity = Vector3.Zero; }
		foreach ( var c in _seatedColliders ) if ( c.IsValid() ) c.Enabled = true;
		_seatedColliders.Clear();
		SetSitting( false );
		if ( _seatedController.IsValid() )
		{
			_seatedController.Enabled = true;
			_seatedController.EyeAngles = new Angles( 0f, Car.WorldRotation.Angles().yaw, 0f );
		}
		_seat = "seat_l";
		if ( _interior is OldCarInterior old ) old.Occupant = null;

		_world.Target = null; // back to following the player
	}
}
