namespace LongTrip.Gameplay;

/// <summary>
/// Picking things up, The Long Drive style. Look at a car part (or a part that is on the car) and press F to grab it; your arms
/// stretch out to it. While you carry it: hold the right mouse button and move the mouse to turn it, the wheel pushes it away or
/// pulls it close, the left button throws it, and F lets go (or fits it onto the car when its green ghost is showing).
/// Goes on the player object.
/// </summary>
public sealed class PlayerGrabber : Component
{
	/// <summary>How far you can reach to pick something up (world units): long, because the arms stretch.</summary>
	[Property] public float ReachDistance { get; set; } = 270f;

	[Property] public float MinHoldDistance { get; set; } = 70f;
	[Property] public float MaxHoldDistance { get; set; } = 240f;

	/// <summary>Degrees of turn per pixel of mouse movement while the right button is held.</summary>
	[Property] public float RotateSpeed { get; set; } = 0.35f;

	/// <summary>The latest grabber, so other parts of the game can show a message on the HUD.</summary>
	public static PlayerGrabber Instance { get; private set; }

	/// <summary>True while the mouse is busy turning a part or swinging a door: cameras that read the mouse (the cockpit view) leave it alone.</summary>
	public static bool LookSuppressed { get; private set; }

	public CarPartItem Held => _held;

	private CarPartItem _held;
	private Rotation _relative = Rotation.Identity;   // the part's orientation in the camera's frame
	private float _holdDistance = 130f;
	private bool _rotating;

	private CarPartItem _hoverItem;
	private CarAssembly.Socket _hoverSocket;

	/// <summary>The door, hood or trunk being dragged open or shut (left mouse held), or null.</summary>
	private CarAssembly.Socket _dragSocket;

	/// <summary>World units a part's handle moves per pixel of mouse movement while dragging.</summary>
	[Property] public float DragUnitsPerPixel { get; set; } = 0.7f;

	private Rigidbody _weldTarget;
	private string _weldTargetName;

	private PlayerController _player;
	private StretchyArms _arms;
	private GrabHud _hud;
	private string _notice;
	private TimeSince _noticeAge = 999f;

	protected override void OnStart()
	{
		Instance = this;
		_player = Components.Get<PlayerController>( true );
		_arms = Components.GetOrCreate<StretchyArms>();

		var hudObject = Scene.CreateObject();
		hudObject.Name = "Grab HUD";
		hudObject.Flags |= GameObjectFlags.NotSaved;
		hudObject.Components.Create<ScreenPanel>();
		_hud = hudObject.Components.Create<GrabHud>();
	}

	protected override void OnEnabled() => Instance = this;

	protected override void OnDisabled()
	{
		// the player is switched off while driving: put everything down
		if ( _held.IsValid() ) Release( false );
		_dragSocket = null;
		if ( _player.IsValid() ) _player.UseLookControls = true;
		_arms?.Hide();
		if ( _hud.IsValid() ) { _hud.Hint = null; _hud.Controls = null; }
	}

	protected override void OnDestroy()
	{
		if ( _hud.IsValid() ) _hud.GameObject.Destroy();
		if ( Instance == this ) Instance = null;
	}

	/// <summary>Show a short message under the crosshair for a few seconds.</summary>
	public void Notify( string text )
	{
		_notice = text;
		_noticeAge = 0f;
	}

	private static CarAssembly FindAssembly( Scene scene ) => scene.GetAllComponents<CarAssembly>().FirstOrDefault();

