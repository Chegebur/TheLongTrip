using LongTrip.World.Props;

namespace LongTrip.Gameplay;

/// <summary>What using an item does (left mouse while holding it).</summary>
public enum UseKind
{
	/// <summary>Nothing: left mouse throws it.</summary>
	None,

	/// <summary>Restores food.</summary>
	Eat,

	/// <summary>Restores water.</summary>
	Drink,

	/// <summary>Pours fuel into the car you are aiming at.</summary>
	Refuel,

	/// <summary>A weapon: left mouse swings it.</summary>
	Melee,

	/// <summary>Armor or a backpack: left mouse puts it on.</summary>
	Wear,
}

/// <summary>
/// How hard an item hits. Damage comes from the speed of the impact: kinetic energy (half the mass times the relative speed squared)
/// times <see cref="Scale"/>, nothing below <see cref="MinSpeed"/>, and never more than <see cref="Cap"/>.
/// </summary>
public sealed class DamageConfig
{
	/// <summary>Damage per joule of impact energy.</summary>
	public float Scale = 0.12f;

	/// <summary>Relative speed (metres per second) below which nothing is hurt.</summary>
	public float MinSpeed = 3f;

	/// <summary>The most damage one hit can do, however fast.</summary>
	public float Cap = 12f;

	/// <summary>Seconds before the same item can hurt something again.</summary>
	public float Cooldown = 0.2f;
}

/// <summary>What happens when something blows up: a blast around it that throws things and hurts.</summary>
public sealed class ExplosionConfig
{
	/// <summary>Blast radius in metres.</summary>
	public float Radius = 8f;

	/// <summary>Launch speed (metres per second) at the centre, fading to nothing at the edge.</summary>
	public float Force = 14f;

	/// <summary>Damage at the centre, fading to nothing at the edge.</summary>
	public float Damage = 45f;
}

/// <summary>How a weapon is swung (left mouse): the arc, how long it takes and how often.</summary>
public sealed class MeleeConfig
{
	public float SwingSeconds = 0.3f;

	/// <summary>Where the swing starts and ends, in degrees about the player's left axis (negative: above the aim, positive: below).</summary>
	public float StartAngle = -50f;
	public float EndAngle = 55f;

	/// <summary>Seconds before another swing can start.</summary>
	public float Cooldown = 0.5f;
}

/// <summary>One kind of item: what it is called, how big and heavy, what it looks like, what using it does and how it hurts.</summary>
/// <summary>An item that holds liquid: how much (litres), and what it starts with.</summary>
public sealed class ContainerConfig
{
	public float Capacity = 50f;
	public string Liquid;       // what it starts holding (null: empty)
	public float StartFill;     // 0..1
}

public sealed class ItemDefinition
{
	/// <summary>Set for barrels, jugs and the like: they hold <see cref="ContainerConfig.Capacity"/> litres of a liquid (use and pouring come later).</summary>
	public ContainerConfig Container;

	public string Id;
	public string Name;
	public string Category;
	public string Description;

	/// <summary>Mass in kilograms (a full container).</summary>
	public float Mass = 1f;

	/// <summary>"models/...vmdl" for a model, or "builtin:name" for one built in code.</summary>
	public string Model;

	/// <summary>Scale applied to the model, and its collision box.</summary>
	public float ModelScale = 1f;

	/// <summary>The collision box in inches (forward, left, up). Null: the model's own bounds.</summary>
	public Vector3? Size;

	public UseKind Use = UseKind.None;

	/// <summary>Food / water restored by using the whole item once (0-100).</summary>
	public float Restore;

	/// <summary>Litres of fuel in a full fuel container.</summary>
	public float Capacity;

	/// <summary>The share of the item (of its <see cref="Item.Amount"/>) one use takes, from 0 to 1.</summary>
	public float PortionPerUse = 1f;

	/// <summary>The item is used up (removed) when nothing is left.</summary>
	public bool ConsumedWhenEmpty;

	/// <summary>Whether it can be put away at all (in a hotbar slot).</summary>
	public bool Stowable = true;

	/// <summary>Armor: which slot it goes in (head, chest, legs, feet) and how much of every hit it takes away (0 to 1).</summary>
	public string ArmorSlot;
	public float Defense;

	/// <summary>Backpack: the tier it unlocks (1 small, 2 medium, 3 large).</summary>
	public int BackpackTier;

	/// <summary>The orientation it is held in, in degrees (pitch, yaw, roll) in your view. Null: however it was lying.</summary>
	public Angles? HoldAngles;

	/// <summary>How far in front of you it is held (world units). 0: where it was picked up.</summary>
	public float HoldDistance;

	/// <summary>1 = carried in one hand (the other arm stays free), 2 = carried in both.</summary>
	public int Hands = 2;

