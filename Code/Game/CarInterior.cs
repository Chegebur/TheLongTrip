namespace LongTrip.Gameplay;

/// <summary>What the car session needs from whichever cabin a car has: the body to hang the camera on, where the driver's eyes are, and a way to hide the driver.</summary>
public interface ICarCabin
{
	/// <summary>The "Kit Body" object (null if the car fell back to the blockout body).</summary>
	GameObject Body { get; }

	/// <summary>Eye position in <see cref="Body"/>'s local frame, in metres.</summary>
	Vector3 EyeM { get; }

	void SetDriverVisible( bool visible );
}

/// <summary>
/// Fits the hatch with its cabin and makes it live: the steering wheel turns with the steering, the speedometer and
/// rev counter follow the car, and the stock driver model is moved onto the left seat (and hidden from the cockpit
/// camera, which sits in his head). Put on the car's root object.
/// </summary>
public sealed class CarInterior : Component, ICarCabin
{
	private const float M = 39.37f;

	public GameObject Body { get; private set; }
	public Vector3 EyeM => CarInteriorBuilder.EyeM;

	private VehicleController _car;
	private GameObject _cabin;
	private GameObject _wheel;
	private GameObject _speedNeedle;
	private GameObject _rpmNeedle;
	private GameObject _driver;
	private float _steerSmoothed;
	private float _speedSmoothed;
	private float _rpmSmoothed;
	private bool _driverVisible = true;

	/// <summary>Needle sweeps, in the same convention as the generated dial textures (clockwise from the bottom-left).</summary>
	private const float DialStart = 135f, DialSweep = 270f;

	protected override void OnStart()
	{
		_car = Components.Get<VehicleController>();
		Body = GameObject.Children.FirstOrDefault( c => c.Name == "Kit Body" );
		if ( !Body.IsValid() ) return; // blockout fallback: no cabin to fit

		_cabin = Mount( "Cabin", CarInteriorBuilder.BuildCabin(), Vector3.Zero, Rotation.Identity );

		// steering wheel: tilted towards the driver, spun about its own axis
		_wheel = Mount( "Steering Wheel", CarInteriorBuilder.BuildSteeringWheel(), CarInteriorBuilder.WheelCentreM * M, WheelTilt );

		var needle = CarInteriorBuilder.BuildNeedle( 0.070f );
		_speedNeedle = Mount( "Speedo Needle", needle, CarInteriorBuilder.SpeedoCentreM * M + new Vector3( 0, -0.004f * M, 0 ), Rotation.Identity );
		_rpmNeedle = Mount( "Rev Needle", needle, CarInteriorBuilder.TachoCentreM * M + new Vector3( 0, -0.004f * M, 0 ), Rotation.Identity );

		// A closed cabin gets almost no ambient light (and the ambient-occlusion effect darkens it further),
		// so give it a soft light of its own, like a dome light that is always on.
		var lightGo = new GameObject( true, "Cabin Light" );
		lightGo.Parent = Body;
		lightGo.LocalPosition = new Vector3( 0f, -0.10f, 1.22f ) * M;
		lightGo.Flags |= GameObjectFlags.NotSaved;
		var light = lightGo.AddComponent<PointLight>();
		light.LightColor = new Color( 1.0f, 0.95f, 0.86f );
		light.Radius = 105f;
		light.Intensity = 5f;
		light.Attenuation = 0.9f;
		light.Shadows = false;

		_driver = GameObject.Children.FirstOrDefault( c => c.Name == "Driver" );
		if ( _driver.IsValid() )
		{
			_driver.WorldPosition = Body.WorldTransform.PointToWorld( CarInteriorBuilder.DriverSeatM * M );
			_driver.WorldRotation = GameObject.WorldRotation;
		}
	}

	private static Rotation WheelTilt => Rotation.FromToRotation( Vector3.Up, CarInteriorBuilder.WheelNormal );

	private GameObject Mount( string name, Model model, Vector3 localPosition, Rotation localRotation )
	{
		var go = new GameObject( true, name );
		go.Parent = Body;
		go.LocalPosition = localPosition;
		go.LocalRotation = localRotation;
		go.Flags |= GameObjectFlags.NotSaved;
		if ( model.IsValid() )
			go.AddComponent<ModelRenderer>().Model = model;
		return go;
	}

