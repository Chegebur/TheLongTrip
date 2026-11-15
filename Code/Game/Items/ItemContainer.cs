namespace LongTrip.Gameplay;

/// <summary>
/// A row of numbered slots items can be put away in: the player's hotbar, and later the car's trunk or glovebox. Putting an item in removes
/// its object from the world and remembers its kind and how full it is; taking one out makes it again. Only items the data file allows
/// (<see cref="ItemDefinition.Stowable"/>) fit; car parts and big things do not.
/// </summary>
public sealed class ItemContainer : Component
{
	public sealed record Stored( string ItemId, float Amount, string Liquid = null );

	[Property] public int Capacity { get; set; } = 6;

	private Stored[] _slots = new Stored[6];

	/// <summary>The slots, in order; an empty slot is null.</summary>
	public IReadOnlyList<Stored> Slots
	{
		get
		{
			if ( _slots.Length != Capacity ) System.Array.Resize( ref _slots, Math.Max( 1, Capacity ) );
			return _slots;
		}
	}

	public int Count => Slots.Count( s => s is not null );

	/// <summary>The lowest-numbered empty slot, or -1 when all are taken.</summary>
	public int FirstFree()
	{
		var slots = Slots;
		for ( int i = 0; i < slots.Count; i++ )
			if ( slots[i] is null ) return i;
		return -1;
	}

	public Stored Get( int slot ) => slot >= 0 && slot < Slots.Count ? _slots[slot] : null;

	/// <summary>Put a stored item straight into a slot (or clear it with null).</summary>
	public void Set( int slot, Stored stored ) { if ( slot >= 0 && slot < Slots.Count ) _slots[slot] = stored; }

	public bool IsEmpty( int slot ) => slot >= 0 && slot < Slots.Count && _slots[slot] is null;

	/// <summary>Put an item away in a slot (the first free one when <paramref name="slot"/> is -1). Returns the slot used, or -1 if it doesn't fit.</summary>
	public int TryStore( Item item, int slot = -1 )
	{
		var def = item.IsValid() ? item.Definition : null;
		if ( def is null || !def.Stowable ) return -1;

		if ( slot < 0 || slot >= Slots.Count || _slots[slot] is not null ) slot = FirstFree();
		if ( slot < 0 ) return -1;

		_slots[slot] = new Stored( item.ItemId, item.Amount, item.Liquid );
		item.Unweld();
		item.GameObject.Destroy();
		return slot;
	}

	/// <summary>Take the item out of a slot, as a new object at a place. Null when the slot is empty.</summary>
	public Item Take( int slot, Vector3 position, Rotation rotation )
	{
		if ( slot < 0 || slot >= Slots.Count || _slots[slot] is null ) return null;

		var stored = _slots[slot];
		_slots[slot] = null;
		var item = ItemLibrary.Spawn( Scene, stored.ItemId, position, rotation, stored.Amount );
		if ( item.IsValid() && stored.Liquid is not null ) { item.Liquid = stored.Liquid; item.Amount = stored.Amount; }
		return item;
	}
}