	/// <summary>Multiplier on walking speed while this is held (heavy two-handed things slow you down).</summary>
	public float MoveSpeed = 1f;

	public DamageConfig Damage = new();
	public MeleeConfig Melee;

	/// <summary>Hit points. Above zero the item can be damaged and broken.</summary>
	public float Health;

	public ExplosionConfig Explosion;
}

/// <summary>
/// The list of item kinds, loaded from <c>Assets/data/items.json</c>, and how to make one in the world. To add an item, add an entry
/// to that file (name, model, mass, what using it does, how it hurts...) and, to have it lying about at the start, to its "loot" list.
/// No code changes are needed.
/// </summary>
public static class ItemLibrary
{
	public const string DataPath = "data/items.json";

	private const string Flat = "materials/world/interior/flat_lit.vmat";
	private const string Metal = "materials/world/interior/flat_metal.vmat";

	// (rebuilt at the start of every play session, so edits to the data file are always picked up)
	private static Dictionary<string, ItemDefinition> _definitions;
	private static List<(string Id, int Count)> _loot;
	private static readonly Dictionary<string, Model> _models = new();

	public static void Reset()
	{
		_definitions = null;
		_loot = null;
		_models.Clear();
	}

	private static Dictionary<string, ItemDefinition> Definitions
	{
		get
		{
			if ( _definitions is null ) Load();
			return _definitions;
		}
	}

	public static IEnumerable<ItemDefinition> All => Definitions.Values;

	/// <summary>The items lying about at the start (id and how many), from the data file.</summary>
	public static IReadOnlyList<(string Id, int Count)> Loot
	{
		get
		{
			if ( _loot is null ) Load();
			return _loot;
		}
	}

	public static ItemDefinition Get( string id ) => id is not null && Definitions.TryGetValue( id, out var d ) ? d : null;

	// ---- the data file -------------------------------------------------------------------------------------------

	private sealed class DamageRecord { public float scale { get; set; } = 0.12f; public float minSpeed { get; set; } = 3f; public float cap { get; set; } = 12f; public float cooldown { get; set; } = 0.2f; }
	private sealed class ExplosionRecord { public float radius { get; set; } = 8f; public float force { get; set; } = 14f; public float damage { get; set; } = 45f; }
	private sealed class MeleeRecord { public float swingSeconds { get; set; } = 0.3f; public float startAngle { get; set; } = -50f; public float endAngle { get; set; } = 55f; public float cooldown { get; set; } = 0.5f; }
	private sealed class ContainerRecord { public float capacity { get; set; } = 50f; public string liquid { get; set; } public float fill { get; set; } }
	private sealed class LootRecord { public string id { get; set; } public int count { get; set; } = 1; }

	private sealed class ItemRecord
	{
		public string id { get; set; }
		public string name { get; set; }
		public string category { get; set; }
		public string description { get; set; }
		public string model { get; set; }
		public string use { get; set; }
		public float mass { get; set; } = 1f;
		public float modelScale { get; set; } = 1f;
		public float[] size { get; set; }
		public float restore { get; set; }
		public float capacity { get; set; }
		public float portionPerUse { get; set; } = 1f;
		public bool consumedWhenEmpty { get; set; }
		public bool stowable { get; set; } = true;
		public string armorSlot { get; set; }
		public float defense { get; set; }
		public int backpack { get; set; }
		public float[] hold { get; set; }
		public float holdDistance { get; set; }
		public int hands { get; set; } = 2;
		public float moveSpeed { get; set; } = 1f;
		public DamageRecord damage { get; set; }
		public MeleeRecord melee { get; set; }
		public float health { get; set; }
		public ContainerRecord container { get; set; }
		public ExplosionRecord explosion { get; set; }
	}

	private sealed class ItemFile
	{
		public List<ItemRecord> items { get; set; } = new();
		public List<LootRecord> loot { get; set; } = new();
	}