	protected override void OnUpdate()
	{
		var camera = Scene.Camera;
		if ( !camera.IsValid() ) return;

		bool pressedF = Input.Keyboard.Pressed( "f" );
		var assembly = FindAssembly( Scene );
		LookSuppressed = false;

		if ( _held.IsValid() )
		{
			_hoverItem = null;
			_hoverSocket = null;

			// right button held: the mouse turns the part instead of turning your head
			_rotating = Input.Down( "Attack2" );
			LookSuppressed = _rotating;
			if ( _player.IsValid() ) _player.UseLookControls = !_rotating;
			if ( _rotating ) Rotate( Input.MouseDelta );

			float wheel = Input.MouseWheel.y;
			if ( wheel != 0f ) _holdDistance = Math.Clamp( _holdDistance + wheel * 14f, MinHoldDistance, MaxHoldDistance );

			// near its socket the part shows a green ghost, and letting go fits it
			CarAssembly.Socket snap = null;
			if ( assembly is not null && assembly.TrySnap( _held, out snap ) ) assembly.ShowGhost( snap );
			else assembly?.HideGhost();

			// Z welds the part to whatever is behind it (the car, another part), exactly where it is
			FindWeldTarget( camera );

			if ( pressedF ) Release( true );
			else if ( Input.Pressed( "Attack1" ) ) Throw( camera );
			else if ( Input.Keyboard.Pressed( "z" ) && _weldTarget.IsValid() ) WeldHeld();

			_hud.Hint = _held.IsValid() && snap is not null ? $"F   Fit {snap.Display}"
				: _held.IsValid() && _weldTarget.IsValid() ? $"Z   Weld to {_weldTargetName}" : null;
		}
		else
		{
			FindTarget( camera, assembly );

			// hold the left mouse button on a door (or the hood, the trunk) and move the mouse to swing it
			bool dragButton = Input.Down( "Attack1" );
			if ( !dragButton ) _dragSocket = null;
			else if ( _dragSocket is null && _hoverSocket is { IsHinged: true } ) _dragSocket = _hoverSocket;

			if ( _dragSocket is not null && (assembly is null || !_dragSocket.IsAttached) ) _dragSocket = null;
			LookSuppressed = _dragSocket is not null;
			if ( _player.IsValid() ) _player.UseLookControls = _dragSocket is null;

			if ( _dragSocket is not null )
			{
				_hoverSocket = _dragSocket;
				_hoverItem = null;
				Drag( camera, assembly, _dragSocket, Input.MouseDelta );
			}
			else if ( pressedF )
			{
				if ( _hoverItem.IsValid() ) Pickup( camera, _hoverItem );
				else if ( _hoverSocket is not null && assembly is not null )
				{
					var item = assembly.Detach( _hoverSocket.Group );
					if ( item.IsValid() ) Pickup( camera, item );
				}
			}

			_hud.Hint = _hoverItem.IsValid() ? $"F   Pick up {_hoverItem.Display}{(_hoverItem.IsWelded ? " (breaks the weld)" : "")}"
				: _hoverSocket is { Group: "handbrake" } ? "Hold left mouse + move up / down   Handbrake"
				: _hoverSocket is { IsHinged: true } ? $"Hold left mouse + move   Open / close {_hoverSocket.Display}      F   Remove"
				: _hoverSocket is not null ? $"F   Remove {_hoverSocket.Display}" : null;
		}

		_hud.Controls = _held.IsValid() ? "Right mouse (hold): turn     Wheel: distance     Left mouse: throw     Z: weld     F: drop / fit" : null;
		if ( _noticeAge < 3.5f && !string.IsNullOrEmpty( _notice ) ) _hud.Hint = _notice;

		_arms.Tick( camera, _held, Time.Delta );
	}

	/// <summary>What is under the crosshair: a loose part, or a part that is on the car.</summary>
	private void FindTarget( CameraComponent camera, CarAssembly assembly )
	{
		_hoverItem = null;
		_hoverSocket = null;

		var origin = camera.WorldPosition;
		var direction = camera.WorldRotation.Forward;

		var tr = Scene.Trace.Ray( origin, origin + direction * ReachDistance ).IgnoreGameObjectHierarchy( GameObject ).Run();
		float wallDistance = tr.Hit ? tr.Distance : ReachDistance;

		if ( tr.Hit && tr.GameObject.IsValid() )
		{
			var item = tr.GameObject.Components.Get<CarPartItem>( FindMode.EverythingInSelfAndParent );
			if ( item.IsValid() )
			{
				_hoverItem = item;
				wallDistance = tr.Distance;
			}
			else if ( tr.GameObject.Components.Get<CarAssembly>( FindMode.EverythingInSelfAndParent ) is not null )
			{
				wallDistance = ReachDistance;   // the car's own collision box doesn't hide the parts inside it
			}
		}

		if ( assembly is not null && assembly.RaycastAttached( origin, direction, ReachDistance, out var socket, out float distance ) )
		{
			if ( !_hoverItem.IsValid() || distance < wallDistance )
			{
				if ( distance <= wallDistance + 1f || !tr.Hit )
				{
					_hoverSocket = socket;
					_hoverItem = null;
				}
			}
		}
	}

	/// <summary>Swing a hinged part: the handle follows the mouse, so pulling it the way it wants to go opens it, pushing it back closes it.</summary>
	private void Drag( CameraComponent camera, CarAssembly assembly, CarAssembly.Socket socket, Vector2 mouse )
	{
		// the handbrake lever: mouse down pulls it on, mouse up pushes it off
		if ( socket.Group == "handbrake" )
		{
			assembly.SwingBy( socket, -mouse.y * 0.45f );
			return;
		}

		var pivot = assembly.PivotWorld( socket );
		var axis = assembly.AxisWorld( socket );
		var radius = assembly.HandleWorld( socket ) - pivot;
		radius -= axis * Vector3.Dot( radius, axis );   // the handle's distance from the hinge line
		float length = radius.Length;
		if ( length < 1f ) return;

		// the way the handle moves when the angle grows, and how far the mouse pulled it that way (on screen)
		var tangent = Vector3.Cross( axis, radius ).Normal;
		var pull = (camera.WorldRotation.Right * mouse.x - camera.WorldRotation.Up * mouse.y) * DragUnitsPerPixel;
		float degrees = Vector3.Dot( pull, tangent ) / length * (180f / MathF.PI);
		assembly.SwingBy( socket, degrees );
	}

