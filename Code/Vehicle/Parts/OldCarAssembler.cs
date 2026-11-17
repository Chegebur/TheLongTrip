using LongTrip.World.Props;

namespace VehicleProto;

/// <summary>
/// Builds the old car from the segmented model. Unlike <see cref="PartKitAssembler"/> (which makes a render object and
/// a box collider per part for the generated hatch kit), this car has ~220 parts, so:
///   - everything that never moves is ONE merged model (old_car_static: body shell, interior, engine bay, glass, lights...)
///   - the parts that move or will be animated stay separate objects: 4 wheels, 4 doors (with their windows), hood,
///     trunk and the steering wheel (see <see cref="OldCarParts"/>)
///   - physics is the kit's normal root collider and raycast wheels; no per-part colliders.
/// Every part model is authored in the car's own frame (+X forward, +Y left, +Z up, origin on the ground in the middle of
/// the wheelbase), so the "Kit Body" object needs no rotation. Part sources, naming and the grouping live in
/// models/vehicles/old_car/parts_manifest.json.
/// </summary>
public static class OldCarAssembler
{
	/// <summary>The car definition's BodyManifest value that selects this assembler (the file also lists every part).</summary>
	public const string ManifestPath = "models/vehicles/old_car/parts_manifest.json";

	const string Folder = "models/vehicles/old_car/";
	const float M = 39.37f;

	/// <summary>How much bigger than the model's natural size (a 4.66 m sedan) the car is built: the visuals, the physics and
	/// every position below all follow it.</summary>
	public const float SizeFactor = 2.2f / 1.4f * 1.08f;   // 1.57: the user asked for 2.2x, then "1.4 times smaller" than that

	/// <summary>Driver's seat, in the car frame (metres, origin on the ground at the middle of the wheelbase).</summary>
	public static readonly Vector3 PelvisM = new Vector3( -0.09f, 0.333f, 0.56f ) * SizeFactor;
	public static readonly Vector3 EyeM = new Vector3( -0.06f, 0.333f, 1.17f ) * SizeFactor;

	/// <summary>The seats you can sit in: the driver's (left) and the passenger's (right).</summary>
	public static string[] SeatGroups => new[] { "seat_l", "seat_r" };

	/// <summary>Where the sitter's pelvis goes and where the eyes are, for a seat (the right seat mirrors the left across the car).</summary>
	public static Vector3 PelvisFor( string seat ) => seat == "seat_r" ? new Vector3( PelvisM.x, -PelvisM.y, PelvisM.z ) : PelvisM;
	public static Vector3 EyeFor( string seat ) => seat == "seat_r" ? new Vector3( EyeM.x, -EyeM.y, EyeM.z ) : EyeM;

	/// <summary>The door a seat is entered through.</summary>
	public static string DoorFor( string seat ) => seat == "seat_r" ? "door_fr" : "door_fl";

	/// <summary>Tuning for how the seated character lines up with the seat (metres in the car frame): forward, left, up. Edit to fit.</summary>
	public static Vector3 SitOffsetM = new Vector3( -0.19f, 0f, -0.09f );

	/// <summary>The steering wheel turns about its hub, along the column (pointing back at the driver, tilted up).</summary>
	public static readonly Vector3 SteeringHubM = new Vector3( 0.459f, 0.419f, 0.814f ) * SizeFactor;
	public static readonly Vector3 SteeringAxis = new Vector3( -0.94f, 0f, 0.34f ).Normal;

	/// <summary>Uniform scale that makes the imported parts the real size (1 when the import is already right).</summary>
	public static float Scale { get; private set; } = 1f;

	public static bool IsOldCar( CarDefinition def ) => def is not null && def.BodyManifest == ManifestPath;

