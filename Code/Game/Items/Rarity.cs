namespace LongTrip.Gameplay;

/// <summary>How special an item is. It colours the glow behind the item in the inventory and the hotbar, and the name in the hover menu.</summary>
public static class Rarity
{
	public static readonly string[] Names = { "Common", "Uncommon", "Rare", "Epic", "Legendary" };

	// r, g, b (0-255) of each rarity
	private static readonly int[][] Colors =
	{
		new[] { 190, 195, 205 },
		new[] { 90, 200, 105 },
		new[] { 80, 150, 245 },
		new[] { 175, 95, 235 },
		new[] { 245, 165, 45 },
	};

	/// <summary>Rarity index (0 = common) from a data-file word; unknown words are common.</summary>
	public static int Parse( string word ) => (word ?? "").ToLowerInvariant() switch
	{
		"uncommon" => 1,
		"rare" => 2,
		"epic" => 3,
		"legendary" => 4,
		_ => 0,
	};

	public static int Of( string itemId ) => ItemLibrary.Get( itemId )?.Rarity ?? 0;

	public static string NameOf( int rarity ) => Names[Math.Clamp( rarity, 0, Names.Length - 1 )];

	/// <summary>A CSS colour of the rarity with the given opacity.</summary>
	public static string Css( int rarity, float alpha )
	{
		var c = Colors[Math.Clamp( rarity, 0, Colors.Length - 1 )];
		return $"rgba({c[0]},{c[1]},{c[2]},{alpha.ToString( System.Globalization.CultureInfo.InvariantCulture )})";
	}

	/// <summary>
	/// The look of a slot: nothing but a faint outline when empty; with an item, a soft glow of its rarity instead of a background. Every property is
	/// always written (the style system only changes what it is given, so an old glow would otherwise stay on a slot after its item has gone).
	/// </summary>
	public static string SlotLook( string itemId, bool open = true )
	{
		if ( !open ) return "background-image: none; background-color: rgba(0,0,0,0.72); box-shadow: inset 0 0 18px rgba(0,0,0,0.85); border-color: rgba(0,0,0,0.65);";
		if ( itemId is null ) return "background-image: none; box-shadow: none; border-color: rgba(255,255,255,0.10);";

		int r = Of( itemId );
		return $"background-image: radial-gradient(circle, {Css( r, 0.42f )} 0%, {Css( r, 0.08f )} 80%); border-color: {Css( r, 0.65f )}; box-shadow: 0 0 18px {Css( r, 0.40f )};";
	}
}
