using LongTrip.World.Props;

namespace LongTrip.Gameplay;

/// <summary>
/// The long rubbery arms: while you carry a part, both arms stretch from your shoulders out to either side of it, however far
/// away it is, and shrink back when you let go. Arms are plain stretched cylinders with a round hand on the end; they are drawn in
/// the world, driven every frame from the camera so they never lag behind your view.
/// </summary>
public sealed class StretchyArms : Component
{
	private const string Flat = "materials/world/interior/flat_lit.vmat";
	private static readonly Color32 Skin = PropMeshBuilder.Flat( 226, 172, 140, 0.65f );

	private static Model _armModel;
	private static Model _handModel;

	private readonly GameObject[] _arm = new GameObject[2];    // 0 = left, 1 = right
	private readonly GameObject[] _hand = new GameObject[2];
	private float _reach;   // 0 = tucked away, 1 = fully on the part

	/// <summary>Arm radius (world units) at normal length; it thins out as the arm stretches.</summary>
	[Property] public float ArmRadius { get; set; } = 2.6f;
	[Property] public float HandRadius { get; set; } = 4.4f;

	protected override void OnStart()
	{
		_armModel = BuildArm();
		_handModel = BuildHand();

		for ( int i = 0; i < 2; i++ )
		{
			_arm[i] = MakePiece( i == 0 ? "Left Arm" : "Right Arm", _armModel );
			_hand[i] = MakePiece( i == 0 ? "Left Hand" : "Right Hand", _handModel );
		}
		SetVisible( false );
	}

	protected override void OnDestroy()
	{
		for ( int i = 0; i < 2; i++ )
		{
			if ( _arm[i].IsValid() ) _arm[i].Destroy();
			if ( _hand[i].IsValid() ) _hand[i].Destroy();
		}
	}

	private GameObject MakePiece( string name, Model model )
	{
		var go = Scene.CreateObject();
		go.Name = name;
		go.Flags |= GameObjectFlags.NotSaved;
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		return go;
	}

	public void Hide()
	{
		_reach = 0f;
		SetVisible( false );
	}

	private void SetVisible( bool visible )
	{
		for ( int i = 0; i < 2; i++ )
		{
			if ( _arm[i].IsValid() ) _arm[i].Enabled = visible;
			if ( _hand[i].IsValid() ) _hand[i].Enabled = visible;
		}
	}

	/// <summary>Move the arms for this frame. <paramref name="held"/> is the part being carried, or null for no arms.</summary>
	public void Tick( CameraComponent camera, CarPartItem held, float dt )
	{
		float goal = held.IsValid() ? 1f : 0f;
		_reach = MathX.Approach( _reach, goal, dt * (goal > _reach ? 6f : 4.5f) );
		if ( _reach < 0.01f ) { SetVisible( false ); return; }
		if ( !camera.IsValid() ) return;
		SetVisible( true );

		float eased = 1f - MathF.Pow( 1f - _reach, 3f );   // fast out, slow in
		var camPos = camera.WorldPosition;
		var camRot = camera.WorldRotation;

		for ( int i = 0; i < 2; i++ )
		{
			float side = i == 0 ? 1f : -1f;   // +Y is left
			var shoulder = camPos + camRot * new Vector3( 4f, side * 15f, -15f );
			var rest = shoulder + camRot * new Vector3( 14f, side * 4f, -18f );

			// where the hand lands: the side of the part, as seen from the camera
			var target = rest;
			if ( held.IsValid() )
			{
				var axis = camRot.Left * side;
				var r = held.WorldRotation;
				var h = held.HalfExtents;
				float reachOut = MathF.Abs( Vector3.Dot( axis, r.Forward ) ) * h.x
					+ MathF.Abs( Vector3.Dot( axis, r.Left ) ) * h.y
					+ MathF.Abs( Vector3.Dot( axis, r.Up ) ) * h.z;
				target = held.WorldPosition + axis * (reachOut + 3f);
			}

			var hand = Vector3.Lerp( rest, target, eased );
			var along = hand - shoulder;
			float length = MathF.Max( 1f, along.Length );

			// the arm gets thinner the further it stretches, like a rubber band
			float thick = ArmRadius * Math.Clamp( 90f / length, 0.55f, 1.3f );

			var arm = _arm[i];
			arm.WorldPosition = shoulder;
			arm.WorldRotation = Rotation.FromToRotation( Vector3.Up, along / length );
			arm.WorldScale = new Vector3( thick, thick, length );

			var palm = _hand[i];
			palm.WorldPosition = hand;
			palm.WorldRotation = camRot;
			palm.WorldScale = new Vector3( HandRadius, HandRadius * 0.9f, HandRadius );
		}
	}

	// ---- meshes -----------------------------------------------------------------------------------------

	/// <summary>A unit cylinder along +Z (radius 1, from z = 0 to z = 1), scaled per arm.</summary>
	private static Model BuildArm()
	{
		var b = new PropMeshBuilder();
		b.Cylinder( Flat, new Vector3( 0f, 0f, 0.5f ), 1f, 1f, 12, null, Skin, true );
		return b.Build( "stretchy_arm" );
	}

	/// <summary>A unit sphere (an octahedron split twice), flat shaded.</summary>
	private static Model BuildHand()
	{
		var b = new PropMeshBuilder();
		Vector3[] v = { new( 1, 0, 0 ), new( -1, 0, 0 ), new( 0, 1, 0 ), new( 0, -1, 0 ), new( 0, 0, 1 ), new( 0, 0, -1 ) };
		int[][] faces = { new[] { 0, 2, 4 }, new[] { 2, 1, 4 }, new[] { 1, 3, 4 }, new[] { 3, 0, 4 }, new[] { 2, 0, 5 }, new[] { 1, 2, 5 }, new[] { 3, 1, 5 }, new[] { 0, 3, 5 } };

		var tris = new List<(Vector3 A, Vector3 B, Vector3 C)>();
		foreach ( var f in faces ) tris.Add( (v[f[0]], v[f[1]], v[f[2]]) );

		for ( int pass = 0; pass < 2; pass++ )
		{
			var next = new List<(Vector3, Vector3, Vector3)>();
			foreach ( var (a, bb, c) in tris )
			{
				var ab = (a + bb).Normal; var bc = (bb + c).Normal; var ca = (c + a).Normal;
				next.Add( (a, ab, ca) ); next.Add( (ab, bb, bc) ); next.Add( (ca, bc, c) ); next.Add( (ab, bc, ca) );
			}
			tris = next;
		}

		foreach ( var (a, bb, c) in tris )
		{
			// counter-clockwise seen from outside
			if ( Vector3.Dot( Vector3.Cross( bb - a, c - a ), a + bb + c ) < 0f )
				b.Triangle( Flat, a, c, bb, 1f, Skin );
			else
				b.Triangle( Flat, a, bb, c, 1f, Skin );
		}
		return b.Build( "stretchy_hand" );
	}
}
