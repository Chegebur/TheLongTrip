using System;
using System.Collections.Generic;

namespace LongTrip.World.Props;

/// <summary>
/// The lights inside one house. They are created switched off and only come on when the camera is close, so a
/// distant street of houses costs nothing.
/// </summary>
public sealed class HouseLights : Component
{
	/// <summary>Lights come on within this distance of the camera (world units, 1 unit = 1 inch).</summary>
	public float OnDistance { get; set; } = 6000f;

	private readonly List<GameObject> _lights = new();
	private TimeSince _lastCheck = 999f;
	private bool _on;
	private bool _noPower;

	private readonly List<GameObject> _furniture = new();
	private IReadOnlyList<FurniturePiece> _pieces;

	public void Setup( IReadOnlyList<Vector3> positions, float radius, IReadOnlyList<FurniturePiece> furniture = null )
	{
		_pieces = furniture;
		_noPower = ((int)MathF.Abs( WorldPosition.x * 0.013f + WorldPosition.y * 0.029f )) % 10 < 3;   // three houses in ten are dark

		foreach ( var p in positions )
		{
			var go = new GameObject( true, "Room Light" );
			go.Parent = GameObject;
			go.LocalPosition = p;
			go.Flags |= GameObjectFlags.NotSaved;

			var light = go.AddComponent<PointLight>();
			light.LightColor = new Color( 1.0f, 0.88f, 0.70f );   // warm
			light.Radius = radius;
			light.Attenuation = 0.9f;
			light.Intensity = 1.3f;
			light.Shadows = false;

			go.Enabled = false;   // switched on by OnUpdate when the camera is near
			_lights.Add( go );
		}
	}

	protected override void OnUpdate()
	{
		if ( _lastCheck < 0.5f ) return;
		_lastCheck = 0f;

		var camera = Scene.Camera;
		if ( !camera.IsValid() ) return;

		// the lights in the house come on at dusk (and not every house has power)
		// the lamps are on at dusk, and always when you are inside (a house is lit from within); not every house has power
		var local = WorldRotation.Inverse * (camera.WorldPosition - WorldPosition);
		float footprintScale = MathF.Max( 0.01f, WorldScale.x );
		bool inside = MathF.Abs( local.x ) < 262f * footprintScale && MathF.Abs( local.y ) < 352f * footprintScale && local.z > -20f && local.z < 300f * footprintScale;
		bool near = (camera.WorldPosition - WorldPosition).Length < OnDistance && !_noPower && (inside || LongTrip.Gameplay.GraphicsSettings.Darkness > 0.25f);
		if ( near == _on ) return;
		_on = near;

		foreach ( var go in _lights )
			if ( go.IsValid() ) go.Enabled = near;

		// The furniture models are only created the first time you get close, and hidden again when you leave.
		if ( near && _furniture.Count == 0 && _pieces is not null )
		{
			foreach ( var piece in _pieces )
			{
				var model = Model.Load( piece.Model );
				if ( model is null || model.IsError ) continue;

				var go = new GameObject( true, "Furniture" );
				go.Parent = GameObject;
				go.LocalPosition = piece.Position;
				go.LocalRotation = Rotation.FromYaw( piece.Yaw );
				go.LocalScale = piece.Scale;
				go.Flags |= GameObjectFlags.NotSaved;
				go.AddComponent<ModelRenderer>().Model = model;
				_furniture.Add( go );
			}
		}
		foreach ( var go in _furniture )
			if ( go.IsValid() ) go.Enabled = near;
	}
}