	/// <summary>Create the "Kit Body" object with the static shell and the door/hood/trunk/steering groups.</summary>
	public static bool TryBuildBody( Scene scene, GameObject root, CarDefinition def )
	{
		var shell = Model.Load( Folder + "old_car_static.vmdl" );
		if ( shell is null || shell.IsError )
		{
			Log.Warning( "[oldcar] old_car_static.vmdl failed to load (not compiled yet?), falling back to the blockout car" );
			return false;
		}

		// The car should be 4.66 m long (the measured model). If the engine imported the parts at a different scale,
		// correct it here (every part is scaled about the car origin) and say so, so the cause can be fixed at the source.
		const float ExpectedLengthM = 4.66f;
		float modelLength = shell.Bounds.Size.x;
		float fit = modelLength > 1f ? ExpectedLengthM * M / modelLength : 1f;
		if ( MathF.Abs( fit - 1f ) < 0.03f ) fit = 1f;
		Scale = fit * SizeFactor;
		Log.Info( $"[oldcar] shell bounds {shell.Bounds.Mins} .. {shell.Bounds.Maxs} (length {modelLength:F1} units, expected {ExpectedLengthM * M:F1}), part scale {Scale:F3}" );

		GameObject body = null;
		try
		{
			body = scene.CreateObject();
			body.Name = "Kit Body";
			body.SetParent( root, false );
			body.LocalPosition = Vector3.Down * VehicleFactory.SeatHeightM( def ) * M;

			// Just the bare shell (body, engine bay, interior trim...). Every detachable group (wheels, doors, hood, seats...)
			// is added by CarAssembly, either attached to the car or lying around as a loose item.
			AddRenderer( scene, body, "Shell", shell, Vector3.Zero );
			_geometry.Clear();

			// The kit's solid belly box would fill the whole cabin; it is replaced by real, hollow collision (floor, roof, pillars,
			// firewall, panels...) so things can be put inside the car and you can crouch into it.
			root.Components.Get<BoxCollider>()?.Destroy();
			BuildBaseColliders( scene, body );
			AddGhostMesh( scene, body, shell, Vector3.Zero );   // what people walk on in the ghost ship

			Log.Info( "[oldcar] body assembled" );
			return true;
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[oldcar] body assembly threw ({e.Message}), falling back to the blockout car" );
			body?.Destroy();
			return false;
		}
	}

	/// <summary>Remove the kit's placeholder wheel visuals: the car starts with no wheels on it.</summary>
	public static void StripBlockoutWheels( GameObject root )
	{
		var wheels = root.Components.Get<VehicleController>()?.Wheels;
		if ( wheels is null ) return;
		foreach ( var wheel in wheels )
			wheel.GameObject.Children.FirstOrDefault( c => c.Name == "Visual (blockout)" )?.Destroy();
	}

	// ---- Detachable groups ---------------------------------------------------------------------------

	/// <summary>The models of one detachable group and where it sits, measured from the imported models (model units, car frame).</summary>
	public sealed class GroupGeometry
	{
		public string Group;
		public string[] Names;
		public Model[] Models;
		public Vector3 Centre;   // middle of the group's bounds
		public Vector3 Size;
	}

	private static readonly Dictionary<string, GroupGeometry> _geometry = new();

	/// <summary>Every socket on the car: the groups cut from the model plus the engine and the radiator (built in code, the model's engine bay is empty).</summary>
	public static string[] GroupNames => OldCarParts.Groups.Select( g => g.Group ).Concat( new[] { "engine", "radiator" } ).ToArray();

	/// <summary>The loose item a socket takes: all four wheel sockets take the same universal "wheel".</summary>
	public static string ItemGroupFor( string socketGroup )
		=> socketGroup.StartsWith( "wheel_" ) ? "wheel" : socketGroup.StartsWith( "seat_" ) ? "seat" : socketGroup;

	public static bool IsRightSide( string group ) => group is "wheel_fr" or "wheel_rr";

	/// <summary>Display name and real-world mass (kg) of a group or of the universal wheel.</summary>
	public static (string Display, float Mass) InfoFor( string group )
	{
		switch ( group )
		{
			case "wheel": return ("Wheel", 22f);
			case "seat": return ("Seat", 20f);
			case "engine": return ("Engine", 120f);
			case "radiator": return ("Radiator", 12f);
		}
		return OldCarParts.Info.TryGetValue( group, out var info ) ? info : (group, 10f);
	}

	public static GroupGeometry GetGeometry( string group )
	{
		// every wheel is the same wheel and every seat the same seat: the front left wheel and the left seat are the models for all of them
		if ( group == "wheel" ) group = "wheel_fl";
		if ( group == "seat" ) group = "seat_l";
		if ( group == "engine" || group == "radiator" ) return GetProceduralGeometry( group );

		if ( _geometry.TryGetValue( group, out var cached ) ) return cached;

		var entry = OldCarParts.Groups.FirstOrDefault( g => g.Group == group );
		if ( entry.Parts is null ) return null;

		var models = new List<Model>();
		var names = new List<string>();
		var bounds = new BBox();
		bool first = true;
		foreach ( var part in entry.Parts )
		{
			var model = Model.Load( $"{Folder}parts/{part}.vmdl" );
			if ( model is null || model.IsError ) { Log.Warning( $"[oldcar] part '{part}' failed to load" ); continue; }
			models.Add( model );
			names.Add( part );
			bounds = first ? model.Bounds : bounds.AddBBox( model.Bounds );
			first = false;
		}

		if ( models.Count == 0 ) return null;
		return _geometry[group] = new GroupGeometry { Group = group, Names = names.ToArray(), Models = models.ToArray(), Centre = bounds.Center, Size = bounds.Size };
	}

