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
				Enter();
			}
		}
		else
		{
			// the car only does what its parts allow: no engine or radiator, no throttle; no steering wheel, no steering
			if ( _assembly is not null )
			{
				var raw = DriveInputs.SampleDeviceInputs();
				Controller.InputOverride = _assembly.Filter( raw );

				if ( MathF.Abs( raw.MoveForward ) > 0.1f && _assembly.WhatIsMissing() is { } problem && _lastWarning > 4f )
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

		// and a few things to eat, drink and fill the tank with, in a row beside the parts
		string[] supplies = { "fuel_can", "fuel_can", "water_bottle", "water_bottle", "water_bottle", "canned_food", "canned_food", "canned_food" };
		ItemLibrary.Reset();
		for ( int i = 0; i < supplies.Length; i++ )
		{
			float x = baseX + i * 60f;
			float y = gen.RoadCenterY( x ) - 330f + 190f;
			float ground = GroundGuard.SurfaceHeight( gen, x, y );
			ItemLibrary.Spawn( Scene, supplies[i], new Vector3( x, y, ground + 30f ), Rotation.FromYaw( random.Next( 360 ) ), supplies[i] == "water_bottle" && i == 3 ? 0.5f : 1f );
		}
	}

	private PlayerController _seatedController;
	private Rigidbody _seatedBody;

	/// <summary>
	/// Put the player's own character in the driver's seat. They stay in the world (nothing is switched off): the character
	/// controller just stops walking, its body is carried by the car, and it sits. So the player can still look at parts, swing
	/// doors and the handbrake lever, pick things up and take parts off or put them on while driving.
	/// </summary>
	private void SeatPlayer()
	{
		var kitBody = Car.Children.FirstOrDefault( c => c.Name == "Kit Body" );
		_seatedController = _playerObject.Components.Get<PlayerController>( true );
		_seatedBody = _playerObject.Components.Get<Rigidbody>( true );

		// the controller (and the capsule it makes) is switched off while seated, and comes back, rebuilt, when the player gets out
		if ( _seatedController.IsValid() ) _seatedController.Enabled = false;
		if ( _seatedBody.IsValid() ) { _seatedBody.Velocity = Vector3.Zero; _seatedBody.MotionEnabled = false; }

		if ( kitBody.IsValid() )
		{
			_playerObject.SetParent( kitBody, false );
			_playerObject.LocalPosition = OldCarAssembler.PelvisM * 39.37f;
			_playerObject.LocalRotation = Rotation.Identity;
		}

		SetSitting( true );
		if ( _interior is OldCarInterior old ) old.Occupant = _playerObject;
	}

	private void SetSitting( bool sitting )
	{
		var renderer = _seatedController.IsValid() ? _seatedController.Renderer : null;
		renderer ??= _playerObject.Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		if ( !renderer.IsValid() ) return;

		renderer.Set( "b_grounded", true );
		renderer.Set( "sit", sitting ? 1 : 0 );
		renderer.Set( "sit_offset_height", sitting ? 4f : 0f );
	}

	private void Enter()
	{
		// you get in through the driver's door, which has to be open (or gone)
		if ( _assembly is not null && !_assembly.CanEnter() )
		{
			PlayerGrabber.Instance?.Notify( "Open the driver's door first" );
			return;
		}

		IsDriving = true;
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
		if ( _interior is not null ) _cockpit.EyeM = _interior.EyeM;
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

		if ( _chase.IsValid() ) _chase.Enabled = false;
		if ( _cockpit.IsValid() ) _cockpit.Enabled = false;
		_interior?.SetDriverVisible( true );
		if ( _camera.IsValid() ) _camera.ZNear = 8f;
		Mouse.Visibility = MouseVisibility.Auto;

		// The driver's door swings open, the handbrake goes on, and the player steps out on the driver's side.
		if ( _assembly is not null ) _assembly.Occupied = false;
		_assembly?.OpenDriverDoor();
		_assembly?.SetHandbrake( true );

		var side = Car.WorldRotation.Left;
		_playerObject.SetParent( null, true );
		_playerObject.WorldPosition = Car.WorldPosition + side * 150f + Vector3.Up * 40f;
		_playerObject.WorldRotation = Rotation.FromYaw( Car.WorldRotation.Angles().yaw );
		if ( _seatedBody.IsValid() ) { _seatedBody.MotionEnabled = true; _seatedBody.Velocity = Vector3.Zero; }
		SetSitting( false );
		if ( _seatedController.IsValid() )
		{
			_seatedController.Enabled = true;
			_seatedController.EyeAngles = new Angles( 0f, Car.WorldRotation.Angles().yaw, 0f );
		}
		if ( _interior is OldCarInterior old ) old.Occupant = null;

		_world.Target = null; // back to following the player
	}
}
