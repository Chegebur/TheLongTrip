namespace LongTrip.Gameplay;

/// <summary>
/// Everything the player carries besides what is in the hands: the pockets and backpack slots (<see cref="Storage"/>), the worn armor, and
/// the backpack tier. The hotbar is the <see cref="ItemContainer"/> on the same object. Pockets are always there; each backpack tier adds
/// four more slots (small, medium, large). Goes on the player object.
/// </summary>
public sealed class PlayerInventory : Component
{
	public static PlayerInventory Instance { get; private set; }

	public const int PocketSlots = 4;
	public const int SlotsPerTier = 4;
	public const int MaxStorage = PocketSlots + 3 * SlotsPerTier;

	public static readonly string[] ArmorSlots = { "head", "chest", "legs", "feet" };

	public ItemContainer.Stored[] Storage { get; } = new ItemContainer.Stored[MaxStorage];
	public ItemContainer.Stored[] Armor { get; } = new ItemContainer.Stored[4];

	/// <summary>0 = pockets only, 1 = small, 2 = medium, 3 = large backpack.</summary>
	public int BackpackTier { get; set; }

	public ItemContainer Hotbar => Components.Get<ItemContainer>();

	public int Unlocked => PocketSlots + BackpackTier * SlotsPerTier;
	public bool IsUnlocked( int slot ) => slot < Unlocked;

	/// <summary>The share of each hit that gets through (armor takes the rest, never more than 70%).</summary>
	public float DamageFactor
	{
		get
		{
			float defense = 0f;
			foreach ( var a in Armor )
				if ( a is not null ) defense += ItemLibrary.Get( a.ItemId )?.Defense ?? 0f;
			return 1f - MathF.Min( 0.7f, defense );
		}
	}

	protected override void OnEnabled() => Instance = this;
	protected override void OnStart() => Instance = this;
	protected override void OnDestroy() { if ( Instance == this ) Instance = null; }

	/// <summary>Where an item can be put away: the hotbar first, then the pockets and backpack. Returns false when everything is full.</summary>
	public bool TryAdd( ItemContainer.Stored stored )
	{
		var hotbar = Hotbar;
		int h = hotbar is not null ? hotbar.FirstFree() : -1;
		if ( h >= 0 ) { hotbar.Set( h, stored ); return true; }
		return TryAddStorage( stored );
	}

	public bool TryAddStorage( ItemContainer.Stored stored )
	{
		for ( int i = 0; i < Unlocked && i < MaxStorage; i++ )
			if ( Storage[i] is null ) { Storage[i] = stored; return true; }
		return false;
	}

	/// <summary>How many of an item are in the hotbar and storage together.</summary>
	public int Count( string id )
	{
		int n = 0;
		var hotbar = Hotbar;
		for ( int i = 0; hotbar is not null && i < hotbar.Slots.Count; i++ ) if ( hotbar.Get( i )?.ItemId == id ) n++;
		for ( int i = 0; i < Unlocked && i < MaxStorage; i++ ) if ( Storage[i]?.ItemId == id ) n++;
		return n;
	}

	/// <summary>Remove some of an item from storage first, then the hotbar. Returns false (and takes nothing) if there are not enough.</summary>
	public bool Consume( string id, int count )
	{
		if ( Count( id ) < count ) return false;
		var hotbar = Hotbar;
		for ( int i = MaxStorage - 1; i >= 0 && count > 0; i-- )
			if ( Storage[i]?.ItemId == id ) { Storage[i] = null; count--; }
		for ( int i = 0; hotbar is not null && i < hotbar.Slots.Count && count > 0; i++ )
			if ( hotbar.Get( i )?.ItemId == id ) { hotbar.Set( i, null ); count--; }
		return true;
	}

	// ---- nails (they let a building piece go where nothing holds it up) ----

	public const string NailId = "nails";

	/// <summary>How many nails you have: in your hands, the hotbar and the pockets and pack. A box of nails holds <see cref="ItemDefinition.Uses"/> of them.</summary>
	public int NailsLeft
	{
		get
		{
			float uses = ItemLibrary.Get( NailId )?.Uses ?? 1;
			float total = 0f;
			var held = PlayerGrabber.Instance?.Held;
			if ( held.IsValid() && held.ItemId == NailId ) total += held.Amount * uses;
			var hotbar = Hotbar;
			for ( int i = 0; hotbar is not null && i < hotbar.Slots.Count; i++ ) if ( hotbar.Get( i )?.ItemId == NailId ) total += hotbar.Get( i ).Amount * uses;
			for ( int i = 0; i < Unlocked && i < MaxStorage; i++ ) if ( Storage[i]?.ItemId == NailId ) total += Storage[i].Amount * uses;
			return (int)MathF.Floor( total + 0.001f );
		}
	}