	/// <summary>Where a group's centre is on the assembled car, in the "Kit Body" frame (world units, 1 = 1 inch).</summary>
	public static Vector3 HomeLocal( string group ) => (GetGeometry( group )?.Centre ?? Vector3.Zero) * Scale;

	/// <summary>The group's size on the assembled car (world units).</summary>
	public static Vector3 GroupSize( string group ) => (GetGeometry( group )?.Size ?? Vector3.One * 10f) * Scale;

	// ---- engine and radiator, built in code ------------------------------------------------------------------

	private const string FlatMaterial = "materials/world/interior/flat_lit.vmat";
	private const string MetalMaterial = "materials/world/interior/flat_metal.vmat";

	private static GroupGeometry GetProceduralGeometry( string group )
	{
		if ( _geometry.TryGetValue( group, out var cached ) && cached.Models.Length > 0 && cached.Models[0].IsValid() ) return cached;

		// The real engine and radiator models (placed in the car's frame by the export). The pieces below are only a fallback
		// for when those models are missing or have not compiled yet.
		var real = Model.Load( $"{Folder}parts/{group}.vmdl" );
		if ( real is not null && !real.IsError )
		{
			return _geometry[group] = new GroupGeometry
			{
				Group = group,
				Names = new[] { group },
				Models = new[] { real },
				Centre = real.Bounds.Center,
				Size = real.Bounds.Size,
			};
		}

		// Built straight in the car's frame at the part's own place, in the model's natural size (inches), exactly like the imported
		// parts, so everything else (scale, sockets, loose items, colliders) treats it the same.
		var b = new PropMeshBuilder();
		Vector3 P( float fwd, float left, float up ) => new Vector3( fwd, left, up ) * M;
		var rubber = PropMeshBuilder.Flat( 26, 26, 28, 0.9f );

		if ( group == "engine" )
		{
			var c = new Vector3( 1.45f, 0f, 0.62f );
			Vector3 E( float f, float l, float u ) => P( c.x + f, c.y + l, c.z + u );
			b.Box( FlatMaterial, E( 0, 0, -0.16f ), new Vector3( 0.44f, 0.26f, 0.08f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 34, 35, 40, 0.5f ) );   // oil pan
			b.Box( FlatMaterial, E( 0, 0, -0.04f ), new Vector3( 0.50f, 0.34f, 0.16f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 64, 66, 72, 0.55f ) );   // block
			b.Box( FlatMaterial, E( 0, 0, 0.08f ), new Vector3( 0.50f, 0.32f, 0.08f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 96, 98, 104, 0.5f ) );   // head
			b.Box( MetalMaterial, E( 0, 0, 0.15f ), new Vector3( 0.44f, 0.24f, 0.06f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 170, 172, 180, 0.3f ) );   // valve cover
			b.Box( FlatMaterial, E( 0, 0.19f, 0.10f ), new Vector3( 0.42f, 0.07f, 0.09f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 120, 122, 128, 0.5f ) );   // intake
			b.Box( FlatMaterial, E( 0, -0.19f, 0.05f ), new Vector3( 0.42f, 0.06f, 0.08f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 118, 74, 52, 0.8f ) );   // exhaust manifold
			var alongFwd = Rotation.FromToRotation( Vector3.Up, Vector3.Forward );
			var alongLeft = Rotation.FromToRotation( Vector3.Up, Vector3.Left );
			b.Cylinder( FlatMaterial, E( 0.29f, 0, -0.02f ), 0.07f * M, 0.05f * M, 18, alongFwd, rubber );   // crank pulley
			b.Cylinder( FlatMaterial, E( -0.34f, 0, -0.02f ), 0.16f * M, 0.18f * M, 20, alongFwd, PropMeshBuilder.Flat( 90, 92, 98, 0.6f ) );   // bellhousing
			b.Cylinder( MetalMaterial, E( 0.20f, 0.20f, -0.04f ), 0.05f * M, 0.10f * M, 14, alongLeft, PropMeshBuilder.Flat( 160, 162, 170, 0.35f ) );   // alternator
		}
		else
		{
			var c = new Vector3( 1.90f, 0f, 0.60f );
			Vector3 R( float f, float l, float u ) => P( c.x + f, c.y + l, c.z + u );
			b.Box( FlatMaterial, R( 0, 0, 0 ), new Vector3( 0.05f, 0.56f, 0.40f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 28, 28, 32, 0.6f ) );   // core
			b.Box( MetalMaterial, R( 0, 0, 0.23f ), new Vector3( 0.07f, 0.58f, 0.06f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 176, 178, 184, 0.3f ) );   // top tank
			b.Box( MetalMaterial, R( 0, 0, -0.23f ), new Vector3( 0.07f, 0.58f, 0.06f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 176, 178, 184, 0.3f ) );   // bottom tank
			b.Box( FlatMaterial, R( -0.06f, 0, 0 ), new Vector3( 0.04f, 0.52f, 0.38f ) * M, 0f, true, 1f, PropMeshBuilder.Flat( 40, 41, 46, 0.7f ) );   // fan shroud
			var alongLeft = Rotation.FromToRotation( Vector3.Up, Vector3.Left );
			b.Cylinder( FlatMaterial, R( 0.0f, 0.32f, 0.22f ), 0.025f * M, 0.08f * M, 12, alongLeft, rubber );   // hose stubs
			b.Cylinder( FlatMaterial, R( 0.0f, -0.32f, -0.22f ), 0.025f * M, 0.08f * M, 12, alongLeft, rubber );
		}

		var model = b.Build( "old_car_" + group );
		return _geometry[group] = new GroupGeometry
		{
			Group = group,
			Names = new[] { group },
			Models = new[] { model },
			Centre = model.Bounds.Center,
			Size = model.Bounds.Size,
		};
	}

