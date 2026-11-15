namespace LongTrip.Gameplay;

/// <summary>
/// Makes the car a moving base. Anything standing on it (you, loose items, things you carry in) is carried along with the car's own
/// movement: each physics step the car's change of position and turn is applied to whatever is resting on it, so you can walk about on a
/// moving car and nothing slides off the back when it pulls away. Goes on the car root.
/// </summary>
public sealed class MovingBase : Component
{
	/// <summary>How far from the car's centre things are looked for (world units).</summary>
	[Property] public float Range { get; set; } = 650f;

	private Transform _previous;
	private bool _hasPrevious;
	private PlayerController _player;

	protected override void OnFixedUpdate()
	{
		if ( IsProxy ) return;   // only the machine that simulates the car carries things on it
		var now = WorldTransform.WithScale( 1f );
		if ( !_hasPrevious ) { _previous = now; _hasPrevious = true; return; }

		var delta = _previous;
		_previous = now;

		// nothing to do while the car is not moving
		if ( (now.Position - delta.Position).LengthSquared < 0.0004f && (now.Rotation.Forward - delta.Rotation.Forward).LengthSquared < 1e-8f ) return;

		// ---- the player, if walking on the car
		if ( !_player.IsValid() ) _player = Scene.GetAllComponents<PlayerController>().FirstOrDefault();
		if ( _player.IsValid() && _player.Enabled && IsStandingOnCar( _player.GameObject, _player.WorldPosition ) )
		{
			var carried = Carry( _player.WorldPosition, delta, now );
			float yaw = (now.Rotation * delta.Rotation.Inverse).Angles().yaw;
			_player.WorldPosition = carried;
			_player.EyeAngles = _player.EyeAngles + new Angles( 0f, yaw, 0f );
		}

		// ---- loose items resting on it
		foreach ( var item in Scene.GetAllComponents<Item>() )
		{
			if ( !item.IsValid() || item.IsHeld || item.Body is null || !item.Body.MotionEnabled ) continue;
			if ( (item.WorldPosition - now.Position).LengthSquared > Range * Range ) continue;
			if ( item.GameObject.IsDescendant( GameObject ) ) continue;
			if ( !IsStandingOnCar( item.GameObject, item.WorldPosition ) ) continue;

			var carried = Carry( item.WorldPosition, delta, now );
			item.WorldPosition = carried;
			item.WorldRotation = (now.Rotation * delta.Rotation.Inverse) * item.WorldRotation;
		}
	}

	/// <summary>Where a point that was fixed to the car's previous pose is now.</summary>
	private static Vector3 Carry( Vector3 point, Transform before, Transform after ) => after.PointToWorld( before.PointToLocal( point ) );

	/// <summary>Is the thing on top of the car (its collision, not just near it)?</summary>
	private bool IsStandingOnCar( GameObject thing, Vector3 position )
	{
		if ( (position - WorldPosition).LengthSquared > Range * Range ) return false;

		var tr = Scene.Trace.Ray( position + Vector3.Up * 6f, position + Vector3.Down * 18f )
			.IgnoreGameObjectHierarchy( thing ).Run();
		return tr.Hit && tr.GameObject.IsValid() && tr.GameObject.IsDescendant( GameObject );
	}
}
