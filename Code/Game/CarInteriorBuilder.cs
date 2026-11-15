using LongTrip.World.Props;

namespace LongTrip.Gameplay;

/// <summary>
/// The hatch's cabin, built from simple shapes in the kit body's frame: +X right, +Y forward, +Z up, origin at the
/// footprint centre on the ground (the same frame as the chassis shell). Distances below are metres, converted to
/// inches on the way into the mesh. Left-hand drive: the driver sits on the left (-X).
/// </summary>
public static class CarInteriorBuilder
{
	private const float M = 39.37f;
	private const string Flat = "materials/world/interior/flat_lit.vmat";
	private const string Metal = "materials/world/interior/flat_metal.vmat";
	private const string Speedo = "materials/world/interior/dial_speedo.vmat";
	private const string Tacho = "materials/world/interior/dial_rpm.vmat";

	// ---- Where things are (kit-body metres) -----------------------------------------------------

	/// <summary>The driver's eyes. Used by the cockpit camera.</summary>
	public static readonly Vector3 EyeM = new( -0.37f, -0.17f, 1.10f );

	/// <summary>Where the pelvis of the seated driver model goes.</summary>
	public static readonly Vector3 DriverSeatM = new( -0.37f, -0.20f, 0.60f );

	public static readonly Vector3 WheelCentreM = new( -0.37f, 0.22f, 0.86f );
	public static readonly Vector3 WheelNormal = new Vector3( 0f, -0.9f, 0.43f ).Normal; // faces the driver, tilted up

	public static readonly Vector3 SpeedoCentreM = new( -0.455f, 0.2905f, 1.040f );
	public static readonly Vector3 TachoCentreM = new( -0.285f, 0.2905f, 1.040f );

	// ---- Palette ---------------------------------------------------------------------------------

	private static readonly Color32 Dash = PropMeshBuilder.Flat( 104, 106, 116, 0.55f );
	private static readonly Color32 Trim = PropMeshBuilder.Flat( 94, 97, 106, 0.6f );
	private static readonly Color32 Fabric = PropMeshBuilder.Flat( 104, 108, 118, 0.92f );
	private static readonly Color32 Bolster = PropMeshBuilder.Flat( 214, 98, 26, 0.8f );    // matches the orange paint
	private static readonly Color32 Carpet = PropMeshBuilder.Flat( 62, 62, 68, 0.97f );
	private static readonly Color32 Roof = PropMeshBuilder.Flat( 172, 169, 162, 0.95f );
	private static readonly Color32 Card = PropMeshBuilder.Flat( 98, 100, 110, 0.7f );
	private static readonly Color32 Black = PropMeshBuilder.Flat( 28, 28, 32, 0.45f );
	private static readonly Color32 Screen = PropMeshBuilder.Flat( 10, 16, 26, 0.08f );
	private static readonly Color32 Chrome = PropMeshBuilder.Flat( 206, 208, 212, 0.25f );
	private static readonly Color32 Rubber = PropMeshBuilder.Flat( 34, 34, 38, 0.9f );

	private static Vector3 P( float x, float y, float z ) => new Vector3( x, y, z ) * M;
	private static Vector3 S( float x, float y, float z ) => new Vector3( x, y, z ) * M;

