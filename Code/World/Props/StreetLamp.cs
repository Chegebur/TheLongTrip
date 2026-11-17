using System;
using System.Collections.Generic;
using LongTrip.Gameplay;

namespace LongTrip.World.Props;

/// <summary>How a lamp prop shines (the "light" entry of a prop in <c>Assets/data/scatter.json</c>).</summary>
public sealed class LampSpec
{
	public Color Color = new( 1f, 0.80f, 0.52f );
	public float Range = 1500f;
	public float Angle = 75f;
	public float Intensity = 4f;

	/// <summary>Share of lamps that are dead (never light) and share that flicker. A worn-out road: not every lamp works.</summary>
	public float Dead = 0.12f;
	public float Flicker = 0.18f;
}

/// <summary>
/// A street lamp that comes on at night. The light sits under the lamp head(s), found from the model's own geometry (the highest part of it), and
/// follows the lamp when it leans, so a lamp that has tipped over a bit throws its light off to the side. Lamps far from the camera stay dark
/// (a long road of them must not cost anything), some are dead and some flicker.
/// </summary>
public sealed class StreetLamp : Component
{
	public float OnDistance { get; set; } = 11000f;

	private readonly List<GameObject> _lights = new();
	private LampSpec _spec;
	private bool _dead, _flicker;
	private TimeUntil _check = 0f;
	private TimeUntil _nextFlicker = 0f;
	private bool _on;
	private float _baseIntensity;
	private SoundHandle _buzz;

	private static readonly Dictionary<Model, List<Vector3>> _heads = new();

	/// <summary>The lamp head positions of a model (in its own frame): the middle of the top slice of its geometry, one per arm.</summary>
	private static List<Vector3> HeadsOf( Model model )
	{
		if ( _heads.TryGetValue( model, out var known ) ) return known;

		var heads = new List<Vector3>();
		try
		{
			var verts = model.GetVertices();
			float top = model.Bounds.Maxs.z;
			float slice = MathF.Max( 6f, model.Bounds.Size.z * 0.07f );
			var up = new List<Vector3>();
			foreach ( var v in verts ) if ( v.Position.z > top - slice ) up.Add( v.Position );

			if ( up.Count > 0 )
			{
				// the arms of the lamp run along its longest horizontal side
				var lo = up[0]; var hi = up[0];
				foreach ( var p in up ) { lo = Vector3.Min( lo, p ); hi = Vector3.Max( hi, p ); }
				bool alongX = (hi.x - lo.x) >= (hi.y - lo.y);
				float spread = alongX ? hi.x - lo.x : hi.y - lo.y;

				Vector3 Centre( Func<Vector3, bool> take )
				{
					var sum = Vector3.Zero; int n = 0;
					foreach ( var p in up ) if ( take( p ) ) { sum += p; n++; }
					return n > 0 ? sum / n : Vector3.Zero;
				}

				if ( spread > 60f )
				{
					float mid = alongX ? (lo.x + hi.x) * 0.5f : (lo.y + hi.y) * 0.5f;
					// a lamp head at each end: the outermost fifth of the arm on both sides
					float edge = spread * 0.22f;
					heads.Add( Centre( p => (alongX ? p.x : p.y) < lo.x * (alongX ? 1 : 0) + lo.y * (alongX ? 0 : 1) + edge ) );
					heads.Add( Centre( p => (alongX ? p.x : p.y) > (alongX ? hi.x : hi.y) - edge ) );
				}
				else heads.Add( Centre( _ => true ) );
			}
		}
		catch ( Exception e ) { Log.Warning( $"[lamp] could not read the lamp head from {model.Name} ({e.Message})" ); }

		if ( heads.Count == 0 ) heads.Add( new Vector3( 0f, 0f, model.Bounds.Maxs.z ) );
		_heads[model] = heads;
		return heads;
	}

	public void Setup( Model model, LampSpec spec, float seed )
	{
		_spec = spec;
		_baseIntensity = spec.Intensity;

		// a fixed fate per lamp, from where it stands
		float roll = (MathF.Abs( MathF.Sin( seed * 12.9898f + WorldPosition.x * 0.0123f + WorldPosition.y * 0.0371f )) * 43758.5453f) % 1f;
		_dead = roll < spec.Dead;
		_flicker = !_dead && roll < spec.Dead + spec.Flicker;
		if ( _dead ) return;

		foreach ( var head in HeadsOf( model ) )
		{
			var go = new GameObject( true, "Lamp light" );
			go.Parent = GameObject;
			go.LocalPosition = head - Vector3.Up * 14f;                  // just under the lamp head
			go.LocalRotation = Rotation.From( 90f, 0f, 0f );              // shining straight down (in the lamp's own frame: it tilts with the lamp)
			go.Flags |= GameObjectFlags.NotSaved;

			var light = go.AddComponent<SpotLight>();
			light.LightColor = spec.Color;
			light.Radius = spec.Range;
			light.ConeOuter = spec.Angle;
			light.ConeInner = spec.Angle * 0.45f;
			light.Attenuation = 1f;
			light.Intensity = spec.Intensity;
			light.Shadows = false;   // GraphicsSettings gives the lights in view their shadows

			go.Enabled = false;
			_lights.Add( go );
		}
	}

	protected override void OnUpdate()
	{
		if ( _dead || _lights.Count == 0 ) return;

		if ( _check )
		{
			_check = 0.5f;
			var cam = Scene.Camera;
			bool near = cam.IsValid() && (cam.WorldPosition - WorldPosition).Length < OnDistance;
			_on = near && GraphicsSettings.Darkness > 0.3f;
			foreach ( var go in _lights ) if ( go.IsValid() ) go.Enabled = _on;
		}

		// the hum of a lit lamp, heard only when you are close
		var camera = Scene.Camera;
		bool close = _on && camera.IsValid() && (camera.WorldPosition - WorldPosition).Length < 2600f;
		if ( close && (_buzz is null || !_buzz.IsPlaying) )
		{
			_buzz = Sfx.Loop( Sfx.LampBuzz, WorldPosition + Vector3.Up * 220f );
			if ( _buzz is not null ) _buzz.Volume = 0.35f;
		}
		else if ( !close && _buzz is not null ) { _buzz.Stop( 0.3f ); _buzz = null; }

		if ( !_on || !_flicker ) return;

		// a failing lamp stutters
		if ( _nextFlicker )
		{
			_nextFlicker = 0.04f + Game.Random.Float( 0.5f );
			bool dim = Game.Random.Float() < 0.35f;
			foreach ( var go in _lights )
			{
				if ( !go.IsValid() ) continue;
				var l = go.Components.Get<SpotLight>();
				if ( l.IsValid() ) l.Intensity = dim ? _baseIntensity * 0.1f : _baseIntensity;
			}
		}
	}
}
