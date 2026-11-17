namespace LongTrip.Gameplay;

/// <summary>
/// The "ghost ship" for a vehicle: everything solid on the car (its body and parts, the pieces built on it, things welded to it, loose items lying on it and the
/// other people standing on it) is cloned as <b>static</b> colliders at a fixed place far from the world, kept in the car's own local space. A person walking on the car
/// does not collide with the real, fast-moving, tilting car at all: they are simulated in the ghost, where nothing moves, and the result (a position in the car's
/// local space) is turned back into a world position through the car's current transform. So no speed, no bump and no tilt of the car can push you through its walls,
/// shake you off or drag the floor out from under you, and what is built on the car comes along for free because it is part of the clone.
/// <para>
/// Gravity in the ghost points where the world's gravity points <i>in the car's frame</i>. When the car tilts, gravity leans across the deck: on a gentle tilt you
/// stand, on a steeper one you slide, and past <see cref="MaxSlope"/> the floor is too steep to stand on and you fall off the side, back into the real world with the
/// car's speed. Each player simulates only their own character on their own machine (the car's transform is the same everywhere, synced), and other players on
/// the car are cloned as static capsules, so everybody collides with everybody and the world poses sync as before.
/// </para>
/// Goes on the car root.
/// </summary>
/// <summary>Marks a piece of the car's collision that the ghost ship must not clone (the exact <see cref="GhostMesh"/> stands in for it).</summary>
public sealed class GhostIgnore : Component { }

/// <summary>An exact (low-polygon, concave) collision mesh for the ghost ship: people on the car walk on this instead of the car's simple physics collision.</summary>
public sealed class GhostMesh : Component
{
	public Model Mesh { get; set; }
}

public sealed class GhostShip : Component
{
	/// <summary>How far from the car's centre things count as on it (world units).</summary>
	[Property] public float Range { get; set; } = 650f;

	/// <summary>Floors steeper than this (degrees away from the way gravity points) cannot be stood on: you slide and fall off.</summary>
	[Property, Range( 20f, 80f )] public float MaxSlope { get; set; } = 55f;

	/// <summary>From this angle on you start to slide down a tilted deck.</summary>
	[Property, Range( 5f, 60f )] public float SlideStart { get; set; } = 28f;

	/// <summary>Where the ghost lives: far above and beside everything the world makes.</summary>
	private static readonly Vector3 Origin = new( 0f, 900000f, 900000f );
	private const string Tag = "ghostship";
	private const float Gravity = 800f;
	private const float Height = 72f, Radius = 15f;

	private GameObject _ghost;
	private readonly Dictionary<Component, GameObject> _clones = new();
	private readonly List<Component> _seen = new();
	private TimeUntil _refresh = 0f;
	private Rigidbody _carBody;
	private CarSession _session;

	// the person walking
	private PlayerController _player;
	private Rigidbody _playerBody;
	private bool _aboard;
	private Vector3 _localPos, _localVel;
	private bool _grounded;
	private Vector3 _groundNormal = Vector3.Up;
	private float _airTime;
	private TimeSince _sinceLeft = 99f;
	private Rotation _lastRotation;
	private Vector3 _nearPrev;
	public bool DebugNear { get; set; }
	private bool _nearValid;

	/// <summary>Every ghost ship that exists.</summary>
	public static readonly List<GhostShip> All = new();

	// things lying about on the car: they are simulated in the ghost too (see Board)
	private sealed class Boarded
	{
		public Item Item;
		public Rigidbody Body;
		public GameObject Proxy, Light;
		public readonly List<ModelRenderer> Originals = new();
	}
	private readonly List<Boarded> _boarded = new();
	private TimeUntil _itemCheck = 0f;

	/// <summary>The space on and above the car where loose things belong to it (the car's own frame, inches).</summary>
	private static readonly Vector3 BayLo = new( -250f, -110f, -60f ), BayHi = new( 250f, 110f, 330f );

