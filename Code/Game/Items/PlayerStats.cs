namespace LongTrip.Gameplay;

/// <summary>
/// The player's needs: food and water run down slowly and are restored by eating and drinking; with either at zero health drains,
/// and it comes back while both are well topped up. Goes on the player object; the HUD shows the values.
/// </summary>
public sealed class PlayerStats : Component
{
	public const float Max = 100f;

	[Sync] public float Health { get; private set; } = Max;
	public float Food { get; private set; } = 80f;
	public float Water { get; private set; } = 80f;

	/// <summary>Points lost per second (a full bar of 100 lasts about 50 and 33 minutes).</summary>
	[Property] public float FoodDecay { get; set; } = 0.033f;
	[Property] public float WaterDecay { get; set; } = 0.05f;

	/// <summary>Hurt the player. Health belongs to the player's owner, so a hit on somebody else's character is sent to them.</summary>
	public void Damage( float amount )
	{
		if ( IsProxy ) { DamageOwner( amount ); return; }
		Health = MathF.Max( 0f, Health - amount );
	}

	[Rpc.Owner] private void DamageOwner( float amount ) => Health = MathF.Max( 0f, Health - amount );

	public void Eat( float amount ) => Food = Math.Clamp( Food + amount, 0f, Max );
	public void Drink( float amount ) => Water = Math.Clamp( Water + amount, 0f, Max );

	protected override void OnUpdate()
	{
		if ( IsProxy ) return;   // the owner runs the needs; everyone else just sees the synced health
		float dt = Time.Delta;
		Food = MathF.Max( 0f, Food - FoodDecay * dt );
		Water = MathF.Max( 0f, Water - WaterDecay * dt );

		if ( Food <= 0f || Water <= 0f ) Health = MathF.Max( 0f, Health - 1f * dt );
		else if ( Food > 30f && Water > 30f ) Health = MathF.Min( Max, Health + 0.5f * dt );
	}
}
