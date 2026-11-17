namespace LongTrip.Gameplay;

/// <summary>
/// Anything the player can pick up: a physics object with a name, a size (how far the stretchy hands reach to either side), a
/// weld and an <see cref="Amount"/> (how full or how much is left). <see cref="CarPartItem"/> is one kind (a part of the car); the
/// others come from <see cref="ItemLibrary"/> and are made with <see cref="ItemLibrary.Spawn"/>.
/// </summary>
public class Item : Component
{
	/// <summary>The id of this item's <see cref="ItemDefinition"/> in <see cref="ItemLibrary"/> (null for car parts, which have their own rules).</summary>
	[Sync] public string ItemId { get; set; }

	[Sync] public string Display { get; set; }

	/// <summary>Half the item's size along its own axes (world units): how far its hands can reach to either side.</summary>
	[Sync] public Vector3 HalfExtents { get; set; }

	public Rigidbody Body { get; set; }

	/// <summary>How full / how much is left, from 0 to 1 (a water bottle's water, a fuel can's fuel).</summary>
	[Sync] public float Amount { get; set; } = 1f;

	/// <summary>What a container item holds ("water", "fuel"...), or null while it is empty. Amount is then how full it is.</summary>
	[Sync] public string Liquid { get; set; }

	/// <summary>A flashlight or lantern that is switched on (it shines while its battery lasts).</summary>
	[Sync] public bool LightOn { get; set; }

	/// <summary>Set by the grabber while somebody is carrying it.</summary>
	[Sync] public bool IsHeld { get; set; }

	/// <summary>Time since it was last let go of (so a thrown item doesn't hurt the one who threw it).</summary>
	public TimeSince SinceReleased { get; set; } = 999f;

	/// <summary>
	/// What this item is welded to (the car, another item), or null. A weld is not a joint: the item gives up its own rigid body and becomes a child of that
	/// object, its colliders part of the object's one body. Nothing is left to stretch, give or bounce, so a welded item is as firm as the car itself.
	/// </summary>
	public GameObject WeldedTo { get; set; }

	public bool IsWelded => WeldedTo.IsValid();
	/// <summary>While the item is being simulated in a ghost ship its real object is far away; this is where it is seen in the world (null otherwise).</summary>
	public Vector3? ProxyPosition { get; set; }

	/// <summary>Where the item is in the world, as anyone sees it.</summary>
	public Vector3 SeenPosition => ProxyPosition ?? WorldPosition;



	private float _weldMass, _weldLinear, _weldAngular;
	private bool _weldCcd;

	/// <summary>Litres of liquid in a container item.</summary>
	public float Litres => Definition?.Container is { } c ? Amount * c.Capacity : 0f;

	/// <summary>A container weighs more with liquid in it: call after the contents change.</summary>
	public void RefreshMass()
	{
		if ( Definition?.Container is not { } c || Body is null ) return;
		float density = LiquidLibrary.Get( Liquid )?.Density ?? 1f;
		Body.MassOverride = Definition.Mass + Amount * c.Capacity * density;
	}

	public ItemDefinition Definition => ItemId is null ? null : ItemLibrary.Get( ItemId );

	/// <summary>Make this item part of <paramref name="target"/>: no joint, no body of its own, a rock-solid connection.</summary>
	public void WeldTo( GameObject target )
	{
		Unweld();
		if ( Body.IsValid() )
		{
			_weldMass = Body.MassOverride; _weldLinear = Body.LinearDamping; _weldAngular = Body.AngularDamping; _weldCcd = Body.EnhancedCcd;
			Body.Destroy();
			Body = null;
		}
		WeldedTo = target;
		GameObject.SetParent( target, true );   // keeps exactly where it is
	}

	/// <summary>Break the weld, if there is one: the item is loose again, with a body of its own. Returns true if there was a weld.</summary>
	public bool Unweld()
	{
		if ( !IsWelded ) return false;
		GameObject.SetParent( null, true );
		WeldedTo = null;

		var body = GameObject.Components.Create<Rigidbody>();
		body.MassOverride = _weldMass > 0f ? _weldMass : 1f;
		body.LinearDamping = _weldLinear;
		body.AngularDamping = _weldAngular;
		body.EnhancedCcd = _weldCcd;
		Body = body;
		RefreshMass();
		return true;
	}

	protected override void OnStart() => Body ??= Components.Get<Rigidbody>();
}