	/// <summary>The static cabin: everything that doesn't move.</summary>
	public static Model BuildCabin()
	{
		var b = new PropMeshBuilder();

		// ---- floor, tunnel, headliner ---------------------------------------------------------
		b.Box( Flat, P( 0, -0.34f, 0.335f ), S( 1.46f, 1.50f, 0.03f ), 0, true, 1f, Carpet );
		b.Box( Flat, P( 0, 0.05f, 0.40f ), S( 0.26f, 1.10f, 0.12f ), 0, false, 1f, Carpet );
		b.Box( Flat, P( 0, -0.34f, 1.392f ), S( 1.22f, 1.42f, 0.02f ), 0, true, 1f, Roof );

		// ---- dashboard --------------------------------------------------------------------------
		b.Box( Flat, P( 0, 0.50f, 0.72f ), S( 1.50f, 0.42f, 0.36f ), 0, true, 1f, Dash );           // main body, top at 0.90
		b.Box( Flat, P( 0, 0.60f, 0.46f ), S( 1.44f, 0.22f, 0.32f ), 0, true, 1f, Dash );           // knee area
		b.Box( Flat, P( -0.37f, 0.40f, 1.065f ), S( 0.60f, 0.21f, 0.12f ), 0, true, 1f, Dash );      // instrument hood
		b.Box( Flat, P( 0.00f, 0.285f, 0.78f ), S( 1.46f, 0.03f, 0.012f ), 0, false, 1f, Trim );    // trim line along the dash

		// instrument panel face (black) with the two dials in front of it
		b.Box( Flat, P( -0.37f, 0.296f, 1.040f ), S( 0.50f, 0.006f, 0.20f ), 0, false, 1f, Black );
		DialFace( b, Speedo, SpeedoCentreM, 0.088f );
		DialFace( b, Tacho, TachoCentreM, 0.088f );
		// small warning light strip
		b.Box( Flat, P( -0.37f, 0.294f, 1.128f ), S( 0.14f, 0.004f, 0.012f ), 0, false, 1f, PropMeshBuilder.Flat( 240, 150, 30, 0.3f ) );

		// centre stack: screen, buttons, vents
		b.Box( Flat, P( 0.0f, 0.292f, 0.84f ), S( 0.30f, 0.05f, 0.26f ), 0, false, 1f, Trim );
		b.Box( Flat, P( 0.0f, 0.266f, 0.895f ), S( 0.22f, 0.006f, 0.11f ), 0, false, 1f, Screen );
		for ( int i = -2; i <= 2; i++ )
			b.Box( Metal, P( i * 0.045f, 0.265f, 0.79f ), S( 0.028f, 0.01f, 0.028f ), 0, false, 1f, Chrome );
		for ( int i = -1; i <= 1; i += 2 )
		{
			b.Box( Flat, P( i * 0.20f, 0.285f, 0.88f ), S( 0.11f, 0.008f, 0.045f ), 0, false, 1f, Black );     // vents
			b.Box( Flat, P( i * 0.62f, 0.285f, 0.88f ), S( 0.11f, 0.008f, 0.045f ), 0, false, 1f, Black );
		}
		// glovebox
		b.Box( Flat, P( 0.40f, 0.287f, 0.72f ), S( 0.50f, 0.012f, 0.20f ), 0, false, 1f, Trim );
		b.Box( Metal, P( 0.40f, 0.28f, 0.80f ), S( 0.07f, 0.01f, 0.012f ), 0, false, 1f, Chrome );

		// steering column cowl
		b.Cylinder( Flat, P( -0.37f, 0.29f, 0.80f ), 0.05f * M, 0.16f * M, 14, Rotation.FromToRotation( Vector3.Up, WheelNormal ), Dash );

		// ---- centre console --------------------------------------------------------------------
		b.Box( Flat, P( 0, -0.12f, 0.52f ), S( 0.24f, 0.62f, 0.26f ), 0, true, 1f, Trim );
		b.Box( Flat, P( 0, -0.23f, 0.655f ), S( 0.22f, 0.30f, 0.05f ), 0, false, 1f, Fabric );        // armrest
		b.Box( Flat, P( 0, 0.10f, 0.66f ), S( 0.18f, 0.20f, 0.04f ), 0, false, 1f, Dash );
		// gear lever
		b.Cylinder( Metal, P( 0, 0.10f, 0.76f ), 0.011f * M, 0.18f * M, 8, null, Chrome );
		b.Cylinder( Flat, P( 0, 0.10f, 0.86f ), 0.026f * M, 0.05f * M, 12, null, Rubber );
		// handbrake
		b.Box( Flat, P( 0, -0.06f, 0.70f ), S( 0.035f, 0.18f, 0.03f ), 0, false, 1f, Black );

		// ---- seats -------------------------------------------------------------------------------
		Seat( b, -0.37f, -0.12f );
		Seat( b, 0.37f, -0.12f );
		RearBench( b );

		// ---- door cards, pillars -----------------------------------------------------------------
		for ( int side = -1; side <= 1; side += 2 )
		{
			float x = side * 0.735f;
			b.Box( Flat, P( x, 0.05f, 0.70f ), S( 0.035f, 0.90f, 0.56f ), 0, true, 1f, Card );              // main card
			b.Box( Flat, P( x - side * 0.02f, 0.05f, 0.935f ), S( 0.08f, 0.82f, 0.05f ), 0, false, 1f, Trim ); // armrest / sill
			b.Box( Flat, P( x - side * 0.012f, 0.06f, 0.73f ), S( 0.012f, 0.60f, 0.07f ), 0, false, 1f, Bolster ); // coloured insert
			b.Box( Metal, P( x - side * 0.03f, -0.20f, 0.88f ), S( 0.03f, 0.11f, 0.02f ), 0, false, 1f, Chrome ); // handle
			b.Cylinder( Flat, P( x - side * 0.02f, 0.30f, 0.56f ), 0.075f * M, 0.02f * M, 18, Rotation.FromToRotation( Vector3.Up, new Vector3( -side, 0, 0 ) ), Black ); // speaker
			// window-switch pad
			b.Box( Flat, P( x - side * 0.03f, 0.18f, 0.95f ), S( 0.03f, 0.10f, 0.012f ), 0, false, 1f, Black );

			// rear quarter panel
			b.Box( Flat, P( x, -0.80f, 0.68f ), S( 0.035f, 0.62f, 0.52f ), 0, true, 1f, Card );
			b.Box( Flat, P( x - side * 0.02f, -0.80f, 0.93f ), S( 0.07f, 0.60f, 0.045f ), 0, false, 1f, Trim );

			// A, B and C pillars, covered in headliner-coloured trim
			Beam( b, Flat, new Vector3( side * 0.672f, 0.70f, 0.90f ), new Vector3( side * 0.648f, 0.30f, 1.40f ), 0.115f, 0.07f, Trim );
			Beam( b, Flat, new Vector3( side * 0.735f, -0.43f, 0.93f ), new Vector3( side * 0.63f, -0.43f, 1.39f ), 0.08f, 0.05f, Trim );
			Beam( b, Flat, new Vector3( side * 0.735f, -1.05f, 0.93f ), new Vector3( side * 0.62f, -1.05f, 1.39f ), 0.08f, 0.06f, Trim );
			// side roof rails
			Beam( b, Flat, new Vector3( side * 0.62f, 0.30f, 1.385f ), new Vector3( side * 0.62f, -1.05f, 1.385f ), 0.055f, 0.03f, Roof );
		}

		// header over the windshield and rear shelf
		b.Box( Flat, P( 0, 0.30f, 1.385f ), S( 1.22f, 0.07f, 0.04f ), 0, false, 1f, Trim );
		b.Box( Flat, P( 0, -1.06f, 0.95f ), S( 1.46f, 0.06f, 0.07f ), 0, false, 1f, Trim );

		// ---- rear-view mirror, sun visors, dome light, pedals ---------------------------------------
		b.Box( Flat, P( 0, 0.275f, 1.34f ), S( 0.025f, 0.02f, 0.07f ), 0, false, 1f, Black );             // stalk
		b.Box( Flat, P( 0, 0.255f, 1.285f ), S( 0.24f, 0.03f, 0.07f ), 0, true, 1f, Black );             // mirror body
		b.Box( Metal, P( 0, 0.240f, 1.285f ), S( 0.22f, 0.004f, 0.055f ), 0, false, 1f, Chrome );
		for ( int side = -1; side <= 1; side += 2 )
			b.Box( Flat, P( side * 0.35f, 0.24f, 1.355f ), S( 0.40f, 0.20f, 0.018f ), 0, true, 1f, Roof );
		b.Box( Flat, P( 0, -0.20f, 1.378f ), S( 0.16f, 0.10f, 0.014f ), 0, false, 1f, PropMeshBuilder.Flat( 245, 240, 222, 0.3f ) );

		var pedalTilt = Rotation.FromAxis( Vector3.Forward, -25f );
		b.Box( Flat, P( -0.51f, 0.60f, 0.40f ), S( 0.075f, 0.02f, 0.11f ), pedalTilt, true, 1f, Rubber );   // clutch
		b.Box( Flat, P( -0.37f, 0.60f, 0.40f ), S( 0.075f, 0.02f, 0.11f ), pedalTilt, true, 1f, Rubber );   // brake
		b.Box( Flat, P( -0.21f, 0.62f, 0.38f ), S( 0.06f, 0.02f, 0.14f ), pedalTilt, true, 1f, Rubber );    // accelerator
		b.Box( Flat, P( -0.62f, 0.58f, 0.37f ), S( 0.14f, 0.10f, 0.03f ), 0, true, 1f, Rubber );            // dead pedal

		return b.Build( "Hatch Cabin" );
	}