	/// <summary>A wheel's hub: the middle of its tyre, in the "Kit Body" frame (world units).</summary>
	public static Vector3 HubLocal( string group )
	{
		var geometry = GetGeometry( group );
		if ( geometry is null ) return Vector3.Zero;
		int tyre = Array.FindIndex( geometry.Names, n => n.EndsWith( "_tire" ) );
		return (tyre >= 0 ? geometry.Models[tyre].Bounds.Center : geometry.Centre) * Scale;
	}

	public static int WheelIndex( string group ) => group switch { "wheel_fl" => 0, "wheel_fr" => 1, "wheel_rl" => 2, "wheel_rr" => 3, _ => -1 };

	/// <summary>
	/// Put a group's parts on the car. Wheels go under the live physics wheel (the kit's WheelVisual bobs and spins them), the
	/// steering wheel is pivoted on its hub so it can turn, everything else sits in the "Kit Body" frame where it was modelled.
	/// Returns the object that holds the visuals (destroy it to take the part off).
	/// </summary>
	public static GameObject BuildAttached( Scene scene, GameObject car, string group )
	{
		int wheelIndex = WheelIndex( group );

		// every wheel socket takes the same wheel model (the front left one); the right-hand sockets turn it round so its face points outwards
		var geometry = GetGeometry( wheelIndex >= 0 ? "wheel_fl" : group.StartsWith( "seat_" ) ? "seat_l" : group );
		if ( geometry is null ) return null;

		if ( wheelIndex >= 0 )
		{
			var wheel = car.Components.Get<VehicleController>()?.Wheels[wheelIndex];
			if ( wheel is null ) return null;

			// the parts are modelled around the car origin, so they hang off the wheel's own hub by minus the model's hub
			var modelHub = HubLocal( "wheel_fl" );

			var visual = scene.CreateObject();
			visual.Name = "Visual";
			visual.SetParent( wheel.GameObject, false );
			visual.Components.Create<WheelVisual>().Wheel = wheel;

			var turned = scene.CreateObject();
			turned.Name = "Face";
			turned.SetParent( visual, false );
			turned.LocalRotation = IsRightSide( group ) ? Rotation.FromYaw( 180f ) : Rotation.Identity;
			for ( int i = 0; i < geometry.Models.Length; i++ )
				AddRenderer( scene, turned, geometry.Names[i], geometry.Models[i], -modelHub );
			return visual;
		}

		var body = car.Children.FirstOrDefault( c => c.Name == "Kit Body" );
		if ( body is null ) return null;

		// The container's origin is the point the part turns about: the steering wheel's hub, a door's hinge, the hood's back edge.
		// Everything in it hangs off that point, so rotating the container swings the part.
		var origin = PivotLocal( group );
		var container = scene.CreateObject();
		container.SetParent( body, false );
		container.Name = group == "steering" ? "steering" : "Part " + group;   // OldCarInterior turns the steering one
		container.LocalPosition = origin;

		// either seat goes in either place: the left seat's model is moved across to the right-hand seat's position
		var seatShift = group.StartsWith( "seat_" ) ? HomeLocal( group ) - HomeLocal( "seat_l" ) : Vector3.Zero;

		for ( int i = 0; i < geometry.Models.Length; i++ )
			AddRenderer( scene, container, geometry.Names[i], geometry.Models[i], -origin + seatShift );

		AddGroupCollider( scene, container, group, geometry, -origin + seatShift );
		AddGhostMeshes( scene, container, group, geometry, -origin + seatShift );
		return container;
	}

