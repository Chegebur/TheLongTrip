namespace LongTrip.Gameplay;

/// <summary>
/// Makes the old car's cabin live: the steering wheel turns with the steering, the stock driver model sits on the left
/// seat (and is hidden from the cockpit camera, which sits in his head), and a soft dome light keeps the closed cabin
/// from going black. The cabin itself (seats, dashboard, pedals...) is part of the car model. Put on the car's root.
/// </summary>
public sealed class OldCarInterior : Component, ICarCabin
{
	private const float M = 39.37f;

	public GameObject Body { get; private set; }
	public Vector3 EyeM => OldCarAssembler.EyeM;

	private VehicleController _car;
	private GameObject _wheel;
	private GameObject _driver;
	private float _steerSmoothed;
	private bool _driverVisible = true;

	protected override void OnStart()
	{
		_car = Components.Get<VehicleController>();
		Body = GameObject.Children.FirstOrDefault( c => c.Name == "Kit Body" );
		if ( !Body.IsValid() ) return;   // blockout fallback: nothing to fit

		_wheel = Body.Children.FirstOrDefault( c => c.Name == "steering" );

		var lightGo = new GameObject( true, "Cabin Light" );
		lightGo.Parent = Body;
		lightGo.LocalPosition = new Vector3( -0.10f, 0.0f, 1.30f ) * OldCarAssembler.SizeFactor * M;
		lightGo.Flags |= GameObjectFlags.NotSaved;
		var light = lightGo.AddComponent<PointLight>();
		light.LightColor = new Color( 1.0f, 0.95f, 0.86f );
		light.Radius = 105f * OldCarAssembler.SizeFactor;
		light.Intensity = 5f;
		light.Attenuation = 0.9f;
		light.Shadows = false;

		_driver = GameObject.Children.FirstOrDefault( c => c.Name == "Driver" );
		if ( _driver.IsValid() )
		{
			_driver.WorldPosition = Body.WorldTransform.PointToWorld( OldCarAssembler.PelvisM * M );
			_driver.WorldRotation = GameObject.WorldRotation;
		}
	}

	/// <summary>Show or hide the driver model. The cockpit camera hides him so you don't see inside your own head.</summary>
	public void SetDriverVisible( bool visible )
	{
		_driverVisible = visible;
		Apply( _driver, visible );
		Apply( Occupant, visible );
	}

	/// <summary>The player sitting in the car (set by the car session): hidden from the cockpit camera, which sits in his head.</summary>
	public GameObject Occupant { get; set; }

	private static void Apply( GameObject go, bool visible )
	{
		if ( !go.IsValid() ) return;
		foreach ( var r in go.Components.GetAll<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			r.RenderType = visible ? ModelRenderer.ShadowRenderType.On : ModelRenderer.ShadowRenderType.ShadowsOnly;
	}

	private TimeSince _sinceStart = 0f;
	private int _probes;

	/// <summary>Writes the rest state to the log at 2 s and 6 s after spawn, to see what the car is resting on.</summary>
	private void Probe()
	{
		var rb = Components.Get<Rigidbody>();
		var tr = Scene.Trace.Ray( WorldPosition + Vector3.Up * 20f, WorldPosition + Vector3.Down * 600f )
			.IgnoreGameObjectHierarchy( GameObject ).WithoutTags( "car" ).Run();
		float ground = tr.Hit ? tr.HitPosition.z : float.NaN;
		Log.Info( $"[oldcar] probe: root z {WorldPosition.z:F1}u, ground below {ground:F1}u, root is {(WorldPosition.z - ground):F1}u above it " +
			$"(expected {VehicleFactory.SeatHeightM( _car.Definition ) * Units.MetersToUnits:F1}u), velocity {rb?.Velocity.Length:F1}u/s, pitch/roll up.z {GameObject.WorldRotation.Up.z:F3}" );

		foreach ( var w in _car.Wheels )
			Log.Info( $"[oldcar]   {w.GameObject.Name}: z {w.WorldPosition.z:F1}u, suspension {w.SuspensionLength * Units.MetersToUnits:F1}u, {w.DebugTrace}" );

		foreach ( var c in GameObject.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
			Log.Info( $"[oldcar]   collider {c.GetType().Name} on '{c.GameObject.Name}' (enabled {c.Enabled})" );
	}

	protected override void OnUpdate()
	{
		if ( _probes < 2 && _sinceStart > (_probes == 0 ? 2f : 6f) && _car.IsValid() )
		{
			_probes++;
			Probe();
		}

		if ( !_car.IsValid() || !Body.IsValid() ) return;

		// the steering wheel is a detachable part: it exists only while it is on the car
		if ( !_wheel.IsValid() ) _wheel = Body.Children.FirstOrDefault( c => c.Name == "steering" );
		if ( !_wheel.IsValid() ) return;

		// about 450 degrees lock to lock; +Steer is a left turn, which is anticlockwise as the driver sees it
		_steerSmoothed = MathX.Lerp( _steerSmoothed, _car.Steer, 1f - MathF.Exp( -14f * Time.Delta ) );
		_wheel.LocalRotation = Rotation.FromAxis( OldCarAssembler.SteeringAxis, _steerSmoothed * 225f );
	}
}
