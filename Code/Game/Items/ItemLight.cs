namespace LongTrip.Gameplay;

/// <summary>How a light item shines (from the "light" entry of <c>Assets/data/items.json</c>).</summary>
public sealed class LightConfig
{
	/// <summary>"spot" (a beam along the item's forward, a flashlight) or "point" (all round, a lantern).</summary>
	public string Type = "spot";

	public Color Color = new( 1f, 0.95f, 0.82f );

	/// <summary>How far it reaches (inches: 40 = about a metre).</summary>
	public float Range = 1600f;

	/// <summary>Beam angle in degrees (spot only).</summary>
	public float Angle = 34f;

	public float Intensity = 3f;

	/// <summary>Minutes a full battery lasts. A brighter light drains faster.</summary>
	public float Minutes = 10f;

	/// <summary>Where the light is, in the item's own frame (forward, left, up in inches).</summary>
	public Vector3 Offset;
}

/// <summary>
/// The light of a flashlight, a lantern and the like. It shines while <see cref="Item.LightOn"/> and there is charge in the battery
/// (<see cref="Item.Amount"/>, 1 = full), which runs down with use; when it gets low the light flickers. A light item left on the ground
/// keeps shining: that makes it a stationary light too.
/// </summary>
public sealed class ItemLight : Component
{
	private Item _item;
	private LightConfig _config;
	private GameObject _lightObject;
	private Light _light;
	private float _baseIntensity;
	private TimeUntil _nextFlicker;

	public void Setup( Item item, LightConfig config )
	{
		_item = item;
		_config = config;

		_lightObject = new GameObject( true, "Light" );
		_lightObject.Parent = GameObject;
		_lightObject.LocalPosition = config.Offset;
		_lightObject.LocalRotation = Rotation.Identity;   // looks along the item's forward
		_lightObject.Flags |= GameObjectFlags.NotSaved;

		if ( config.Type == "point" )
		{
			var point = _lightObject.AddComponent<PointLight>();
			point.Radius = config.Range;
			point.Attenuation = 1f;
			point.Shadows = false;
			_light = point;
		}
		else
		{
			var spot = _lightObject.AddComponent<SpotLight>();
			spot.Radius = config.Range;
			spot.ConeOuter = config.Angle;
			spot.ConeInner = config.Angle * 0.35f;
			spot.Attenuation = 1f;
			spot.Shadows = false;   // GraphicsSettings turns shadows on for the lights in view
			_light = spot;
		}

		_light.LightColor = config.Color;
		_light.Intensity = _baseIntensity = config.Intensity;
		_lightObject.Enabled = false;
	}

	protected override void OnUpdate()
	{
		if ( !_item.IsValid() || !_lightObject.IsValid() ) return;

		bool on = _item.LightOn && _item.Amount > 0.001f;
		if ( _lightObject.Enabled != on ) _lightObject.Enabled = on;
		if ( !on ) return;

		// the battery runs down (only the one who has it, so it is not counted twice in multiplayer)
		if ( !_item.IsProxy )
		{
			_item.Amount = MathF.Max( 0f, _item.Amount - Time.Delta / MathF.Max( 1f, _config.Minutes * 60f ) );
			if ( _item.Amount <= 0.001f ) { _item.Amount = 0f; _item.LightOn = false; }
		}

		// nearly flat: it stutters
		float brightness = 1f;
		if ( _item.Amount < 0.12f )
		{
			if ( _nextFlicker ) _nextFlicker = 0.05f + Game.Random.Float( 0.3f );
			brightness = Game.Random.Float() < 0.25f ? 0.25f : MathX.Lerp( 0.5f, 1f, _item.Amount / 0.12f );
		}
		_light.Intensity = _baseIntensity * brightness;
	}
}