	/// <summary>Show or hide the driver model. The cockpit camera hides him so you don't see inside your own head.</summary>
	public void SetDriverVisible( bool visible )
	{
		if ( visible == _driverVisible || !_driver.IsValid() ) return;
		_driverVisible = visible;

		foreach ( var r in _driver.Components.GetAll<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			r.RenderType = visible ? ModelRenderer.ShadowRenderType.On : ModelRenderer.ShadowRenderType.ShadowsOnly;
	}

	protected override void OnUpdate()
	{
		if ( !_car.IsValid() || !Body.IsValid() ) return;

		float k = 1f - MathF.Exp( -14f * Time.Delta );

		// ---- steering wheel (about 450 degrees lock to lock)
		_steerSmoothed = MathX.Lerp( _steerSmoothed, _car.Steer, k );
		if ( _wheel.IsValid() )
			_wheel.LocalRotation = WheelTilt * Rotation.FromAxis( Vector3.Up, -_steerSmoothed * 225f );

		// ---- gauges
		float kmh = _car.SpeedMs * 3.6f;
		float rpm = _car.Drivetrain?.Rpm ?? 0f;
		_speedSmoothed = MathX.Lerp( _speedSmoothed, kmh, k );
		_rpmSmoothed = MathX.Lerp( _rpmSmoothed, rpm, k );

		Point( _speedNeedle, Math.Clamp( _speedSmoothed / 180f, 0f, 1.04f ) );
		Point( _rpmNeedle, Math.Clamp( _rpmSmoothed / 8000f, 0f, 1.04f ) );
	}

	/// <summary>Aim a needle at a fraction (0..1) of the dial sweep. The dial faces -Y; image angles run clockwise from +X.</summary>
	private static void Point( GameObject needle, float fraction )
	{
		if ( !needle.IsValid() ) return;
		float a = (DialStart + DialSweep * fraction) * MathF.PI / 180f;
		var dir = new Vector3( MathF.Cos( a ), 0f, -MathF.Sin( a ) );   // (right, up) as seen by the driver
		needle.LocalRotation = Rotation.FromToRotation( Vector3.Forward, dir );
	}
}

/// <summary>
/// First-person camera in the driver's seat. Move the mouse to look around (head turn); after a moment it eases back
/// to looking down the road. Goes on the main camera; <see cref="CarSession"/> switches it with the chase camera.
/// </summary>
public sealed class CockpitCamera : Component
{
	private const float M = 39.37f;

	public GameObject Car { get; set; }
	public GameObject Body { get; set; }
	public VehicleController Controller { get; set; }

	/// <summary>The driver's eye in <see cref="Body"/>'s frame (metres). Set by the car session from the car's cabin.</summary>
	public Vector3 EyeM { get; set; } = CarInteriorBuilder.EyeM;

	[Property] public float BaseFov { get; set; } = 88f;
	[Property] public float RecenterDelay { get; set; } = 2.5f;

	private CameraComponent _camera;
	private float _yaw, _pitch;
	private TimeSince _lastLook = 999f;

	protected override void OnStart() => _camera = Components.Get<CameraComponent>();

	protected override void OnEnabled()
	{
		_yaw = _pitch = 0f;
		_lastLook = 999f;
	}

	protected override void OnUpdate()
	{
		if ( !Car.IsValid() || !Body.IsValid() ) return;

		Mouse.Visibility = MouseVisibility.Hidden;

		// while the mouse is turning a part or swinging a door the head stays where it is
		var look = PlayerGrabber.LookSuppressed ? default : Input.AnalogLook;
		if ( PlayerGrabber.LookSuppressed ) _lastLook = 0f;
		if ( MathF.Abs( look.yaw ) > 0.02f || MathF.Abs( look.pitch ) > 0.02f )
		{
			_lastLook = 0f;
			// you are a person sitting in a seat: you can turn all the way round and look straight up or straight down at your feet, and the head stays where you put it
			_yaw = (_yaw + look.yaw + 540f) % 360f - 180f;
			_pitch = Math.Clamp( _pitch + look.pitch, -89f, 89f );
		}

		WorldPosition = Body.WorldTransform.PointToWorld( EyeM * M );
		WorldRotation = Car.WorldRotation * Rotation.From( _pitch, _yaw, 0f );

		if ( _camera.IsValid() )
		{
			float speed = Controller.IsValid() ? Controller.SpeedMs : 0f;
			_camera.FieldOfView = MathX.Lerp( _camera.FieldOfView, BaseFov + Math.Clamp( speed / 40f, 0f, 1f ) * 12f, 1f - MathF.Exp( -6f * Time.Delta ) );
		}
	}
}
