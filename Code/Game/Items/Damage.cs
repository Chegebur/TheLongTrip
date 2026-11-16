namespace LongTrip.Gameplay;

/// <summary>
/// Something that can be hurt and broken: it has hit points, and when they run out it is removed, setting off an explosion first if
/// it has one (an explosive barrel). Items with a "health" in the data file get one; hit it with things (see <see cref="ImpactDamage"/>).
/// </summary>
public sealed class Damageable : Component
{
	[Property, Sync] public float Health { get; set; } = 50f;
	public float MaxHealth { get; set; }
	public ExplosionConfig Explosion { get; set; }
	public bool IsDead { get; private set; }

	protected override void OnStart() => MaxHealth = MathF.Max( MaxHealth, Health );

	/// <summary>Hit points are the host's to change (one referee, so two players hitting a barrel cannot both blow it up): a client's hit is sent to the host.</summary>
	public void Damage( float amount )
	{
		if ( IsDead || amount <= 0f ) return;
		if ( Networking.IsActive && !Networking.IsHost ) { DamageHost( amount ); return; }
		ApplyDamage( amount );
	}

	[Rpc.Host] private void DamageHost( float amount ) => ApplyDamage( amount );

	private void ApplyDamage( float amount )
	{
		if ( IsDead ) return;
		Health -= amount;
		if ( Health <= 0f ) Die();
	}

	private void Die()
	{
		IsDead = true;
		var position = WorldPosition;
		var self = GameObject;
		var blast = Explosion;
		var held = Components.Get<Item>();
		// anything holding fuel goes up when it breaks: the more fuel, the bigger the blast
		if ( blast is null && held is not null && held.Liquid == "fuel" && held.Litres > 3f )
			blast = new ExplosionConfig { Radius = MathF.Min( 9f, 2f + held.Litres * 0.05f ), Force = MathF.Min( 12f, 3f + held.Litres * 0.06f ), Damage = MathF.Min( 60f, 10f + held.Litres * 0.3f ) };
		if ( blast is not null ) Boom( position, blast.Radius, blast.Force, blast.Damage );
		if ( self.IsValid() ) self.Destroy();
	}

	/// <summary>Everyone sees and feels the blast; each machine pushes the things it owns, the host referees the damage (see <see cref="Blast.Explode"/>).</summary>
	[Rpc.Broadcast] private void Boom( Vector3 position, float radius, float force, float damage )
		=> Blast.Explode( Scene, position, new ExplosionConfig { Radius = radius, Force = force, Damage = damage }, GameObject );
}

/// <summary>
/// Makes an item hurt what it hits, by how fast it hits: damage is the impact's kinetic energy (half the mass times the relative speed
/// squared) times the item's damage scale, only above a minimum speed and never above the item's cap. A swung bat, a thrown bottle and a
/// falling crowbar all work the same way. The player who is holding an item, or has only just let go of it, is not hurt by it.
/// </summary>
public sealed class ImpactDamage : Component, Component.ICollisionListener
{
	public DamageConfig Config { get; set; } = new();
	public Item Item { get; set; }

	private TimeSince _lastHit = 999f;
	private TimeSince _lastSound = 999f;

	public void OnCollisionStart( Collision collision )
	{
		// only the machine that simulates this object (its owner) judges its hits: a proxy would report the same hit again, late and doubled
		if ( IsProxy ) return;

		// a knock you can hear, by what it is made of
		float knock = collision.Contact.Speed.Length * Units.UnitsToMeters;
		if ( knock > 2.5f && _lastSound > 0.18f )
		{
			_lastSound = 0f;
			Sfx.Play( Sfx.ImpactFor( Item?.Definition ), WorldPosition );
		}
		if ( Config is null || _lastHit < Config.Cooldown ) return;

		// relative speed of the two bodies at the contact, in metres per second
		float speed = collision.Contact.Speed.Length * Units.UnitsToMeters;
		if ( speed < Config.MinSpeed ) return;

		var other = collision.Other.GameObject;
		if ( !other.IsValid() ) return;

		var body = Components.Get<Rigidbody>();
		float mass = body.IsValid() && body.MassOverride > 0f ? body.MassOverride : 1f;
		float damage = MathF.Min( Config.Cap, 0.5f * mass * speed * speed * Config.Scale );
		if ( damage < 0.5f ) return;

		var target = other.Components.Get<Damageable>( FindMode.EverythingInSelfAndParent );
		var player = other.Components.Get<PlayerStats>( FindMode.EverythingInSelfAndParent );

		if ( target.IsValid() && target.GameObject != GameObject )
		{
			target.Damage( damage );
			_lastHit = 0f;
		}
		else if ( player.IsValid() && Item.IsValid() && !Item.IsHeld && Item.SinceReleased > 1.2f )
		{
			player.Damage( damage );
			_lastHit = 0f;
		}
	}
}

/// <summary>A blast: throws nearby objects away (the lighter, the further), hurts what can be hurt (fading with distance) and flashes.</summary>
public static class Blast
{
	public static void Explode( Scene scene, Vector3 position, ExplosionConfig config, GameObject source )
	{
		float radius = config.Radius * Units.MetersToUnits;
		Log.Info( $"[blast] explosion at {position} radius {config.Radius} m" );

		foreach ( var body in scene.GetAllComponents<Rigidbody>().ToList() )
		{
			if ( !body.IsValid() || !body.MotionEnabled || body.GameObject == source || body.IsProxy ) continue;   // each machine pushes what it simulates
			var offset = body.WorldPosition - position;
			float distance = offset.Length;
			if ( distance > radius ) continue;

			float falloff = 1f - distance / radius;
			var direction = (offset + Vector3.Up * 20f).Normal;   // a little upwards
			float mass = body.MassOverride > 0f ? body.MassOverride : 10f;
			body.Velocity += direction * config.Force * Units.MetersToUnits * falloff / (1f + mass / 60f);
		}

		bool referee = !Networking.IsActive || Networking.IsHost;   // only the host decides who is hurt (the damage then goes to each player's owner)
		foreach ( var target in scene.GetAllComponents<Damageable>().ToList() )
		{
			if ( !referee || !target.IsValid() || target.GameObject == source || target.IsDead ) continue;
			float distance = (target.WorldPosition - position).Length;
			if ( distance > radius ) continue;
			target.Damage( config.Damage * (1f - distance / radius) );
		}

		foreach ( var player in scene.GetAllComponents<PlayerStats>().ToList() )
		{
			if ( !referee ) break;
			float distance = (player.WorldPosition - position).Length;
			if ( distance > radius ) continue;
			player.Damage( config.Damage * (1f - distance / radius) );
		}

		// a quick orange flash
		var flash = scene.CreateObject();
		flash.Name = "Blast flash";
		flash.WorldPosition = position + Vector3.Up * 30f;
		flash.Flags |= GameObjectFlags.NotSaved;
		var light = flash.Components.Create<PointLight>();
		light.LightColor = new Color( 1f, 0.62f, 0.25f );
		light.Radius = radius * 1.2f;
		light.Intensity = 12f;
		light.Shadows = false;
		flash.Components.Create<LifeTime>().Seconds = 0.2f;
	}
}

/// <summary>Removes its object after a short time.</summary>
public sealed class LifeTime : Component
{
	public float Seconds { get; set; } = 1f;

	private TimeSince _age = 0f;

	protected override void OnUpdate()
	{
		if ( _age > Seconds ) GameObject.Destroy();
	}
}