	private Transform Frame => WorldTransform.WithScale( 1f );

	protected override void OnStart()
	{
		_carBody = Components.Get<Rigidbody>();
		_ghost = Scene.CreateObject();
		_ghost.Name = "Ghost ship";
		_ghost.Flags |= GameObjectFlags.NotSaved;
		_ghost.WorldPosition = Origin + Vector3.Right * (Scene.GetAllComponents<GhostShip>().Count() * 8000f);
		_ghost.Tags.Add( Tag );
		_lastRotation = WorldRotation;
		All.Add( this );
	}

	protected override void OnDestroy()
	{
		All.Remove( this );
		ReleaseAll();
		End( false );
		if ( _ghost.IsValid() ) _ghost.Destroy();
	}

	protected override void OnDisabled() { ReleaseAll(); End( false ); }

	/// <summary>True while the local player is being simulated in the ghost.</summary>
	public bool Carrying => _aboard;

	// ---- the clone -------------------------------------------------------------------------------------------------------------------

	private void Mirror( bool membership )
	{
		var frame = Frame;

		if ( membership )
		{
			_seen.Clear();

			// the real car's own collision is for the car's physics only: nothing a person or an item does ever touches it (Collision.config)
			foreach ( var c in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
				if ( c.IsValid() ) c.GameObject.Tags.Add( "realcar" );

			foreach ( var c in Components.GetAll<Collider>( FindMode.EnabledInSelfAndDescendants ) )
				if ( c.IsValid() && !c.IsTrigger && c.Components.Get<GhostIgnore>( FindMode.EverythingInSelfAndParent ) is null ) _seen.Add( c );

			// the exact meshes of the body and its parts
			foreach ( var gm in Components.GetAll<GhostMesh>( FindMode.EnabledInSelfAndDescendants ) )
				if ( gm.IsValid() && gm.Mesh is not null ) _seen.Add( gm );

			// loose and welded items lying on or fixed to the car
			foreach ( var item in Scene.GetAllComponents<Item>() )
			{
				if ( !item.IsValid() || item.IsHeld || IsBoarded( item ) ) continue;
				if ( (item.WorldPosition - WorldPosition).Length > Range + 200f ) continue;
				foreach ( var c in item.Components.GetAll<Collider>( FindMode.EnabledInSelfAndDescendants ) )
					if ( c.IsValid() && !c.IsTrigger ) _seen.Add( c );
			}

			// other people (the local one is simulated, not cloned)
			foreach ( var p in Scene.GetAllComponents<PlayerController>() )
				if ( p.IsValid() && p.IsProxy && (p.WorldPosition - WorldPosition).Length < Range + 200f ) _seen.Add( p );

			// drop the clones of what is gone, make the ones that are new
			foreach ( var gone in _clones.Keys.Where( k => !k.IsValid() || !_seen.Contains( k ) ).ToList() )
			{
				if ( _clones[gone].IsValid() ) _clones[gone].Destroy();
				_clones.Remove( gone );
			}
			foreach ( var source in _seen )
				if ( !_clones.ContainsKey( source ) ) { var made = Clone( source ); if ( made.IsValid() ) _clones[source] = made; }
		}

		// poses: relative to the car, so the clone is the car as it stands still
		foreach ( var (source, clone) in _clones )
		{
			if ( !source.IsValid() || !clone.IsValid() ) continue;
			var go = source.GameObject;
			if ( source is PlayerController person )
			{
				clone.WorldPosition = _ghost.WorldPosition + frame.PointToLocal( person.WorldPosition + Vector3.Up * Height * 0.5f );
				clone.WorldRotation = Rotation.Identity;
			}
			else
			{
				clone.WorldPosition = _ghost.WorldPosition + frame.PointToLocal( go.WorldPosition );
				clone.WorldRotation = frame.Rotation.Inverse * go.WorldRotation;
				clone.WorldScale = go.WorldScale;
			}
		}
	}

	private GameObject Clone( Component source )
	{
		var go = Scene.CreateObject();
		go.Name = "Ghost of " + source.GameObject.Name;
		go.Flags |= GameObjectFlags.NotSaved;
		go.SetParent( _ghost, false );
		go.Tags.Add( Tag );

		switch ( source )
		{
			case BoxCollider box:
			{
				var c = go.AddComponent<BoxCollider>(); c.Center = box.Center; c.Scale = box.Scale; break;
			}
			case SphereCollider sphere:
			{
				var c = go.AddComponent<SphereCollider>(); c.Center = sphere.Center; c.Radius = sphere.Radius; break;
			}
			case CapsuleCollider capsule:
			{
				var c = go.AddComponent<CapsuleCollider>(); c.Start = capsule.Start; c.End = capsule.End; c.Radius = capsule.Radius; break;
			}
			case ModelCollider model:
			{
				var c = go.AddComponent<ModelCollider>(); c.Model = model.Model; break;
			}
			case GhostMesh mesh:
			{
				var c = go.AddComponent<ModelCollider>(); c.Model = mesh.Mesh; break;
			}
			case PlayerController:
			{
				var c = go.AddComponent<BoxCollider>(); c.Scale = new Vector3( Radius * 2f, Radius * 2f, Height ); break;
			}
			default:
				go.Destroy();
				return null;
		}
		return go;
	}

	// ---- the walker -----------------------------------------------------------------------------------------------------------------

	private PlayerController LocalPlayer()
	{
		if ( !_player.IsValid() || _player.IsProxy ) _player = Scene.GetAllComponents<PlayerController>().FirstOrDefault( p => p.IsValid() && !p.IsProxy );
		return _player;
	}

	private Vector3 CarVelocityAt( Vector3 world ) => _carBody.IsValid() ? _carBody.GetVelocityAtPoint( world ) : Vector3.Zero;

	/// <summary>
	/// Is the player standing on a surface of the car? Asked of the ghost, not the real car (people never touch that): a floor-like surface of the clone right under the feet.
	/// A side of the car, or the ground beside it, does not count, so walking past the car never counts as getting on it.
	/// </summary>
	private bool StandingOnCar( PlayerController pc )
	{
		if ( (pc.WorldPosition - WorldPosition).LengthSquared > Range * Range ) return false;

		var frame = Frame;
		var local = frame.PointToLocal( pc.WorldPosition );
		var up = (frame.Rotation.Inverse * Vector3.Up).Normal;
		var tr = Sweep( local + up * 6f, local - up * 22f );
		return tr.Hit && !tr.StartedSolid && Vector3.Dot( tr.Normal, up ) > 0.7f;
	}

	private void Begin( PlayerController pc )
	{
		var frame = Frame;
		_playerBody = pc.Components.Get<Rigidbody>( true );
		_localPos = frame.PointToLocal( pc.WorldPosition );
		var worldVelocity = _playerBody.IsValid() ? _playerBody.Velocity : Vector3.Zero;
		_localVel = frame.Rotation.Inverse * (worldVelocity - CarVelocityAt( pc.WorldPosition ));
		_grounded = true;
		_airTime = 0f;
		_aboard = true;
		_lastRotation = WorldRotation;

		// the character stops walking by itself (the ghost walks it); its body no longer takes part in the real physics while aboard
		pc.UseInputControls = false;
		if ( _playerBody.IsValid() ) _playerBody.MotionEnabled = false;
	}

	/// <summary>Back into the real world: the character walks and falls by itself again, with the car's speed plus its own.</summary>
	private void End( bool withVelocity )
	{
		if ( !_aboard ) return;
		_aboard = false;
		_sinceLeft = 0f;

		var pc = _player;
		if ( !pc.IsValid() ) return;
		var frame = Frame;
		pc.UseInputControls = true;
		pc.WorldPosition = frame.PointToWorld( _localPos );
		if ( _playerBody.IsValid() )
		{
			_playerBody.MotionEnabled = true;
			_playerBody.Velocity = withVelocity ? CarVelocityAt( pc.WorldPosition ) + frame.Rotation * _localVel : Vector3.Zero;
		}
	}

	private Vector3 GhostPoint( Vector3 local ) => _ghost.WorldPosition + local;

	private SceneTraceResult Sweep( Vector3 from, Vector3 to )
	{
		var box = new BBox( new Vector3( -Radius, -Radius, 0f ), new Vector3( Radius, Radius, Height ) );
		return Scene.Trace.Box( box, GhostPoint( from ), GhostPoint( to ) ).WithTag( Tag ).Run();
	}

	protected override void OnFixedUpdate()
	{
		Mirror( _refresh );
		if ( _refresh ) _refresh = 0.25f;

		TickItems( Time.Delta );

		var pc = LocalPlayer();
		if ( !pc.IsValid() ) return;
		if ( !pc.GameObject.Tags.Has( "ghostrider" ) ) pc.GameObject.Tags.Add( "ghostrider" );   // (never collides with the real car)

		_session ??= Scene.GetAllComponents<CarSession>().FirstOrDefault();
		if ( _session.IsValid() && _session.IsDriving ) { End( false ); _nearValid = false; return; }   // sitting in a seat: the seat carries you

		if ( !_aboard )
		{
			if ( _sinceLeft > 0.4f && StandingOnCar( pc ) ) Begin( pc );
			if ( !_aboard ) { NearCar( pc ); return; }
		}

		_nearValid = false;
		Simulate( pc, Time.Delta );
	}

	/// <summary>
	/// Walking about near the car on the ground (not on it): the real car never touches you, so the ghost keeps you out of it. Where you are, measured in the car's own
	/// frame, is swept from where you were a moment ago (in the car's frame then): the car moving into you, or you walking into the car, is the same thing, and the
	/// answer is a plain push out along the surface, with no impulse and no bounce.
	/// </summary>
	private void NearCar( PlayerController pc )
	{
		if ( (pc.WorldPosition - WorldPosition).Length > Range + 150f ) { _nearValid = false; return; }

		var frame = Frame;
		var now = frame.PointToLocal( pc.WorldPosition );
		if ( !_nearValid ) { _nearPrev = now; _nearValid = true; return; }

		var up = (frame.Rotation.Inverse * Vector3.Up).Normal;
		var resolved = Slide( _nearPrev, now - _nearPrev, up );
		_nearPrev = resolved;

		var push = resolved - now;
		if ( push.LengthSquared < 0.0004f ) return;

		pc.WorldPosition = frame.PointToWorld( resolved );
		var body = pc.Components.Get<Rigidbody>( true );
		if ( body.IsValid() )
		{
			var direction = frame.Rotation * push.Normal;
			float into = Vector3.Dot( body.Velocity, direction );
			if ( into < 0f ) body.Velocity -= direction * into;
		}
	}

	/// <summary>Slide a move through the ghost: stop at what is in the way and glide along it; no stepping, no velocity.</summary>
	private Vector3 Slide( Vector3 pos, Vector3 move, Vector3 up )
	{
		for ( int i = 0; i < 3 && move.LengthSquared > 1e-6f; i++ )
		{
			var tr = Sweep( pos, pos + move );
			if ( DebugNear ) Log.Info( $"[ghost] slide {i} pos {pos} move {move} hit {tr.Hit} solid {tr.StartedSolid} frac {tr.Fraction:F3} normal {tr.Normal}" );
			if ( tr.StartedSolid )
			{
				foreach ( var lift in new[] { 0.5f, 1f, 2f, 4f, 8f, 16f } )
				{
					if ( Sweep( pos + up * lift, pos + up * lift + up * 0.05f ).StartedSolid ) continue;
					pos += up * lift; break;
				}
				break;
			}
			pos = tr.EndPosition - _ghost.WorldPosition;
			if ( !tr.Hit ) break;
			var n = tr.Normal;
			move = move * (1f - tr.Fraction);
			move -= n * Vector3.Dot( move, n );
			pos += n * 0.05f;
		}
		return pos;
	}

	// ---- loose things on the car ---------------------------------------------------------------------------------------------------------

	private bool InBay( Vector3 local ) => local.x > BayLo.x && local.x < BayHi.x && local.y > BayLo.y && local.y < BayHi.y && local.z > BayLo.z && local.z < BayHi.z;

	private bool IsBoarded( Item item ) => _boarded.Any( b => b.Item == item );

	private void TickItems( float dt )
	{
		var frame = Frame;
		var down = (frame.Rotation.Inverse * Vector3.Down).Normal;

		for ( int i = _boarded.Count - 1; i >= 0; i-- )
		{
			var b = _boarded[i];
			if ( !b.Item.IsValid() || !b.Body.IsValid() ) { CleanUp( b ); _boarded.RemoveAt( i ); continue; }
			if ( b.Item.IsHeld || b.Item.IsWelded || !InBay( b.Item.WorldPosition - _ghost.WorldPosition ) ) { Unboard( b ); _boarded.RemoveAt( i ); continue; }
			b.Body.Velocity += down * Gravity * dt;   // gravity as the car sees it
		}

		// a joined multiplayer game keeps loose items in the world (their position is what the others see)
		if ( Networking.IsActive || !_itemCheck ) return;
		_itemCheck = 0.1f;

		foreach ( var item in Scene.GetAllComponents<Item>().ToList() )
		{
			if ( !item.IsValid() || item.IsHeld || item.IsWelded || item.IsProxy || !item.Body.IsValid() || IsBoarded( item ) ) continue;
			if ( !InBay( frame.PointToLocal( item.WorldPosition ) ) ) continue;
			if ( (item.Body.Velocity - CarVelocityAt( item.WorldPosition )).Length > 700f ) continue;
			Board( item );
		}
	}

	/// <summary>The item moves into the ghost: its real body lives there (on the static clone, in the car's frame, with the car's gravity); what you see is a copy at the matching place in the world.</summary>
	private void Board( Item item )
	{
		var frame = Frame;
		var go = item.GameObject;
		var body = item.Body;
		var itemTransform = go.WorldTransform;

		var local = frame.PointToLocal( go.WorldPosition );
		var rotation = frame.Rotation.Inverse * go.WorldRotation;
		var velocity = frame.Rotation.Inverse * (body.Velocity - CarVelocityAt( go.WorldPosition ));
		var spin = frame.Rotation.Inverse * body.AngularVelocity;

		// the static clone of this item's colliders (made while it was only lying near) must go before the real body arrives: they would overlap and throw it away
		foreach ( var c in go.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ).ToList() )
		{
			if ( !_clones.TryGetValue( c, out var clone ) ) continue;
			if ( clone.IsValid() ) { clone.Enabled = false; clone.Destroy(); }
			_clones.Remove( c );
		}

		var b = new Boarded { Item = item, Body = body };
		b.Proxy = Scene.CreateObject();
		b.Proxy.Name = "Ghost proxy of " + go.Name;
		b.Proxy.Flags |= GameObjectFlags.NotSaved;

		foreach ( var r in go.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ).ToList() )
		{
			if ( !r.IsValid() || !r.Enabled ) continue;
			var t = r.GameObject.WorldTransform;
			var child = Scene.CreateObject();
			child.SetParent( b.Proxy, false );
			child.LocalPosition = itemTransform.PointToLocal( t.Position );
			child.LocalRotation = itemTransform.Rotation.Inverse * t.Rotation;
			child.LocalScale = new Vector3( t.Scale.x / MathF.Max( 0.0001f, itemTransform.Scale.x ), t.Scale.y / MathF.Max( 0.0001f, itemTransform.Scale.y ), t.Scale.z / MathF.Max( 0.0001f, itemTransform.Scale.z ) );
			var copy = child.Components.Create<ModelRenderer>();
			copy.Model = r.Model; copy.Tint = r.Tint; copy.MaterialOverride = r.MaterialOverride; copy.RenderType = r.RenderType;
			r.Enabled = false;
			b.Originals.Add( r );
		}

