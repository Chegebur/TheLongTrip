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
}

/// <summary>One kind of item: what it is called, how big and heavy, what it looks like and what using it does. Add new kinds in <see cref="ItemLibrary"/>.</summary>
public sealed class ItemDefinition
{
	public string Id;
	public string Name;
	public string Category;

	/// <summary>Mass in kilograms (a full container).</summary>
	public float Mass = 1f;

	/// <summary>Size in inches (forward, left, up): the model and the collision box.</summary>
	public Vector3 Size = new( 4f, 4f, 4f );

	public UseKind Use = UseKind.None;

	/// <summary>Food / water restored by using the whole item once (0-100).</summary>
	public float Restore;

	/// <summary>Litres of fuel in a full fuel container.</summary>
	public float Capacity;

	/// <summary>The share of the item (of its <see cref="Item.Amount"/>) one use takes, from 0 to 1.</summary>
	public float PortionPerUse = 1f;

	/// <summary>The item is used up (removed) when nothing is left.</summary>
	public bool ConsumedWhenEmpty;

	/// <summary>Whether it fits in the backpack.</summary>
	public bool Stowable = true;

	public string Description;

	/// <summary>Builds the model, centred on its origin, in inches.</summary>
	public Func<Model> BuildModel;
}

/// <summary>
/// The list of item kinds, and how to make one in the world. To add an item: add a definition in <see cref="Build"/> (and a model
/// builder, or point it at a real model), then <c>ItemLibrary.Spawn( scene, "your_id", position, rotation )</c>.
/// </summary>
public static class ItemLibrary
{
	private const string Flat = "materials/world/interior/flat_lit.vmat";
	private const string Metal = "materials/world/interior/flat_metal.vmat";

	// (rebuilt at the start of every play session, so edits to the definitions are always picked up)
	private static Dictionary<string, ItemDefinition> _definitions;
	private static readonly Dictionary<string, Model> _models = new();

	public static void Reset()
	{
		_definitions = null;
		_models.Clear();
	}

	private static Dictionary<string, ItemDefinition> Definitions => _definitions ??= Build();

	public static IEnumerable<ItemDefinition> All => Definitions.Values;

	public static ItemDefinition Get( string id ) => id is not null && Definitions.TryGetValue( id, out var d ) ? d : null;

	private static Dictionary<string, ItemDefinition> Build()
	{
		var list = new[]
		{
			new ItemDefinition
			{
				Id = "water_bottle", Name = "Water bottle", Category = "Drink", Mass = 0.6f, Size = new Vector3( 2.8f, 2.8f, 9f ),
				Use = UseKind.Drink, Restore = 40f, PortionPerUse = 0.25f,
				Description = "A few sips each.", BuildModel = WaterBottle,
			},
			new ItemDefinition
			{
				Id = "canned_food", Name = "Canned food", Category = "Food", Mass = 0.4f, Size = new Vector3( 3f, 3f, 4.5f ),
				Use = UseKind.Eat, Restore = 35f, PortionPerUse = 1f, ConsumedWhenEmpty = true,
				Description = "Eat it in one go.", BuildModel = Can,
			},
			new ItemDefinition
			{
				Id = "fuel_can", Name = "Fuel can", Category = "Fuel", Mass = 3f, Size = new Vector3( 6f, 13f, 15f ),
				Use = UseKind.Refuel, Capacity = 20f, PortionPerUse = 0.1f, Stowable = false,
				Description = "Aim at the car and click to pour.", BuildModel = FuelCan,
			},
		};

		var map = new Dictionary<string, ItemDefinition>();
		foreach ( var d in list ) map[d.Id] = d;
		return map;
	}

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
		go.Flags |= GameObjectFlags.NotSaved;

		if ( !_models.TryGetValue( id, out var model ) || !model.IsValid() )
			_models[id] = model = def.BuildModel?.Invoke();
		if ( model.IsValid() ) go.Components.Create<ModelRenderer>().Model = model;

		var body = go.Components.Create<Rigidbody>();
		body.MassOverride = def.Mass;
		body.LinearDamping = 0.1f;
		body.AngularDamping = 0.8f;
		body.EnhancedCcd = true;

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = def.Size;
		collider.Center = Vector3.Zero;

		var item = go.Components.Create<Item>();
		item.ItemId = id;
		item.Display = def.Name;
		item.HalfExtents = def.Size * 0.5f;
		item.Amount = Math.Clamp( amount, 0f, 1f );
		item.Body = body;
		return item;
	}

	// ---- the models (simple, built in code: swap in real models by pointing BuildModel at them) ------------------

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