	/// <summary>Use up one nail (from what you hold first, then the pockets, then the hotbar). An empty box is thrown away. False if there are none.</summary>
	public bool ConsumeNail()
	{
		float uses = MathF.Max( 1f, ItemLibrary.Get( NailId )?.Uses ?? 1 );
		float one = 1f / uses;

		var held = PlayerGrabber.Instance?.Held;
		if ( held.IsValid() && held.ItemId == NailId && held.Amount * uses >= 0.999f )
		{
			held.Amount -= one;
			if ( held.Amount * uses < 0.5f ) held.GameObject.Destroy();
			return true;
		}

		for ( int i = 0; i < MaxStorage && i < Unlocked; i++ )
		{
			var s = Storage[i];
			if ( s?.ItemId != NailId || s.Amount * uses < 0.999f ) continue;
			var left = s.Amount - one;
			Storage[i] = left * uses < 0.5f ? null : s with { Amount = left };
			return true;
		}

		var hotbar = Hotbar;
		for ( int i = 0; hotbar is not null && i < hotbar.Slots.Count; i++ )
		{
			var s = hotbar.Get( i );
			if ( s?.ItemId != NailId || s.Amount * uses < 0.999f ) continue;
			var left = s.Amount - one;
			hotbar.Set( i, left * uses < 0.5f ? null : s with { Amount = left } );
			return true;
		}
		return false;
	}

	/// <summary>Index into <see cref="ArmorSlots"/> an item goes in, or -1 if it is not armor.</summary>
	public static int ArmorIndex( string itemId )
	{
		var def = ItemLibrary.Get( itemId );
		return def?.ArmorSlot is null ? -1 : System.Array.IndexOf( ArmorSlots, def.ArmorSlot.ToLowerInvariant() );
	}

	/// <summary>Put on a piece of armor. Whatever was in that slot goes back into storage. False if it is not armor or there is no room for the old piece.</summary>
	public bool Wear( ItemContainer.Stored stored )
	{
		int slot = ArmorIndex( stored.ItemId );
		if ( slot < 0 ) return false;

		var old = Armor[slot];
		Armor[slot] = stored;
		if ( old is not null && !TryAdd( old ) ) { Armor[slot] = old; return false; }
		return true;
	}

	/// <summary>Put on a backpack: it sets the tier (a bigger pack replaces a smaller one). False if you already have an equal or bigger one.</summary>
	public bool WearPack( int tier )
	{
		if ( tier <= BackpackTier ) return false;
		BackpackTier = tier;
		return true;
	}
}

/// <summary>One crafting recipe, from <c>Assets/data/recipes.json</c>.</summary>
public sealed class Recipe
{
	public string Id, Name, Description, Output;
	public int OutputCount = 1;
	public List<(string Item, int Count)> Inputs = new();
}

public static class RecipeLibrary
{
	public const string DataPath = "data/recipes.json";

	private sealed class InputRecord { public string item { get; set; } public int count { get; set; } = 1; }
	private sealed class RecipeRecord
	{
		public string id { get; set; }
		public string name { get; set; }
		public string description { get; set; }
		public string output { get; set; }
		public int count { get; set; } = 1;
		public List<InputRecord> inputs { get; set; } = new();
	}
	private sealed class RecipeFile { public List<RecipeRecord> recipes { get; set; } = new(); }

	private static List<Recipe> _all;
	public static void Reset() => _all = null;

	public static IReadOnlyList<Recipe> All
	{
		get
		{
			if ( _all is not null ) return _all;
			_all = new List<Recipe>();
			try
			{
				if ( !FileSystem.Mounted.FileExists( DataPath ) ) return _all;
				var file = Json.Deserialize<RecipeFile>( FileSystem.Mounted.ReadAllText( DataPath ) );
				foreach ( var r in file?.recipes ?? new() )
				{
					if ( string.IsNullOrEmpty( r.output ) || ItemLibrary.Get( r.output ) is null ) continue;
					var recipe = new Recipe { Id = r.id ?? r.output, Name = r.name ?? ItemLibrary.Get( r.output ).Name, Description = r.description, Output = r.output, OutputCount = Math.Max( 1, r.count ) };
					foreach ( var i in r.inputs ) if ( !string.IsNullOrEmpty( i.item ) ) recipe.Inputs.Add( (i.item, Math.Max( 1, i.count )) );
					_all.Add( recipe );
				}
			}
			catch ( System.Exception e ) { Log.Warning( $"[recipes] could not read {DataPath} ({e.Message})" ); }
			return _all;
		}
	}
}
