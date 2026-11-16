namespace LongTrip.Gameplay;

/// <summary>
/// Dresses the player's character in the armor they wear. The armor items are native s&amp;box clothing (see the "clothing" field in
/// items.json); whenever the worn pieces change, the character's clothes are rebuilt from what they were wearing to begin with plus the armor.
/// Goes on the player object.
/// </summary>
public sealed class PlayerOutfit : Component
{
	private Dresser _dresser;
	private ClothingContainer _base;
	private string _applied = "";
	private TimeSince _sinceStart = 0f;

	/// <summary>What the character is wearing right now (read from the clothes the dresser has put on), or plain underwear if nothing.</summary>
	private ClothingContainer CaptureBase()
	{
		var container = new ClothingContainer();
		var body = _dresser.BodyTarget;
		if ( body.IsValid() )
		{
			foreach ( var child in body.GameObject.Children )
			{
				if ( !child.Name.StartsWith( "Clothing - " ) ) continue;
				string name = child.Name.Substring( "Clothing - ".Length );
				var found = ResourceLibrary.GetAll<Clothing>().FirstOrDefault( c => System.IO.Path.GetFileNameWithoutExtension( c.ResourcePath ) == name );
				if ( found is not null ) container.Add( found );
			}
		}

		if ( container.Clothing.Count == 0 )
		{
			foreach ( var name in new[] { "bra_black", "y_front_pants_white" } )
			{
				var found = ResourceLibrary.GetAll<Clothing>().FirstOrDefault( c => System.IO.Path.GetFileNameWithoutExtension( c.ResourcePath ) == name );
				if ( found is not null ) container.Add( found );
			}
		}
		return container;
	}

	protected override void OnUpdate()
	{
		var inventory = PlayerInventory.Instance;
		if ( inventory is null ) return;

		_dresser ??= Components.Get<Dresser>( FindMode.EverythingInSelfAndDescendants );
		if ( !_dresser.IsValid() ) return;

		// what the character wears at the start (their own avatar), noted once it has had time to dress
		if ( _base is null )
		{
			if ( _sinceStart < 2f ) return;
			_base = CaptureBase();
		}

		string signature = string.Join( ',', inventory.Armor.Select( a => a?.ItemId ?? "-" ) );
		if ( signature == _applied ) return;
		_applied = signature;

		try
		{
			var outfit = ClothingContainer.CreateFromJson( _base.Serialize() );
			foreach ( var worn in inventory.Armor )
			{
				var path = worn is null ? null : ItemLibrary.Get( worn.ItemId )?.Clothing;
				if ( string.IsNullOrEmpty( path ) ) continue;
				var clothing = ResourceLibrary.Get<Clothing>( path );
				if ( clothing is not null ) outfit.Add( clothing );
			}
			_dresser.Apply( outfit );
		}
		catch ( System.Exception e ) { Log.Warning( $"[outfit] could not dress the player ({e.Message})" ); }
	}
}