	/// <summary>Where the model of a seat socket has to be moved to from the left seat's own place (zero for the left seat).</summary>
	public static Vector3 SeatShift( string group ) => group.StartsWith( "seat_" ) ? HomeLocal( group ) - HomeLocal( "seat_l" ) : Vector3.Zero;

	/// <summary>The point (in the "Kit Body" frame, world units) a group turns about: its hinge, the steering wheel's hub, or the car origin.</summary>
	public static Vector3 PivotLocal( string group )
	{
		if ( group == "steering" ) return SteeringHubM * M;
		if ( WheelIndex( group ) >= 0 ) return HubLocal( group );
		return TryGetHinge( group, out var pivot, out _, out _ ) ? pivot : Vector3.Zero;
	}

	/// <summary>Hinge of a door, the hood or the trunk: where it turns (world units, car frame), about which axis, and how far it opens (signed degrees).</summary>
	public static bool TryGetHinge( string group, out Vector3 pivotLocal, out Vector3 axis, out float maxAngle )
	{
		pivotLocal = default; axis = default; maxAngle = 0f;
		if ( !OldCarParts.Hinges.TryGetValue( group, out var h ) ) return false;
		pivotLocal = h.Pivot * M * Scale;
		axis = h.Axis;
		maxAngle = h.MaxAngle;
		return true;
	}

	/// <summary>The exact (low-polygon) mesh of a model, for the ghost ship: people walk on this. It does not take part in the real physics.</summary>
	private static void AddGhostMesh( Scene scene, GameObject parent, Model model, Vector3 localPosition )
	{
		var mesh = LongTrip.World.Props.ColliderGenerator.LowPoly( model );
		if ( mesh is null ) return;
		var go = scene.CreateObject();
		go.Name = "Ghost mesh";
		go.SetParent( parent, false );
		go.LocalPosition = localPosition;
		go.LocalScale = Vector3.One * Scale;
		go.Components.Create<LongTrip.Gameplay.GhostMesh>().Mesh = mesh;
	}

	private static void AddGhostMeshes( Scene scene, GameObject container, string group, GroupGeometry geometry, Vector3 shift )
	{
		bool wanted = group.StartsWith( "door_" ) || group is "hood" or "trunk" or "engine" or "radiator" || group.StartsWith( "seat_" ) || group.StartsWith( "bumper_" );
		if ( !wanted ) return;
		for ( int i = 0; i < geometry.Models.Length; i++ ) AddGhostMesh( scene, container, geometry.Models[i], shift );
	}

	/// <summary>
	/// The body's collision made from the shell model itself (see <see cref="LongTrip.World.Props.ColliderGenerator.Hollow"/>): the floor, roof, pillars, panels and
	/// firewall as they really are, with the cabin and the boot open inside. False when it cannot be made (the hand-placed slabs are used then).
	/// </summary>
	private static bool BuildGeneratedBodyCollision( Scene scene, GameObject body, Model shell )
	{
		var generated = LongTrip.World.Props.ColliderGenerator.LowPolyHulls( shell, 1.4f, 450 ) ?? LongTrip.World.Props.ColliderGenerator.Hollow( shell, 4.5f, 700 );   // the simplified body mesh as convex hulls; boxes only if it is missing
		if ( generated is null ) return false;

		var go = scene.CreateObject();
		go.Name = "Body Collision";
		go.SetParent( body, false );
		go.LocalScale = Vector3.One * Scale;
		var collider = go.Components.Create<ModelCollider>();
		collider.Model = generated;
		collider.Friction = 0.1f;
		return true;
	}