	private static void Load()
	{
		_definitions = new Dictionary<string, ItemDefinition>();
		_loot = new List<(string, int)>();

		ItemFile file = null;
		try
		{
			if ( FileSystem.Mounted.FileExists( DataPath ) )
				file = Json.Deserialize<ItemFile>( FileSystem.Mounted.ReadAllText( DataPath ) );
			else
				Log.Warning( $"[items] {DataPath} not found, using the built-in items" );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[items] {DataPath} could not be read ({e.Message}), using the built-in items" );
		}

		if ( file is null || file.items is null || file.items.Count == 0 ) file = BuiltIn();

		foreach ( var r in file.items )
		{
			if ( string.IsNullOrEmpty( r.id ) ) continue;

			var def = new ItemDefinition
			{
				Id = r.id,
				Name = r.name ?? r.id,
				Category = r.category ?? "Item",
				Description = r.description,
				Mass = MathF.Max( 0.01f, r.mass ),
				Model = r.model,
				ModelScale = r.modelScale <= 0f ? 1f : r.modelScale,
				Size = r.size is { Length: 3 } ? new Vector3( r.size[0], r.size[1], r.size[2] ) : null,
				Use = ParseUse( r.use ),
				Restore = r.restore,
				Capacity = r.capacity,
				PortionPerUse = r.portionPerUse <= 0f ? 1f : r.portionPerUse,
				ConsumedWhenEmpty = r.consumedWhenEmpty,
				Stowable = r.stowable,
				ArmorSlot = r.armorSlot, Defense = r.defense, BackpackTier = r.backpack,
				HoldAngles = r.hold is { Length: 3 } ? new Angles( r.hold[0], r.hold[1], r.hold[2] ) : null,
				HoldDistance = r.holdDistance,
				Hands = r.hands == 1 ? 1 : 2,
				MoveSpeed = r.moveSpeed <= 0f ? 1f : r.moveSpeed,
				Health = r.health,
			};

			var d = r.damage ?? new DamageRecord();
			def.Damage = new DamageConfig { Scale = d.scale, MinSpeed = d.minSpeed, Cap = d.cap, Cooldown = d.cooldown };
			if ( r.melee is { } m ) def.Melee = new MeleeConfig { SwingSeconds = m.swingSeconds, StartAngle = m.startAngle, EndAngle = m.endAngle, Cooldown = m.cooldown };
			if ( r.explosion is { } x ) def.Explosion = new ExplosionConfig { Radius = x.radius, Force = x.force, Damage = x.damage };
			if ( r.container is { } k ) def.Container = new ContainerConfig { Capacity = k.capacity, Liquid = k.liquid, StartFill = k.fill };
			if ( def.Use == UseKind.Melee && def.Melee is null ) def.Melee = new MeleeConfig();

			_definitions[def.Id] = def;
		}

		foreach ( var l in file.loot ?? new List<LootRecord>() )
			if ( !string.IsNullOrEmpty( l.id ) && l.count > 0 ) _loot.Add( (l.id, l.count) );

		Log.Info( $"[items] {_definitions.Count} item kinds loaded" );
	}

	private static UseKind ParseUse( string use ) => (use ?? "").ToLowerInvariant() switch
	{
		"eat" => UseKind.Eat,
		"drink" => UseKind.Drink,
		"refuel" => UseKind.Refuel,
		"melee" => UseKind.Melee,
		"wear" => UseKind.Wear,
		_ => UseKind.None,
	};

	/// <summary>The items used when the data file is missing or broken.</summary>
	private static ItemFile BuiltIn() => new()
	{
		items = new List<ItemRecord>
		{
			new() { id = "water_bottle", name = "Water bottle", category = "Drink", mass = 0.6f, model = "builtin:water_bottle", size = new[] { 2.8f, 2.8f, 9f }, use = "drink", restore = 40f, portionPerUse = 0.25f },
			new() { id = "canned_food", name = "Canned food", category = "Food", mass = 0.4f, model = "builtin:can", size = new[] { 3f, 3f, 4.5f }, use = "eat", restore = 35f, consumedWhenEmpty = true },
			new() { id = "fuel_can", name = "Fuel can", category = "Fuel", mass = 3f, model = "builtin:fuel_can", size = new[] { 6f, 13f, 15f }, use = "refuel", capacity = 20f, portionPerUse = 0.1f, stowable = false },
		},
		loot = new List<LootRecord> { new() { id = "water_bottle", count = 3 }, new() { id = "canned_food", count = 3 }, new() { id = "fuel_can", count = 2 } },
	};

	// ---- making one -----------------------------------------------------------------------------------