	/// <summary>The steering wheel, modelled around the origin with its axis along local Z. Rotate the object to steer.</summary>
	public static Model BuildSteeringWheel()
	{
		var b = new PropMeshBuilder();
		float rim = 0.16f * M;

		b.Ring( Flat, Vector3.Zero, rim, 0.0185f * M, 28, 8, null, Black );
		b.Cylinder( Flat, Vector3.Zero, 0.05f * M, 0.045f * M, 16, null, Dash );
		b.Cylinder( Flat, Vector3.Up * 0.024f * M, 0.026f * M, 0.006f * M, 16, null, Bolster );   // horn pad accent

		// three spokes at 120-degree spacing
		for ( int i = 0; i < 3; i++ )
		{
			float a = 90f + i * 120f;
			var dir = Rotation.FromAxis( Vector3.Up, a ) * Vector3.Right;
			var rot = Rotation.LookAt( dir, Vector3.Up );
			b.Box( Flat, dir * (rim * 0.52f), new Vector3( rim * 1.0f, 0.032f * M, 0.014f * M ), rot, true, 1f, Trim );
		}
		// stitching accent at the 12 o'clock mark
		b.Box( Flat, Vector3.Right * rim + Vector3.Up * 0.012f * M, new Vector3( 0.02f * M, 0.014f * M, 0.014f * M ), 0, false, 1f, Bolster );

		return b.Build( "Hatch Steering Wheel" );
	}