	/// <summary>The collision of an attached part from its own models, in the same places as its renderers. Wheels, lights and the steering wheel have none. False if it cannot be made.</summary>
	private static bool AddGeneratedGroupCollision( Scene scene, GameObject container, string group, GroupGeometry geometry, Vector3 shift )
	{
		bool wanted = group.StartsWith( "door_" ) || group is "hood" or "trunk" or "engine" or "radiator" or "windshield" or "rear_window" || group.StartsWith( "seat_" ) || group.StartsWith( "bumper_" );
		if ( !wanted ) return true;   // nothing wanted: done (not a failure)

		bool any = false;
		for ( int i = 0; i < geometry.Models.Length; i++ )
		{
			var generated = LongTrip.World.Props.ColliderGenerator.LowPolyHulls( geometry.Models[i], 1.2f, 24 ) ?? LongTrip.World.Props.ColliderGenerator.Hollow( geometry.Models[i], group.StartsWith( "seat_" ) || group is "engine" or "radiator" ? 3f : 2.5f, 160 );
			if ( generated is null ) continue;

			var go = scene.CreateObject();
			go.Name = "Collision";
			go.SetParent( container, false );
			go.LocalPosition = shift;
			go.LocalScale = Vector3.One * Scale;
			var collider = go.Components.Create<ModelCollider>();
			collider.Model = generated;
			collider.Friction = 0.1f;
			any = true;
		}
		return any;
	}

	/// <summary>
	/// The collision that travels with an attached part: doors, hood and trunk are thin slabs (so an open door really is a door and a
	/// removed one is an opening), seats and bumpers are boxes, windscreens are slanted slabs. Wheels, lights and the steering wheel have none.
	/// </summary>
	private static void AddGroupCollider( Scene scene, GameObject container, string group, GroupGeometry geometry, Vector3 shift )
	{
		var size = geometry.Size * Scale;
		var centre = geometry.Centre * Scale + shift;
		var rotation = Rotation.Identity;
		Vector3 box;

		if ( group.StartsWith( "door_" ) ) box = new Vector3( size.x, 0.09f * M * Scale, size.z );
		else if ( group == "hood" || group == "trunk" )
		{
			float thick = 0.08f * M * Scale;
			box = new Vector3( size.x, size.y, thick );
			centre += Vector3.Up * (size.z - thick) * 0.5f;   // the top skin
		}
		else if ( group.StartsWith( "seat_" ) ) box = size * new Vector3( 1f, 0.9f, 0.9f );
		else if ( group.StartsWith( "bumper_" ) || group == "engine" ) box = size;
		else if ( group == "radiator" ) box = size;
		else if ( group == "windshield" || group == "rear_window" )
		{
			float slant = MathF.Atan2( size.z, size.x ) * 180f / MathF.PI;   // the glass runs this steeply
			box = new Vector3( MathF.Sqrt( size.x * size.x + size.z * size.z ), size.y, 2.2f * Scale );
			rotation = Rotation.FromAxis( Vector3.Left, group == "windshield" ? slant : -slant );
		}
		else return;

		var go = scene.CreateObject();
		go.Name = "Collision";
		go.SetParent( container, false );
		go.LocalPosition = centre;
		go.LocalRotation = rotation;
		// (the windscreen and the rear window are thin slanted slabs, and the ghost ship uses them as they are)
		if ( group != "windshield" && group != "rear_window" ) go.Components.Create<LongTrip.Gameplay.GhostIgnore>();
		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = box;
		collider.Friction = 0.1f;
	}

	/// <summary>A box of the fixed hollow body: centre and size in metres (forward, left, up) at the model's natural size, optionally tilted about the left axis.</summary>
	private readonly record struct Slab( float Fwd, float Left, float Up, float SizeFwd, float SizeLeft, float SizeUp, float TiltDeg = 0f );