	/// <summary>The loose part or the car behind the held part, for welding.</summary>
	private void FindWeldTarget( CameraComponent camera )
	{
		_weldTarget = null;
		_weldTargetName = null;
		if ( !_held.IsValid() ) return;

		var origin = camera.WorldPosition;
		float reach = _holdDistance + _held.HalfExtents.Length + 160f;
		var tr = Scene.Trace.Ray( origin, origin + camera.WorldRotation.Forward * reach )
			.IgnoreGameObjectHierarchy( GameObject ).WithoutTags( "held" ).Run();
		if ( !tr.Hit || !tr.GameObject.IsValid() ) return;

		var body = tr.GameObject.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndParent );
		if ( !body.IsValid() || body == _held.Body ) return;

		_weldTarget = body;
		var item = body.Components.Get<CarPartItem>();
		_weldTargetName = item.IsValid() ? item.Display : (body.Components.Get<CarAssembly>() is not null ? "the car" : body.GameObject.Name);
	}

	/// <summary>Fix the held part where it is to the object behind it (a fixed joint), then let go.</summary>
	private void WeldHeld()
	{
		var item = _held;
		var target = _weldTarget;
		string name = _weldTargetName;
		Release( false );
		if ( !item.IsValid() || !target.IsValid() ) return;

		if ( item.Body is not null ) { item.Body.Velocity = Vector3.Zero; item.Body.AngularVelocity = Vector3.Zero; }
		item.Unweld();

		var joint = item.GameObject.Components.Create<FixedJoint>();
		joint.Body = target.GameObject;
		joint.EnableCollision = false;
		item.Weld = joint;
		Notify( $"{item.Display} welded to {name}" );
	}

	private void Pickup( CameraComponent camera, CarPartItem item )
	{
		if ( !item.IsValid() || item.Body is null ) return;

		if ( item.Unweld() ) Notify( "Weld broken" );

		_held = item;
		item.GameObject.Tags.Add( "held" );
		item.IsHeld = true;
		item.Body.Gravity = false;
		item.Body.AngularVelocity = Vector3.Zero;

		_relative = camera.WorldRotation.Inverse * item.WorldRotation;
		_holdDistance = Math.Clamp( (item.WorldPosition - camera.WorldPosition).Length, MinHoldDistance, MaxHoldDistance );
	}

	private void Rotate( Vector2 mouse )
	{
		// turn the part in the camera's frame: left-right about the camera's up axis, up-down about its left axis
		var yaw = Rotation.FromAxis( Vector3.Up, -mouse.x * RotateSpeed );
		var pitch = Rotation.FromAxis( Vector3.Left, -mouse.y * RotateSpeed );
		_relative = yaw * pitch * _relative;
	}

	/// <summary>Let go of the part. With <paramref name="fit"/>, a part that is near its socket goes onto the car instead of dropping.</summary>
	private void Release( bool fit )
	{
		var item = _held;
		_held = null;
		_rotating = false;
		if ( !item.IsValid() ) return;

		item.IsHeld = false;
		item.GameObject.Tags.Remove( "held" );
		if ( item.Body is not null )
		{
			item.Body.Gravity = true;
			item.Body.Velocity *= 0.3f;
		}

		var assembly = FindAssembly( Scene );
		if ( assembly is null ) return;
		assembly.HideGhost();
		if ( fit && assembly.TrySnap( item, out var socket ) ) assembly.Attach( item, socket );
	}

	private void Throw( CameraComponent camera )
	{
		var item = _held;
		Release( false );
		if ( !item.IsValid() || item.Body is null ) return;

		float mass = item.Body.MassOverride;
		item.Body.Velocity = camera.WorldRotation.Forward * (1100f / (1f + mass / 120f));
	}

	protected override void OnFixedUpdate()
	{
		if ( !_held.IsValid() ) return;

		var camera = Scene.Camera;
		if ( !camera.IsValid() ) return;
		var body = _held.Body;
		if ( body is null ) return;

		// carried a little way in front of you, where you left it; the part is pulled there by its velocity, so walls still stop it
		var target = camera.WorldPosition + camera.WorldRotation.Forward * _holdDistance;
		var toTarget = target - body.WorldPosition;

		if ( toTarget.Length > 450f )
		{
			Release( false );   // snagged on something: let it go
			return;
		}

		var velocity = toTarget * 14f;
		if ( velocity.Length > 2600f ) velocity = velocity.Normal * 2600f;
		body.Velocity = velocity;
		body.AngularVelocity = Vector3.Zero;

		var want = camera.WorldRotation * _relative;
		_held.WorldRotation = Rotation.Slerp( _held.WorldRotation, want, 1f - MathF.Exp( -18f * Time.Delta ) );
	}
}