	/// <summary>Create an item of a kind at a place. <paramref name="amount"/> is how full it is (0 to 1).</summary>
	public static Item Spawn( Scene scene, string id, Vector3 position, Rotation rotation, float amount = 1f )
	{
		var def = Get( id );
		if ( def is null ) { Log.Warning( $"[items] unknown item '{id}'" ); return null; }

		var go = scene.CreateObject();
		go.Name = def.Name;
		go.Tags.Add( "pickup" );
		go.WorldPosition = position;
		go.WorldRotation = rotation;
		go.WorldScale = Vector3.One * def.ModelScale;
		go.Flags |= GameObjectFlags.NotSaved;

		var model = ModelFor( def );
		if ( model.IsValid() ) go.Components.Create<ModelRenderer>().Model = model;

		// the collision box: given in the data, or the model's own bounds (models are centred on their origin)
		var size = def.Size ?? (model.IsValid() ? model.Bounds.Size : new Vector3( 6f, 6f, 6f ));
		var centre = def.Size.HasValue || !model.IsValid() ? Vector3.Zero : model.Bounds.Center;

		var body = go.Components.Create<Rigidbody>();
		body.MassOverride = def.Mass;
		body.LinearDamping = 0.1f;
		body.AngularDamping = 0.8f;
		body.EnhancedCcd = true;

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = size;
		collider.Center = centre;

		var item = go.Components.Create<Item>();
		item.ItemId = id;
		item.Display = def.Name;
		item.HalfExtents = size * def.ModelScale * 0.5f;
		item.Amount = def.Container is { } box ? box.StartFill : Math.Clamp( amount, 0f, 1f );
		if ( def.Container is { } held ) item.Liquid = held.StartFill > 0f ? held.Liquid : null;
		item.Body = body;

		// everything hurts a little when it hits hard enough; weapons hurt a lot
		var impact = go.Components.Create<ImpactDamage>();
		impact.Config = def.Damage;
		impact.Item = item;

		if ( def.Health > 0f )
		{
			var damageable = go.Components.Create<Damageable>();
			damageable.Health = def.Health;
			damageable.MaxHealth = def.Health;
			damageable.Explosion = def.Explosion;
		}

		// in a multiplayer game the host owns it to begin with; whoever picks it up takes it over (see PlayerGrabber)
		if ( Networking.IsActive )
		{
			go.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
			go.Network.SetOrphanedMode( NetworkOrphaned.Host );
			go.NetworkSpawn();
		}

		return item;
	}

	/// <summary>The model an item is drawn with (for previews).</summary>
	public static Model GetModel( ItemDefinition def ) => ModelFor( def );

	private static Model ModelFor( ItemDefinition def )
	{
		if ( _models.TryGetValue( def.Id, out var cached ) && cached.IsValid() ) return cached;

		Model model = null;
		string path = def.Model ?? "";
		if ( path.StartsWith( "builtin:" ) )
		{
			model = path.Substring( "builtin:".Length ) switch
			{
				"water_bottle" => WaterBottle(),
				"can" => Can(),
				"fuel_can" => FuelCan(),
				_ => null,
			};
		}
		else if ( path.Length > 0 )
		{
			model = Model.Load( path );
			if ( model is null || model.IsError ) { Log.Warning( $"[items] model '{path}' for '{def.Id}' failed to load (not compiled yet?)" ); model = null; }
		}

		if ( model.IsValid() ) _models[def.Id] = model;
		return model;
	}

	// ---- built-in models (simple, built in code) ---------------------------------------------------------------------

	private static Model WaterBottle()
	{
		var b = new PropMeshBuilder();
		var axis = Rotation.Identity;   // cylinders stand along local Z
		b.Cylinder( Flat, new Vector3( 0, 0, -0.6f ), 1.4f, 7.8f, 16, axis, PropMeshBuilder.Flat( 150, 190, 232, 0.15f ) );
		b.Cylinder( Flat, new Vector3( 0, 0, 3.9f ), 0.75f, 1.2f, 12, axis, PropMeshBuilder.Flat( 236, 238, 244, 0.5f ) );
		b.Cylinder( Flat, new Vector3( 0, 0, 0.4f ), 1.43f, 2.2f, 16, axis, PropMeshBuilder.Flat( 60, 130, 210, 0.6f ) );   // label
		return b.Build( "item_water_bottle" );
	}

	private static Model Can()
	{
		var b = new PropMeshBuilder();
		b.Cylinder( Metal, Vector3.Zero, 1.5f, 4.5f, 18, Rotation.Identity, PropMeshBuilder.Flat( 186, 188, 194, 0.3f ) );
		b.Cylinder( Flat, Vector3.Zero, 1.53f, 2.8f, 18, Rotation.Identity, PropMeshBuilder.Flat( 176, 42, 36, 0.7f ) );   // label
		return b.Build( "item_can" );
	}

	private static Model FuelCan()
	{
		var b = new PropMeshBuilder();
		var red = PropMeshBuilder.Flat( 190, 40, 32, 0.5f );
		b.Box( Flat, new Vector3( 0, 0, -0.5f ), new Vector3( 6f, 13f, 14f ), 0f, true, 1f, red );
		b.Box( Flat, new Vector3( 0, 0, 7.4f ), new Vector3( 3f, 8f, 1.4f ), 0f, true, 1f, PropMeshBuilder.Flat( 150, 30, 24, 0.6f ) );   // carrying handle
		b.Cylinder( Metal, new Vector3( 0, 5f, 7.6f ), 0.9f, 1.2f, 12, Rotation.Identity, PropMeshBuilder.Flat( 40, 40, 44, 0.4f ) );   // cap
		return b.Build( "item_fuel_can" );
	}
}
