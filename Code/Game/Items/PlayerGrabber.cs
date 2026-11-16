namespace LongTrip.Gameplay;

/// <summary>
/// Picking things up, The Long Drive style. Look at an item (or a part that is on the car) and press F to grab it; your arms
/// stretch out to it. While you carry it: hold the right mouse button and move the mouse to turn it, the wheel pushes it away or
/// pulls it close, the left button uses it (eat, drink, pour fuel, swing a weapon; anything else is thrown), T throws it, Z welds it
/// to what is behind it, and F lets go, or fits a car part onto the car when its green ghost is showing.
/// <para>
/// The hotbar: keys 1-6 are slots. A number takes that slot's item into your hands (the same number again, or R, puts it back; another
/// number swaps). With empty hands the wheel picks a slot and R equips it. Hold the left mouse on a door or the handbrake lever and move
/// the mouse to swing it. Goes on the player object.
/// </para>
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

	public Item Held => _held;

	private Item _held;
	private Rotation _relative = Rotation.Identity;   // the item's orientation in the camera's frame
	private float _holdDistance = 130f;
	private bool _rotating;

	// the hotbar
	private int _selected;
	private int _equipped = -1;   // the slot the held item came out of

	// swinging a weapon
	private bool _swinging;
	private TimeSince _swingTime = 999f;
	private float _swingReadyAt;
	private MeleeConfig _swing;

	private Item _hoverItem;
	private CarAssembly.Socket _hoverSocket;

	/// <summary>The door, hood or trunk being dragged open or shut (left mouse held), or null.</summary>
	private CarAssembly.Socket _dragSocket;

	/// <summary>World units a part's handle moves per pixel of mouse movement while dragging.</summary>
	[Property] public float DragUnitsPerPixel { get; set; } = 0.7f;

	private Rigidbody _weldTarget;
	private string _weldTargetName;

	private PlayerController _player;
	private PlayerStats _stats;
	private ItemContainer _pack;
	private PlayerInventory _inventory;
	private StretchyArms _arms;
	private GrabHud _hud;
	private InventoryHud _inventoryHud;
	private string _notice;
	private TimeSince _noticeAge = 999f;

	protected override void OnStart()
	{
		Instance = this;
		ItemLibrary.Reset();   // pick up any change to the item data
		_player = Components.Get<PlayerController>( true );
		_arms = Components.GetOrCreate<StretchyArms>();
		_stats = Components.GetOrCreate<PlayerStats>();
		_pack = Components.GetOrCreate<ItemContainer>();
		_pack.Capacity = 6;
		_inventory = Components.GetOrCreate<PlayerInventory>();
		Components.GetOrCreate<PlayerOutfit>();
		Components.GetOrCreate<ItemIcons>();
		RecipeLibrary.Reset();

		var hudObject = Scene.CreateObject();
		hudObject.Name = "Grab HUD";
		hudObject.Flags |= GameObjectFlags.NotSaved;
		hudObject.Components.Create<ScreenPanel>();
		_hud = hudObject.Components.Create<GrabHud>();
		_inventoryHud = hudObject.Components.Create<InventoryHud>();
	}

	protected override void OnEnabled() => Instance = this;

	protected override void OnDisabled()
	{
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

	/// <summary>Which slot key (1-6) was pressed this frame, as 0-5, or -1.</summary>
	private int PressedSlot()
	{
		for ( int i = 0; i < _pack.Capacity; i++ )
			if ( Input.Pressed( $"Slot{i + 1}" ) ) return i;
		return -1;
	}

	protected override void OnUpdate()
	{
		var camera = Scene.Camera;
		if ( !camera.IsValid() ) return;

		if ( BuildSystem.Active )
		{
			LookSuppressed = false;
			if ( _player.IsValid() ) _player.UseLookControls = true;
			_hud.Hint = BuildSystem.Hint; _hud.Controls = null;
			UpdateHud( FindAssembly( Scene ) );
			_arms.Tick( camera, _held, Time.Delta, _held.IsValid() ? (_held.Definition?.Hands ?? 2) : 2 );
			return;
		}

		// with the inventory open the mouse belongs to the menu: no grabbing, swinging or looking about
		if ( InventoryHud.IsOpen )
		{
			LookSuppressed = true;
			if ( _player.IsValid() ) _player.UseLookControls = false;
			_hud.Hint = null; _hud.Controls = null;
			UpdateHud( FindAssembly( Scene ) );
			_arms.Tick( camera, _held, Time.Delta, _held.IsValid() ? (_held.Definition?.Hands ?? 2) : 2 );
			return;
		}

		bool pressedF = Input.Keyboard.Pressed( "f" );
		bool pressedR = Input.Keyboard.Pressed( "r" );
		int slotKey = PressedSlot();
		var assembly = FindAssembly( Scene );
		LookSuppressed = false;

		if ( slotKey >= 0 ) { OnSlotKey( camera, slotKey ); }

		if ( _held.IsValid() )
		{
			_hoverItem = null;
			_hoverSocket = null;
			var part = _held as CarPartItem;

			// right button held: the mouse turns the part instead of turning your head
			_rotating = Input.Down( "Attack2" );
			LookSuppressed = _rotating;
			if ( _player.IsValid() ) _player.UseLookControls = !_rotating;
			if ( _rotating ) Rotate( Input.MouseDelta );

			float wheel = Input.MouseWheel.y;
			if ( wheel != 0f ) _holdDistance = Math.Clamp( _holdDistance + wheel * 14f, MinHoldDistance, MaxHoldDistance );

			// near its socket a car part shows a green ghost, and letting go fits it
			CarAssembly.Socket snap = null;
			if ( part is not null && assembly is not null && assembly.TrySnap( part, out snap ) ) assembly.ShowGhost( snap );
			else assembly?.HideGhost();

			// Z welds the item to whatever is behind it (the car, another item), exactly where it is
			FindWeldTarget( camera );

			var def = _held.Definition;
			bool usable = def is not null && def.Use != UseKind.None;

			if ( pressedF ) Release( true );
			else if ( Input.Pressed( "Attack1" ) ) { if ( !usable || !UseHeld( def ) ) Throw( camera ); }
			else if ( Input.Keyboard.Pressed( "t" ) ) Throw( camera );
			else if ( pressedR ) Holster();
			else if ( Input.Keyboard.Pressed( "z" ) && _weldTarget.IsValid() ) WeldHeld();

			_hud.Hint = _held.IsValid() && snap is not null ? $"F   Fit {snap.Display}"
				: _held.IsValid() && _weldTarget.IsValid() ? $"Z   Weld to {_weldTargetName}" : null;
			_hud.Controls = _held.IsValid()
				? $"Right mouse (hold): turn     Wheel: distance     Left mouse: {(usable ? UsePrompt( def ) : "throw")}     T: throw     R: holster     Z: weld     F: drop / fit"
				: null;
		}
		else
		{
			FindTarget( camera, assembly );

			// the wheel picks a hotbar slot while your hands are empty
			float wheel = Input.MouseWheel.y;
			if ( wheel != 0f ) _selected = ((_selected - Math.Sign( wheel )) % _pack.Capacity + _pack.Capacity) % _pack.Capacity;

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
			else if ( pressedR )
			{
				Equip( camera, _selected );   // empty hands: equip the selected slot
			}

			_hud.Hint = _hoverItem.IsValid() ? $"F   Pick up {_hoverItem.Display}{Describe( _hoverItem )}{(_hoverItem.IsWelded ? " (breaks the weld)" : "")}"
				: _hoverSocket is { Group: "handbrake" } ? "Hold left mouse + move up / down   Handbrake"
				: _hoverSocket is { IsHinged: true } ? $"Hold left mouse + move   Open / close {_hoverSocket.Display}      F   Remove"
				: _hoverSocket is not null ? $"F   Remove {_hoverSocket.Display}" : null;
			_hud.Controls = null;
		}

		if ( _noticeAge < 3.5f && !string.IsNullOrEmpty( _notice ) ) _hud.Hint = _notice;

		UpdateHud( assembly );
		_arms.Tick( camera, _held, Time.Delta, _held.IsValid() ? (_held.Definition?.Hands ?? 2) : 2 );
		ApplyCarrySpeed();
	}

	private float _baseWalk = -1f, _baseRun = -1f;

	/// <summary>Heavy things slow you down: the held item's <see cref="ItemDefinition.MoveSpeed"/> scales walking and running.</summary>
	private void ApplyCarrySpeed()
	{
		if ( !_player.IsValid() ) return;
		if ( _baseWalk < 0f ) { _baseWalk = _player.WalkSpeed; _baseRun = _player.RunSpeed; }
		float k = _held.IsValid() ? (_held.Definition?.MoveSpeed ?? 1f) : 1f;
		_player.WalkSpeed = _baseWalk * k;
		_player.RunSpeed = _baseRun * k;
	}

	/// <summary>Feed the HUD: stats, the car's fuel gauge (while sitting in it) and the hotbar.</summary>
	private void UpdateHud( CarAssembly assembly )
	{
		if ( _stats.IsValid() ) { _hud.Health = _stats.Health; _hud.Food = _stats.Food; _hud.Water = _stats.Water; }
		_hud.Info = WorldInfo.Lines( Scene );
		_hud.Fuel = assembly is { Occupied: true } ? $"Fuel  {assembly.Fuel:F0} / {CarAssembly.TankCapacity:F0} L" : null;

		var slots = _pack.Slots;
		var names = new string[slots.Count];
		var amounts = new float[slots.Count];
		var ids = new string[slots.Count];
		for ( int i = 0; i < slots.Count; i++ )
		{
			var stored = slots[i];
			ids[i] = stored?.ItemId;
			names[i] = stored is null ? "" : (ItemLibrary.Get( stored.ItemId )?.Name ?? stored.ItemId);
			amounts[i] = stored is null ? -1f : stored.Amount;
		}

		// the slot the held item came from shows its item as "in hand"
		if ( _equipped >= 0 && _equipped < names.Length && _held.IsValid() )
		{
			names[_equipped] = _held.Display;
			ids[_equipped] = _held.ItemId;
			amounts[_equipped] = _held.Amount;
		}

		_hud.SlotIds = ids;
		_hud.SlotNames = names;
		_hud.SlotAmounts = amounts;
		_hud.Selected = _selected;
		if ( _inventoryHud.IsValid() ) { _inventoryHud.HotSelected = _selected; _inventoryHud.HeldSlot = _held.IsValid() ? _equipped : -1; _inventoryHud.Info = _hud.Info; }
		_hud.Equipped = _held.IsValid() ? _equipped : -1;
	}

	// ---- the hotbar --------------------------------------------------------------------------------------------------

	/// <summary>A number key: put the same slot's item back, or swap to this slot's item, or equip it with empty hands.</summary>
	private void OnSlotKey( CameraComponent camera, int slot )
	{
		_selected = slot;

		if ( _held.IsValid() )
		{
			if ( _equipped == slot ) { Holster(); return; }   // the same number again: put it away
			if ( !Holster() ) return;                          // different number: put the held item away first, then take this slot's
		}

		Equip( camera, slot );
	}

	/// <summary>Take the item out of a slot and hold it.</summary>
	private void Equip( CameraComponent camera, int slot )
	{
		if ( _held.IsValid() ) return;
		if ( _pack.IsEmpty( slot ) ) { Notify( $"Slot {slot + 1} is empty" ); return; }

		var item = _pack.Take( slot, camera.WorldPosition + camera.WorldRotation.Forward * 90f, camera.WorldRotation );
		if ( !item.IsValid() ) return;

		Pickup( camera, item );
		_equipped = slot;
	}

	/// <summary>Put the held item away: back in the slot it came from if that is free, otherwise the first free slot.</summary>
	private bool Holster()
	{
		var item = _held;
		if ( !item.IsValid() ) return false;

		var def = item.Definition;
		if ( def is null || !def.Stowable ) { Notify( $"The {item.Display.ToLowerInvariant()} doesn't fit in a slot" ); return false; }

		int slot = _equipped >= 0 && _pack.IsEmpty( _equipped ) ? _equipped : _pack.FirstFree();
		if ( slot < 0 )
		{
			// the hotbar is full: pockets and backpack
			var stored = new ItemContainer.Stored( item.ItemId, item.Amount, item.Liquid );
			if ( _inventory is null || !_inventory.TryAddStorage( stored ) ) { Notify( "All the slots are full" ); return false; }
			Release( false );
			item.GameObject.Destroy();
			Notify( $"{def.Name} put in your pockets" );
			return true;
		}

		Release( false );
		_pack.TryStore( item, slot );
		Sfx.Play( Sfx.Press );
		Notify( $"{def.Name} put in slot {slot + 1}" );
		return true;
	}

	/// <summary>How full / how much is left, for the "Pick up" hint.</summary>
	private static string Describe( Item item )
	{
		var def = item.Definition;
		if ( def is null ) return "";
		if ( def.Container is { } c ) return item.Amount > 0.01f ? $"  ({item.Amount * c.Capacity:F0} / {c.Capacity:F0} L {item.Liquid})" : $"  (empty, {c.Capacity:F0} L)";
		if ( def.Use == UseKind.Refuel ) return $"  ({item.Amount * def.Capacity:F0} L)";
		if ( def.Use == UseKind.Drink || def.Use == UseKind.Eat ) return $"  ({(int)(item.Amount * 100f)}%)";
		return "";
	}

	private static string UsePrompt( ItemDefinition def ) => def.Use switch
	{
		UseKind.Eat => "eat",
		UseKind.Drink => "drink",
		UseKind.Refuel => "pour fuel into the car",
		UseKind.Melee => "swing",
		UseKind.Wear => "put on",
		_ => "use",
	};

	/// <summary>What is under the crosshair: a loose item, or a part that is on the car.</summary>
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
			var item = tr.GameObject.Components.Get<Item>( FindMode.EverythingInSelfAndParent );
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

	/// <summary>The loose item or the car behind the held item, for welding and for pouring fuel.</summary>
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
		var item = body.Components.Get<Item>();
		_weldTargetName = item.IsValid() ? item.Display : (body.Components.Get<CarAssembly>() is not null ? "the car" : body.GameObject.Name);
	}

	/// <summary>Fix the held item where it is to the object behind it (a fixed joint), then let go.</summary>
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

	/// <summary>Use the held item (left mouse). Returns false if it can't be used right now, so the click throws it instead.</summary>
	private bool UseHeld( ItemDefinition def )
	{
		if ( !_held.IsValid() ) return false;

		// a weapon is swung; how hard it hits comes from its speed (see ImpactDamage)
		if ( def.Use == UseKind.Melee )
		{
			if ( _swinging || Time.Now < _swingReadyAt ) return true;
			_swing = def.Melee ?? new MeleeConfig();
			_swinging = true;
			_swingTime = 0f;
			_swingReadyAt = Time.Now + _swing.Cooldown;
			return true;
		}

		if ( def.Use == UseKind.Wear )
		{
			if ( _inventory is null ) return true;
			var worn = _held;
			bool ok;
			if ( def.BackpackTier > 0 ) ok = _inventory.WearPack( def.BackpackTier );
			else ok = _inventory.Wear( new ItemContainer.Stored( worn.ItemId, worn.Amount, worn.Liquid ) );

			if ( !ok ) { Notify( def.BackpackTier > 0 ? "You already have an equal or bigger pack" : "No room to swap that" ); return true; }
			Sfx.Play( Sfx.Cloth );
			Notify( def.BackpackTier > 0 ? $"You put on the {def.Name.ToLowerInvariant()}" : $"You wear the {def.Name.ToLowerInvariant()}" );
			Release( false );
			if ( worn.IsValid() ) worn.GameObject.Destroy();
			return true;
		}

		if ( _held.Amount <= 0.01f )
		{
			Notify( $"{_held.Display} is empty" );
			return true;
		}

		float take = MathF.Min( def.PortionPerUse, _held.Amount );

		switch ( def.Use )
		{
			case UseKind.Eat:
				Sfx.Play( Sfx.Cloth );
				_stats?.Eat( def.Restore * take );
				Notify( $"You ate the {_held.Display.ToLowerInvariant()}" );
				break;

			case UseKind.Drink:
				Sfx.Play( Sfx.Press );
				_stats?.Drink( def.Restore * take );
				Notify( "You drink" );
				break;

			case UseKind.Refuel:
			{
				var car = _weldTarget.IsValid() ? _weldTarget.Components.Get<CarAssembly>() : null;
				if ( car is null ) { Notify( "Aim at the car to pour" ); return true; }

				float accepted = car.AddFuel( def.Capacity * take );
				if ( accepted <= 0.01f ) { Notify( "The tank is full" ); return true; }
				take = accepted / def.Capacity;   // only what went in leaves the can
				Notify( $"Poured {accepted:F1} L" );
				break;
			}

			default:
				return false;
		}

		_held.Amount = MathF.Max( 0f, _held.Amount - take );
		if ( _held.Amount <= 0.01f && def.ConsumedWhenEmpty )
		{
			var used = _held;
			Release( false );
			if ( used.IsValid() ) used.GameObject.Destroy();
		}
		return true;
	}

	private void Pickup( CameraComponent camera, Item item )
	{
		if ( !item.IsValid() || item.Body is null ) return;

		if ( item.Unweld() ) Notify( "Weld broken" );

		// networked: the one who carries it simulates it (so it follows the hand with no lag), and it is theirs until somebody else grabs it
		if ( item.GameObject.Network.Active && item.IsProxy ) item.GameObject.Network.TakeOwnership();

		Sfx.Play( Sfx.DragStart, item.WorldPosition );
		_held = item;
		_equipped = -1;
		_swinging = false;
		item.GameObject.Tags.Add( "held" );
		item.IsHeld = true;
		item.Body.Gravity = false;
		item.Body.AngularVelocity = Vector3.Zero;

		_relative = camera.WorldRotation.Inverse * item.WorldRotation;
		_holdDistance = Math.Clamp( (item.WorldPosition - camera.WorldPosition).Length, MinHoldDistance, 150f );   // brought in close, like holding it out in front of you

		// items that are held a certain way (a weapon, tip forward) are turned into that pose and held at their own distance
		var def = item.Definition;
		if ( def?.HoldAngles is { } angles ) _relative = Rotation.From( angles );
		if ( def is not null && def.HoldDistance > 0f ) _holdDistance = def.HoldDistance;
	}

	private void Rotate( Vector2 mouse )
	{
		// turn the item in the camera's frame: left-right about the camera's up axis, up-down about its left axis
		var yaw = Rotation.FromAxis( Vector3.Up, -mouse.x * RotateSpeed );
		var pitch = Rotation.FromAxis( Vector3.Left, -mouse.y * RotateSpeed );
		_relative = yaw * pitch * _relative;
	}

	/// <summary>Let go of the item. With <paramref name="fit"/>, a car part that is near its socket goes onto the car instead of dropping.</summary>
	private void Release( bool fit )
	{
		var item = _held;
		_held = null;
		_rotating = false;
		_swinging = false;
		_equipped = -1;
		if ( !item.IsValid() ) return;

		item.IsHeld = false;
		item.SinceReleased = 0f;
		item.GameObject.Tags.Remove( "held" );
		if ( item.Body is not null )
		{
			item.Body.Gravity = true;
			item.Body.Velocity *= 0.3f;
		}

		var assembly = FindAssembly( Scene );
		if ( assembly is null ) return;
		assembly.HideGhost();
		if ( fit && item is CarPartItem part && assembly.TrySnap( part, out var socket ) ) assembly.Attach( part, socket );
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

		// carried a little way in front of you, where you left it; the item is pulled there by its velocity, so walls still stop it
		var forward = camera.WorldRotation.Forward;
		var want = camera.WorldRotation * _relative;

		// weight: heavy things are slow to follow your hand (they lag, and swing when you turn), light things stay right in front
		float mass = body.MassOverride > 0f ? body.MassOverride : 1f;
		float heft = 1f / (1f + mass / 30f);
		float gain = MathX.Lerp( 5f, 14f, heft ), maxSpeed = 2600f * MathX.Lerp( 0.3f, 1f, heft ), turn = MathX.Lerp( 8f, 18f, heft );

		// standing on what you are holding would lift you into the air: let go instead
		var below = Scene.Trace.Ray( GameObject.WorldPosition + Vector3.Up * 12f, GameObject.WorldPosition + Vector3.Down * 26f ).IgnoreGameObjectHierarchy( GameObject ).Run();
		if ( below.Hit && below.GameObject.IsValid() && below.GameObject.Components.Get<Item>( FindMode.EverythingInSelfAndParent ) == _held )
		{
			Notify( "You can't stand on what you are holding" );
			Release( false );
			return;
		}

		// a swing sweeps the item through an arc about your left axis; it is the speed of that sweep that hurts
		if ( _swinging && _swing is not null )
		{
			float p = _swingTime / MathF.Max( 0.05f, _swing.SwingSeconds );
			if ( p >= 1f )
			{
				_swinging = false;
			}
			else
			{
				float eased = p * p * (3f - 2f * p);
				float arc = MathX.Lerp( _swing.StartAngle, _swing.EndAngle, eased );
				var tilt = Rotation.FromAxis( camera.WorldRotation.Left, arc );
				forward = tilt * forward;
				want = tilt * want;
				gain = 42f; maxSpeed = 4200f; turn = 40f;
			}
		}

		// it never goes through a wall or the car: the hold distance shortens to whatever is in the way (with the item's own size to spare)
		float distance = _holdDistance;
		float reach = MathF.Max( _held.HalfExtents.x, MathF.Max( _held.HalfExtents.y, _held.HalfExtents.z ) );
		var blocked = Scene.Trace.Ray( camera.WorldPosition, camera.WorldPosition + forward * (distance + reach) )
			.IgnoreGameObjectHierarchy( GameObject ).WithoutTags( "held" ).Run();
		if ( blocked.Hit ) distance = MathF.Max( MinHoldDistance * 0.5f, MathF.Min( distance, blocked.Distance - reach ) );

		var target = camera.WorldPosition + forward * distance;
		var toTarget = target - body.WorldPosition;

		if ( toTarget.Length > 450f )
		{
			Release( false );   // snagged on something: let it go
			return;
		}

		var velocity = toTarget * gain;
		if ( velocity.Length > maxSpeed ) velocity = velocity.Normal * maxSpeed;
		body.Velocity = velocity;
		body.AngularVelocity = Vector3.Zero;

		_held.WorldRotation = Rotation.Slerp( _held.WorldRotation, want, 1f - MathF.Exp( -turn * Time.Delta ) );
	}
}