	/// <summary>A gauge needle: a thin pointer along local +X, pivoting at the origin.</summary>
	public static Model BuildNeedle( float lengthM )
	{
		var b = new PropMeshBuilder();
		float len = lengthM * M;
		b.Box( Flat, new Vector3( len * 0.5f - 0.02f * M, 0, 0 ), new Vector3( len, 0.0035f * M, 0.0035f * M ), 0, true, 1f, PropMeshBuilder.Flat( 235, 60, 40, 0.3f ) );
		b.Cylinder( Flat, Vector3.Zero, 0.009f * M, 0.006f * M, 10, Rotation.FromToRotation( Vector3.Up, new Vector3( 0, 1, 0 ) ), Black );
		return b.Build( "Hatch Needle" );
	}

	// ---- helpers ------------------------------------------------------------------------------

	/// <summary>A dial face quad on the instrument panel, facing the driver (-Y).</summary>
	private static void DialFace( PropMeshBuilder b, string material, Vector3 centreM, float radiusM )
	{
		var c = centreM * M;
		float r = radiusM * M;
		// seen from the driver (looking +Y): bottom-left, bottom-right, top-right, top-left
		b.UnitQuad( material, c + new Vector3( -r, 0, -r ), c + new Vector3( r, 0, -r ), c + new Vector3( r, 0, r ), c + new Vector3( -r, 0, r ) );
	}

