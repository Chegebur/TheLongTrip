namespace LongTrip.Gameplay;

/// <summary>
/// Anything the player can pick up: a physics object with a name, a size (how far the stretchy hands reach to either side), a
/// weld and an <see cref="Amount"/> (how full or how much is left). <see cref="CarPartItem"/> is one kind (a part of the car); the
/// others come from <see cref="ItemLibrary"/> and are made with <see cref="ItemLibrary.Spawn"/>.
/// </summary>
public class Item : Component
{
	/// <summary>The id of this item's <see cref="ItemDefinition"/> in <see cref="ItemLibrary"/> (null for car parts, which have their own rules).</summary>
	public string ItemId { get; set; }

	public string Display { get; set; }

	/// <summary>Half the item's size along its own axes (world units): how far its hands can reach to either side.</summary>
	public Vector3 HalfExtents { get; set; }

	public Rigidbody Body { get; set; }

	/// <summary>How full / how much is left, from 0 to 1 (a water bottle's water, a fuel can's fuel).</summary>
	public float Amount { get; set; } = 1f;

	/// <summary>Set by the grabber while somebody is carrying it.</summary>
	public bool IsHeld { get; set; }

	/// <summary>Time since it was last let go of (so a thrown item doesn't hurt the one who threw it).</summary>
	public TimeSince SinceReleased { get; set; } = 999f;

	/// <summary>The weld holding this item to something else (the car, another item), or null.</summary>
	public FixedJoint Weld { get; set; }

	public bool IsWelded => Weld.IsValid();

	public ItemDefinition Definition => ItemId is null ? null : ItemLibrary.Get( ItemId );

	/// <summary>Break the weld, if there is one. Returns true if there was.</summary>
	public bool Unweld()
	{
		if ( !IsWelded ) return false;
		Weld.Destroy();
		Weld = null;
		return true;
	}

	protected override void OnStart() => Body ??= Components.Get<Rigidbody>();
}