	// (a property, not a static field: a static field keeps its old boxes across an editor hot-reload)
	private static Slab[] BaseSlabs => new Slab[]
	{
		// floor pan, roof, dashboard, centre console
		new( -0.10f, 0f, 0.30f, 3.90f, 1.50f, 0.08f ),
		new( -0.42f, 0f, 1.49f, 1.60f, 1.52f, 0.07f ),
		new( 0.74f, 0f, 0.80f, 0.34f, 1.45f, 0.45f ),
		new( 0.40f, 0f, 0.47f, 1.03f, 0.32f, 0.32f ),
		// sills under the doors
		// (their bottom edge is the body's lowest point, so a car with no wheels rests on them)
		new( -0.20f, 0.80f, 0.30f, 2.15f, 0.12f, 0.20f ), new( -0.20f, -0.80f, 0.30f, 2.15f, 0.12f, 0.20f ),
		// pillars: A (windscreen), B (between the doors), C (rear window)
		new( 0.62f, 0.72f, 1.18f, 0.14f, 0.10f, 0.50f ), new( 0.62f, -0.72f, 1.18f, 0.14f, 0.10f, 0.50f ),
		new( -0.23f, 0.74f, 0.90f, 0.13f, 0.12f, 1.14f ), new( -0.23f, -0.74f, 0.90f, 0.13f, 0.12f, 1.14f ),
		new( -1.30f, 0.74f, 1.22f, 0.14f, 0.10f, 0.55f ), new( -1.30f, -0.74f, 1.22f, 0.14f, 0.10f, 0.55f ),
		// engine bay: fenders, floor and firewall (the engine and the radiator are parts and bring their own collision)
		new( 1.50f, 0.80f, 0.62f, 1.20f, 0.07f, 0.66f ), new( 1.50f, -0.80f, 0.62f, 1.20f, 0.07f, 0.66f ),
		new( 1.55f, 0f, 0.36f, 1.15f, 1.40f, 0.06f ), new( 1.02f, 0f, 0.70f, 0.07f, 1.45f, 0.70f ),
		// boot: floor, rear panel, rear quarter panels
		new( -2.00f, 0f, 0.52f, 0.85f, 1.40f, 0.07f ), new( -2.36f, 0f, 0.78f, 0.07f, 1.45f, 0.65f ),
		new( -1.80f, 0.80f, 0.85f, 1.00f, 0.07f, 0.95f ), new( -1.80f, -0.80f, 0.85f, 1.00f, 0.07f, 0.95f ),
	};

	/// <summary>Hollow collision for the bare body: things fit inside the cabin and the boot, and you can crouch into it.</summary>
	private static void BuildBaseColliders( Scene scene, GameObject body )
	{
		var parent = scene.CreateObject();
		parent.Name = "Body Collision";
		parent.SetParent( body, false );
		// (the exact ghost mesh stands in for the slabs, except the low ones: the floors of the cabin, the engine bay and the boot, which the mesh does not have, so people can walk inside)

		float k = M * Scale;
		foreach ( var s in BaseSlabs )
		{
			var go = scene.CreateObject();
			go.Name = "Slab";
			go.SetParent( parent, false );
			go.LocalPosition = new Vector3( s.Fwd, s.Left, s.Up ) * k;
			go.LocalRotation = s.TiltDeg == 0f ? Rotation.Identity : Rotation.FromAxis( Vector3.Left, s.TiltDeg );
			if ( s.Up >= 0.6f ) go.Components.Create<LongTrip.Gameplay.GhostIgnore>();
			var collider = go.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( s.SizeFwd, s.SizeLeft, s.SizeUp ) * k;
			collider.Friction = 0.1f;
		}
	}

	/// <summary>Build the renderers for a loose item: the group's models hung off the group centre, so the item's origin is its middle.</summary>
	public static void BuildLoose( Scene scene, GameObject item, string group )
	{
		var geometry = GetGeometry( group );
		if ( geometry is null ) return;

		var visual = scene.CreateObject();
		visual.Name = "Visual";
		visual.SetParent( item, false );
		for ( int i = 0; i < geometry.Models.Length; i++ )
			AddRenderer( scene, visual, geometry.Names[i], geometry.Models[i], -geometry.Centre * Scale );
	}

	static void AddRenderer( Scene scene, GameObject parent, string name, Model model, Vector3 localPosition )
	{
		var go = scene.CreateObject();
		go.Name = name;
		go.SetParent( parent, false );
		go.LocalPosition = localPosition;
		go.LocalScale = Vector3.One * Scale;
		go.Components.Create<ModelRenderer>().Model = model;
	}
}
