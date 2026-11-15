namespace LongTrip.Gameplay;

/// <summary>
/// A loose car part lying around the world: an <see cref="Item"/> the player can pick up (F), turn (hold right mouse) and
/// carry to the car, where it snaps onto its socket. <see cref="Spawn"/> builds one from a detachable group of the old car.
/// </summary>
public sealed class CarPartItem : Item
{
	/// <summary>Which group of the car this is (see <c>OldCarParts</c>): "wheel", "door_rr", "hood"...</summary>
	public string Group { get; set; }

	/// <summary>Create a loose part at a place. The item's origin is the middle of the part.</summary>
	public static CarPartItem Spawn( Scene scene, string group, Vector3 position, Rotation rotation )
	{
		var size = OldCarAssembler.GroupSize( group );
		if ( size.Length < 1f ) return null;

		var (display, kilograms) = OldCarAssembler.InfoFor( group );
		float mass = kilograms * OldCarAssembler.SizeFactor * OldCarAssembler.SizeFactor;

		var go = scene.CreateObject();
		go.Name = display;
		go.Tags.Add( "pickup" );
		go.WorldPosition = position;
		go.WorldRotation = rotation;
		go.Flags |= GameObjectFlags.NotSaved;

		OldCarAssembler.BuildLoose( scene, go, group );

		var body = go.Components.Create<Rigidbody>();
		body.MassOverride = mass;
		body.LinearDamping = 0.1f;
		body.AngularDamping = 0.6f;
		body.EnhancedCcd = true;   // a thrown part must not tunnel through the road

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = size;
		collider.Center = Vector3.Zero;

		var item = go.Components.Create<CarPartItem>();
		item.Group = group;
		item.Display = display;
		item.HalfExtents = size * 0.5f;
		item.Body = body;
		return item;
	}
}
