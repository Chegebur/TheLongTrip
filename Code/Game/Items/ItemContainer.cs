namespace LongTrip.Gameplay;

/// <summary>
/// Somewhere items can be put away: the player's backpack, and later the car's trunk or glovebox. Putting an item in removes its
/// object from the world and remembers its kind and how full it is; taking one out makes it again. Only items whose definition is
/// <see cref="ItemDefinition.Stowable"/> fit (car parts and fuel cans do not).
/// </summary>
public sealed class ItemContainer : Component
{
	public sealed record Stored( string ItemId, float Amount );

	[Property] public int Capacity { get; set; } = 12;

	public List<Stored> Contents { get; } = new();

	public bool IsFull => Contents.Count >= Capacity;

	/// <summary>Put an item away. Returns false (and leaves the item alone) if it doesn't fit.</summary>
	public bool TryStore( Item item )
	{
		var def = item.IsValid() ? item.Definition : null;
		if ( def is null || !def.Stowable || IsFull ) return false;

		Contents.Add( new Stored( item.ItemId, item.Amount ) );
		item.Unweld();
		item.GameObject.Destroy();
		return true;
	}

	/// <summary>Take the most recently stored item out, as a new object at a place. Null when the container is empty.</summary>
	public Item TakeLast( Vector3 position, Rotation rotation )
	{
		if ( Contents.Count == 0 ) return null;

		var last = Contents[^1];
		Contents.RemoveAt( Contents.Count - 1 );
		return ItemLibrary.Spawn( Scene, last.ItemId, position, rotation, last.Amount );
	}
}