		// a flashlight's light goes with the picture
		b.Light = go.Children.FirstOrDefault( c => c.Name == "Light" );
		if ( b.Light.IsValid() ) b.Light.SetParent( b.Proxy, false );

		go.SetParent( _ghost, false );
		go.WorldPosition = _ghost.WorldPosition + local;
		go.WorldRotation = rotation;
		body.Velocity = velocity;
		body.AngularVelocity = spin;
		body.Gravity = false;
		TagTree( go, true );

		_boarded.Add( b );
	}

	/// <summary>Back into the world with the car's speed plus its own.</summary>
	private void Unboard( Boarded b )
	{
		var frame = Frame;
		var go = b.Item.GameObject;
		var local = go.WorldPosition - _ghost.WorldPosition;
		var rotation = go.WorldRotation;
		var velocity = b.Body.Velocity;
		var spin = b.Body.AngularVelocity;

		go.SetParent( Scene, false );
		go.WorldPosition = frame.PointToWorld( local );
		go.WorldRotation = frame.Rotation * rotation;
		b.Body.Gravity = true;
		b.Body.Velocity = CarVelocityAt( go.WorldPosition ) + frame.Rotation * velocity;
		b.Body.AngularVelocity = frame.Rotation * spin;
		TagTree( go, false );
		CleanUp( b );
	}

	private void CleanUp( Boarded b )
	{
		foreach ( var r in b.Originals ) if ( r.IsValid() ) r.Enabled = true;
		if ( b.Light.IsValid() && b.Item.IsValid() ) b.Light.SetParent( b.Item.GameObject, false );
		if ( b.Item.IsValid() ) b.Item.ProxyPosition = null;
		if ( b.Proxy.IsValid() ) b.Proxy.Destroy();
	}

	private static void TagTree( GameObject go, bool on )
	{
		if ( on ) go.Tags.Add( Tag ); else go.Tags.Remove( Tag );
		foreach ( var child in go.Children ) TagTree( child, on );
	}

	private void ReleaseAll()
	{
		foreach ( var b in _boarded.ToList() ) { if ( b.Item.IsValid() && b.Body.IsValid() ) Unboard( b ); else CleanUp( b ); }
		_boarded.Clear();
	}

	/// <summary>Bring an item out of any ghost it is in (before it is picked up).</summary>
	public static void Release( Item item )
	{
		foreach ( var ship in All.ToList() )
		{
			if ( !ship.IsValid() ) continue;
			var b = ship._boarded.FirstOrDefault( x => x.Item == item );
			if ( b is null ) continue;
			ship.Unboard( b );
			ship._boarded.Remove( b );
		}
	}

	/// <summary>Look for an item that lives in a ghost ship, along a ray in the world. Null if there is none.</summary>
	public static Item RayItem( Vector3 origin, Vector3 direction, float maxDistance, out float distance )
	{
		distance = 0f;
		foreach ( var ship in All )
		{
			if ( !ship.IsValid() || ship._boarded.Count == 0 ) continue;
			var frame = ship.Frame;
			var o = ship._ghost.WorldPosition + frame.PointToLocal( origin );
			var d = frame.Rotation.Inverse * direction;
			var tr = ship.Scene.Trace.Ray( o, o + d * maxDistance ).WithTag( Tag ).Run();
			if ( !tr.Hit || !tr.GameObject.IsValid() ) continue;
			var item = tr.GameObject.Components.Get<Item>( FindMode.EverythingInSelfAndParent );
			if ( item.IsValid() && ship.IsBoarded( item ) ) { distance = tr.Distance; return item; }
		}
		return null;
	}

	private void Simulate( PlayerController pc, float dt )
	{
		var frame = Frame;

		// gravity as the car sees it: when the car tilts, it leans across the deck
		var down = (frame.Rotation.Inverse * Vector3.Down).Normal;
		var up = -down;

		// what is under the feet (along gravity)
		var probe = Sweep( _localPos + up * 2f, _localPos + down * 3f );
		float cosMax = MathF.Cos( MaxSlope * MathF.PI / 180f );
		bool floor = probe.Hit && !probe.StartedSolid;
		var normal = floor ? probe.Normal : up;
		float flatness = floor ? Vector3.Dot( normal, up ) : 0f;
		bool standable = floor && flatness >= cosMax;
		bool rising = Vector3.Dot( _localVel, up ) > 40f;
		_grounded = standable && !rising;
		_groundNormal = normal;

		// what you want to do
		var analog = Input.AnalogMove;
		var wishWorld = Rotation.FromYaw( pc.EyeAngles.yaw ) * new Vector3( analog.x, analog.y, 0f ).ClampLength( 1f );
		var wish = frame.Rotation.Inverse * wishWorld;
		float speed = Input.Down( "Run" ) ? pc.RunSpeed : pc.WalkSpeed;
		if ( _grounded ) wish -= normal * Vector3.Dot( wish, normal );
		var target = wish.Length > 0.01f ? wish.Normal * speed * MathF.Min( 1f, wish.Length ) : Vector3.Zero;

		var vertical = up * Vector3.Dot( _localVel, up );
		var flat = _localVel - vertical;
		if ( _grounded )
		{
			// walk: speed up towards what you want, brake when you want nothing; whatever pushed you into the floor is gone
			var along = _localVel - normal * Vector3.Dot( _localVel, normal );
			float accel = target.Length > 0.1f ? 1600f : 1400f;
			var delta = target - along;
			along += delta.Length > accel * dt ? delta.Normal * accel * dt : delta;

			// a tilted deck: the part of gravity along the slope drags you downhill, more the steeper it is
			float angle = MathF.Acos( Math.Clamp( flatness, 0f, 1f ) ) * 180f / MathF.PI;
			float slide = Math.Clamp( (angle - SlideStart) / MathF.Max( 1f, MaxSlope - SlideStart ), 0f, 1f );
			var downhill = down - normal * Vector3.Dot( down, normal );
			_localVel = along + downhill * Gravity * slide * dt * 2f;

			if ( Input.Pressed( "Jump" ) ) { _localVel += up * 300f; _grounded = false; }
		}
		else
		{
			// in the air (or on a floor too steep to stand on): a little steering, and gravity
			var delta = target - flat;
			flat += delta.Length > 240f * dt ? delta.Normal * 240f * dt : delta;
			_localVel = flat + vertical + down * Gravity * dt;
			if ( floor && !standable )
			{
				// sliding down a steep floor: keep out of it, and it lets go of you at the edge
				float into = Vector3.Dot( _localVel, normal );
				if ( into < 0f ) _localVel -= normal * into;
			}
		}

		if ( _localVel.Length > 1500f ) _localVel = _localVel.Normal * 1500f;
		_airTime = _grounded ? 0f : _airTime + dt;
		_localPos = Move( _localPos, dt, up );

		// ---- back into the world
		var world = frame.PointToWorld( _localPos );
		pc.WorldPosition = world;
		if ( _playerBody.IsValid() ) _playerBody.Velocity = frame.Rotation * _localVel;   // (for the walking animation)

		// the car turns under you: so does your view
		float yaw = (WorldRotation * _lastRotation.Inverse).Angles().yaw;
		_lastRotation = WorldRotation;
		pc.EyeAngles = pc.EyeAngles + new Angles( 0f, yaw, 0f );

		// left the car: stepped onto the road, or fell off it
		var ground = Scene.Trace.Ray( world + Vector3.Up * 12f, world + Vector3.Down * 8f ).IgnoreGameObjectHierarchy( GameObject ).IgnoreGameObjectHierarchy( pc.GameObject ).Run();
		bool onRealGround = ground.Hit && ground.GameObject.IsValid() && !ground.GameObject.IsDescendant( GameObject ) && ground.GameObject.Components.Get<Item>() is null;   // (things lying on the car do not count as the road)
		bool far = (world - WorldPosition).Length > Range + 120f;
		if ( onRealGround || far || (_airTime > 0.35f && _localPos.z < -90f) ) End( true );
	}

	/// <summary>Move through the ghost: slide along what is in the way, step up small ledges, push out of anything stuck in.</summary>
	private Vector3 Move( Vector3 pos, float dt, Vector3 up )
	{
		var move = _localVel * dt;
		for ( int i = 0; i < 4 && move.LengthSquared > 1e-6f; i++ )
		{
			var tr = Sweep( pos, pos + move );
			if ( tr.StartedSolid )
			{
				// already inside something (a part just moved into us): the smallest push up that gets us out; if there is none, stay put
				bool freed = false;
				foreach ( var lift in new[] { 0.5f, 1f, 2f, 4f, 8f } )
				{
					if ( Sweep( pos + up * lift, pos + up * lift + up * 0.05f ).StartedSolid ) continue;
					pos += up * lift; freed = true; break;
				}
				if ( !freed ) _localVel = Vector3.Zero;
				break;
			}

			pos = tr.EndPosition - _ghost.WorldPosition;
			if ( !tr.Hit ) break;

			// a low ledge in front of the feet: step up onto it
			if ( _grounded && Vector3.Dot( tr.Normal, up ) < 0.5f )
			{
				var flatMove = (move * (1f - tr.Fraction)); flatMove -= up * Vector3.Dot( flatMove, up );
				if ( flatMove.LengthSquared > 0.01f )
				{
					var raised = Sweep( pos, pos + up * 18f );
					var raisedPos = raised.EndPosition - _ghost.WorldPosition;
					var over = Sweep( raisedPos, raisedPos + flatMove );
					var overPos = over.EndPosition - _ghost.WorldPosition;
					var land = Sweep( overPos, overPos - up * 20f );
					if ( !over.StartedSolid && over.Fraction > 0.5f && land.Hit && Vector3.Dot( land.Normal, up ) > 0.7f )
					{
						pos = land.EndPosition - _ghost.WorldPosition;
						move = Vector3.Zero;
						break;
					}
				}
			}

			var n = tr.Normal;
			move = move * (1f - tr.Fraction);
			move -= n * Vector3.Dot( move, n );
			float into = Vector3.Dot( _localVel, n );
			if ( into < 0f ) _localVel -= n * into;
			pos += n * 0.05f;
		}
		return pos;
	}

	protected override void OnUpdate()
	{
		var frame = Frame;
		foreach ( var b in _boarded )
		{
			if ( !b.Item.IsValid() || !b.Proxy.IsValid() ) continue;
			b.Proxy.WorldPosition = frame.PointToWorld( b.Item.WorldPosition - _ghost.WorldPosition );
			b.Proxy.WorldRotation = frame.Rotation * b.Item.WorldRotation;
			b.Item.ProxyPosition = b.Proxy.WorldPosition;
		}

		// between physics steps the player stays fixed to the car (the car's own transform is interpolated each frame), so there is no shake
		if ( !_aboard || !_player.IsValid() ) return;
		_player.WorldPosition = frame.PointToWorld( _localPos );
	}
}