	/// <summary>A box running from <paramref name="a"/> to <paramref name="b"/> (metres), with a cross-section of the given size.</summary>
	private static void Beam( PropMeshBuilder builder, string material, Vector3 a, Vector3 b, float width, float depth, Color32 color )
	{
		var p0 = a * M; var p1 = b * M;
		var dir = (p1 - p0).Normal;
		var rot = Rotation.LookAt( dir, Vector3.Up );
		builder.Box( material, (p0 + p1) * 0.5f, new Vector3( (p1 - p0).Length, width * M, depth * M ), rot, true, 1f, color );
	}

	private static void Seat( PropMeshBuilder b, float x, float y )
	{
		float width = 0.46f;
		b.Box( Flat, P( x, y, 0.47f ), S( width, 0.52f, 0.12f ), 0, true, 1f, Fabric );                              // cushion
		b.Box( Flat, P( x - 0.20f, y, 0.52f ), S( 0.07f, 0.50f, 0.13f ), 0, false, 1f, Bolster );                   // cushion bolsters
		b.Box( Flat, P( x + 0.20f, y, 0.52f ), S( 0.07f, 0.50f, 0.13f ), 0, false, 1f, Bolster );

		var lean = Rotation.FromAxis( Vector3.Forward, 12f );    // backrest leans back (rotation about the kit's X axis)
		var back = P( x, y - 0.26f, 0.78f );
		b.Box( Flat, back, S( width, 0.12f, 0.60f ), lean, true, 1f, Fabric );
		b.Box( Flat, back + lean * P( -0.20f, 0.02f, 0f ), S( 0.07f, 0.14f, 0.58f ), lean, false, 1f, Bolster );
		b.Box( Flat, back + lean * P( 0.20f, 0.02f, 0f ), S( 0.07f, 0.14f, 0.58f ), lean, false, 1f, Bolster );
		b.Box( Flat, back + lean * P( 0, 0.0f, 0.40f ), S( 0.24f, 0.09f, 0.18f ), lean, true, 1f, Fabric );        // headrest
		b.Cylinder( Flat, back + lean * P( -0.06f, 0f, 0.32f ), 0.008f * M, 0.07f * M, 6, lean, Black );
		b.Cylinder( Flat, back + lean * P( 0.06f, 0f, 0.32f ), 0.008f * M, 0.07f * M, 6, lean, Black );

		// rails under the cushion
		b.Box( Flat, P( x - 0.17f, y, 0.38f ), S( 0.03f, 0.45f, 0.05f ), 0, false, 1f, Black );
		b.Box( Flat, P( x + 0.17f, y, 0.38f ), S( 0.03f, 0.45f, 0.05f ), 0, false, 1f, Black );
	}

	private static void RearBench( PropMeshBuilder b )
	{
		b.Box( Flat, P( 0, -0.78f, 0.47f ), S( 1.30f, 0.48f, 0.12f ), 0, true, 1f, Fabric );
		var lean = Rotation.FromAxis( Vector3.Forward, 14f );
		var back = P( 0, -1.00f, 0.80f );
		b.Box( Flat, back, S( 1.30f, 0.10f, 0.56f ), lean, true, 1f, Fabric );
		for ( int i = -1; i <= 1; i++ )
			b.Box( Flat, back + lean * P( i * 0.40f, 0.0f, 0.38f ), S( 0.22f, 0.08f, 0.15f ), lean, true, 1f, Fabric );
		b.Box( Flat, P( 0, -0.78f, 0.535f ), S( 0.02f, 0.46f, 0.012f ), 0, false, 1f, Black );            // seam
	}
}
