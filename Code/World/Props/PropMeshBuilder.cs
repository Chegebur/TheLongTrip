using System;
using System.Collections.Generic;

namespace LongTrip.World.Props;

/// <summary>
/// Collects geometry grouped by material and turns it into one <see cref="Model"/> with a sub-mesh per material,
/// plus optional box collision shapes. Front faces are counter-clockwise seen from outside (the terrain's convention).
/// <list type="bullet">
/// <item>Textured materials use UVs in world units divided by <see cref="TileSize"/>.</item>
/// <item>The flat-colour material (<c>flat_lit</c>) ignores UVs: per-vertex colour is the albedo and its alpha the roughness.</item>
/// </list>
/// </summary>
public sealed class PropMeshBuilder
{
	private sealed class Part
	{
		public readonly List<Vertex> Vertices = new();
		public readonly List<int> Indices = new();
	}

	private readonly Dictionary<string, Part> _parts = new();
	private readonly List<(Vector3 Extents, Vector3 Center, Rotation Rotation)> _collision = new();

	/// <summary>World units (inches) covered by one repeat of a textured material.</summary>
	public float TileSize { get; set; } = 110f;

	/// <summary>Colour used when a call doesn't give one: white for textured materials.</summary>
	public static readonly Color32 White = new( 255, 255, 255, 255 );

	/// <summary>A flat-material colour. <paramref name="roughness"/> is stored in alpha.</summary>
	public static Color32 Flat( int r, int g, int b, float roughness = 0.7f )
		=> new( (byte)r, (byte)g, (byte)b, (byte)Math.Clamp( roughness * 255f, 0f, 255f ) );

	private Part PartFor( string material )
	{
		if ( !_parts.TryGetValue( material, out var part ) )
			_parts[material] = part = new Part();
		return part;
	}

	private static Vertex V( Vector3 p, Vector3 n, Vector3 t, Vector4 uv, Color32 c )
	{
		var v = new Vertex( p, n, t, uv );
		v.Color = c;
		return v;
	}

	// ---- Collision -------------------------------------------------------------------------

	/// <summary>Add a solid box (full size, not half) to the model's collision.</summary>
	public void Collide( Vector3 center, Vector3 size, Rotation? rotation = null )
		=> _collision.Add( (size * 0.5f, center, rotation ?? Rotation.Identity) );

	public int CollisionCount => _collision.Count;

	// ---- Primitives ------------------------------------------------------------------------

	/// <summary>
	/// One quad. Corners are bottom-left, bottom-right, top-right, top-left as seen from the front, so the outward
	/// normal is (bottom-right - bottom-left) x (top-left - bottom-left).
	/// <paramref name="u0"/> / <paramref name="v0"/> offset the UVs (in world units) so a wall cut into pieces
	/// still shows one continuous texture.
	/// </summary>
	public void Quad( string material, Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, float uvScale = 1f, Color32? color = null, float u0 = 0f, float v0 = 0f )
	{
		var part = PartFor( material );
		var across = br - bl;
		var up = tl - bl;
		var normal = Vector3.Cross( across, up ).Normal;
		var tangent = across.Normal;
		var c = color ?? White;

		float uA = u0 / TileSize * uvScale, vA = v0 / TileSize * uvScale;
		float uw = uA + across.Length / TileSize * uvScale;
		float vh = vA + up.Length / TileSize * uvScale;

		int b = part.Vertices.Count;
		part.Vertices.Add( V( bl, normal, tangent, new Vector4( uA, vA, 0, 0 ), c ) );
		part.Vertices.Add( V( br, normal, tangent, new Vector4( uw, vA, 0, 0 ), c ) );
		part.Vertices.Add( V( tr, normal, tangent, new Vector4( uw, vh, 0, 0 ), c ) );
		part.Vertices.Add( V( tl, normal, tangent, new Vector4( uA, vh, 0, 0 ), c ) );

		part.Indices.Add( b ); part.Indices.Add( b + 1 ); part.Indices.Add( b + 2 );
		part.Indices.Add( b ); part.Indices.Add( b + 2 ); part.Indices.Add( b + 3 );
	}

	/// <summary>A quad whose UVs run 0..1 across it exactly once (gauge faces, labels).</summary>
	public void UnitQuad( string material, Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Color32? color = null )
	{
		var part = PartFor( material );
		var normal = Vector3.Cross( br - bl, tl - bl ).Normal;
		var tangent = (br - bl).Normal;
		var c = color ?? White;
		int b = part.Vertices.Count;
		part.Vertices.Add( V( bl, normal, tangent, new Vector4( 0, 1, 0, 0 ), c ) );
		part.Vertices.Add( V( br, normal, tangent, new Vector4( 1, 1, 0, 0 ), c ) );
		part.Vertices.Add( V( tr, normal, tangent, new Vector4( 1, 0, 0, 0 ), c ) );
		part.Vertices.Add( V( tl, normal, tangent, new Vector4( 0, 0, 0, 0 ), c ) );
		part.Indices.Add( b ); part.Indices.Add( b + 1 ); part.Indices.Add( b + 2 );
		part.Indices.Add( b ); part.Indices.Add( b + 2 ); part.Indices.Add( b + 3 );
	}

	/// <summary>A triangle with outward normal (b - a) x (c - a).</summary>
	public void Triangle( string material, Vector3 a, Vector3 b, Vector3 c, float uvScale = 1f, Color32? color = null )
	{
		var part = PartFor( material );
		var normal = Vector3.Cross( b - a, c - a ).Normal;
		var tangent = (b - a).Normal;
		var col = color ?? White;
		int baseIndex = part.Vertices.Count;

		Vector4 UV( Vector3 p ) => new( Vector3.Dot( p - a, tangent ) / TileSize * uvScale, Vector3.Dot( p - a, Vector3.Cross( normal, tangent ) ) / TileSize * uvScale, 0, 0 );

		foreach ( var p in new[] { a, b, c } )
			part.Vertices.Add( V( p, normal, tangent, UV( p ), col ) );
		part.Indices.Add( baseIndex ); part.Indices.Add( baseIndex + 1 ); part.Indices.Add( baseIndex + 2 );
	}

	/// <summary>A box, rotated about Z by <paramref name="yaw"/> degrees. All faces but (optionally) the bottom.</summary>
	public void Box( string material, Vector3 center, Vector3 size, float yaw = 0f, bool bottom = false, float uvScale = 1f, Color32? color = null )
		=> Box( material, center, size, Rotation.FromYaw( yaw ), bottom, uvScale, color );

	/// <summary>A box with any rotation.</summary>
	public void Box( string material, Vector3 center, Vector3 size, Rotation rot, bool bottom = false, float uvScale = 1f, Color32? color = null )
	{
		var h = size * 0.5f;
		Vector3 P( float x, float y, float z ) => center + rot * new Vector3( x * h.x, y * h.y, z * h.z );

		Quad( material, P( 1, -1, -1 ), P( 1, 1, -1 ), P( 1, 1, 1 ), P( 1, -1, 1 ), uvScale, color );   // +X
		Quad( material, P( -1, 1, -1 ), P( -1, -1, -1 ), P( -1, -1, 1 ), P( -1, 1, 1 ), uvScale, color ); // -X
		Quad( material, P( 1, 1, -1 ), P( -1, 1, -1 ), P( -1, 1, 1 ), P( 1, 1, 1 ), uvScale, color );    // +Y
		Quad( material, P( -1, -1, -1 ), P( 1, -1, -1 ), P( 1, -1, 1 ), P( -1, -1, 1 ), uvScale, color );  // -Y
		Quad( material, P( -1, -1, 1 ), P( 1, -1, 1 ), P( 1, 1, 1 ), P( -1, 1, 1 ), uvScale, color );     // +Z
		if ( bottom )
			Quad( material, P( -1, 1, -1 ), P( 1, 1, -1 ), P( 1, -1, -1 ), P( -1, -1, -1 ), uvScale, color ); // -Z
	}

	/// <summary>A cylinder with its axis along the rotation's local Z, centred on <paramref name="center"/>.</summary>
	public void Cylinder( string material, Vector3 center, float radius, float height, int segments, Rotation? rotation = null, Color32? color = null, bool caps = true )
	{
		var rot = rotation ?? Rotation.Identity;
		var c = color ?? White;
		var part = PartFor( material );
		float h = height * 0.5f;

		for ( int i = 0; i < segments; i++ )
		{
			float a0 = MathF.PI * 2f * i / segments, a1 = MathF.PI * 2f * (i + 1) / segments;
			var d0 = new Vector3( MathF.Cos( a0 ), MathF.Sin( a0 ), 0 );
			var d1 = new Vector3( MathF.Cos( a1 ), MathF.Sin( a1 ), 0 );

			Vector3 W( Vector3 d, float z ) => center + rot * (d * radius + Vector3.Up * z);

			// side: outward normal is the radial direction
			int b = part.Vertices.Count;
			var n0 = rot * d0; var n1 = rot * d1;
			var t = rot * Vector3.Up;
			part.Vertices.Add( V( W( d0, -h ), n0, t, Vector4.Zero, c ) );
			part.Vertices.Add( V( W( d1, -h ), n1, t, Vector4.Zero, c ) );
			part.Vertices.Add( V( W( d1, h ), n1, t, Vector4.Zero, c ) );
			part.Vertices.Add( V( W( d0, h ), n0, t, Vector4.Zero, c ) );
			part.Indices.Add( b ); part.Indices.Add( b + 1 ); part.Indices.Add( b + 2 );
			part.Indices.Add( b ); part.Indices.Add( b + 2 ); part.Indices.Add( b + 3 );

			if ( caps )
			{
				// top cap (normal +Z) and bottom cap (normal -Z)
				var up = rot * Vector3.Up;
				int tb = part.Vertices.Count;
				part.Vertices.Add( V( center + up * h, up, t, Vector4.Zero, c ) );
				part.Vertices.Add( V( W( d0, h ), up, t, Vector4.Zero, c ) );
				part.Vertices.Add( V( W( d1, h ), up, t, Vector4.Zero, c ) );
				part.Indices.Add( tb ); part.Indices.Add( tb + 1 ); part.Indices.Add( tb + 2 );

				int bb = part.Vertices.Count;
				part.Vertices.Add( V( center - up * h, -up, t, Vector4.Zero, c ) );
				part.Vertices.Add( V( W( d1, -h ), -up, t, Vector4.Zero, c ) );
				part.Vertices.Add( V( W( d0, -h ), -up, t, Vector4.Zero, c ) );
				part.Indices.Add( bb ); part.Indices.Add( bb + 1 ); part.Indices.Add( bb + 2 );
			}
		}
	}

	/// <summary>A torus (steering wheel rim): its hole faces along the rotation's local Z.</summary>
	public void Ring( string material, Vector3 center, float majorRadius, float minorRadius, int majorSegments, int minorSegments, Rotation? rotation = null, Color32? color = null )
	{
		var rot = rotation ?? Rotation.Identity;
		var c = color ?? White;
		var part = PartFor( material );

		Vector3 Pos( int i, int j, out Vector3 normal )
		{
			float u = MathF.PI * 2f * i / majorSegments;
			float v = MathF.PI * 2f * j / minorSegments;
			var radial = new Vector3( MathF.Cos( u ), MathF.Sin( u ), 0 );
			var local = radial * (majorRadius + minorRadius * MathF.Cos( v )) + Vector3.Up * (minorRadius * MathF.Sin( v ));
			normal = rot * (radial * MathF.Cos( v ) + Vector3.Up * MathF.Sin( v ));
			return center + rot * local;
		}

		for ( int i = 0; i < majorSegments; i++ )
		{
			for ( int j = 0; j < minorSegments; j++ )
			{
				var p00 = Pos( i, j, out var n00 );
				var p10 = Pos( i + 1, j, out var n10 );
				var p11 = Pos( i + 1, j + 1, out var n11 );
				var p01 = Pos( i, j + 1, out var n01 );
				var t = (p10 - p00).Normal;

				int b = part.Vertices.Count;
				part.Vertices.Add( V( p00, n00, t, Vector4.Zero, c ) );
				part.Vertices.Add( V( p10, n10, t, Vector4.Zero, c ) );
				part.Vertices.Add( V( p11, n11, t, Vector4.Zero, c ) );
				part.Vertices.Add( V( p01, n01, t, Vector4.Zero, c ) );
				// wound so the outside (normal side) is the front
				part.Indices.Add( b ); part.Indices.Add( b + 3 ); part.Indices.Add( b + 2 );
				part.Indices.Add( b ); part.Indices.Add( b + 2 ); part.Indices.Add( b + 1 );
			}
		}
	}

	public bool IsEmpty => _parts.Count == 0;

	/// <summary>Build the model. Material keys are resource paths. Main thread only.</summary>
	public Model Build( string name )
	{
		var builder = Model.Builder.WithName( name );
		bool any = false;

		foreach ( var (materialPath, part) in _parts )
		{
			if ( part.Indices.Count == 0 ) continue;

			var material = Material.Load( materialPath );
			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( part.Vertices.Count, part.Vertices );
			mesh.CreateIndexBuffer( part.Indices.Count, part.Indices );

			var min = part.Vertices[0].Position;
			var max = min;
			foreach ( var v in part.Vertices )
			{
				min = Vector3.Min( min, v.Position );
				max = Vector3.Max( max, v.Position );
			}
			mesh.Bounds = new BBox( min, max );

			builder = builder.AddMesh( mesh );
			any = true;
		}

		foreach ( var (extents, center, rotation) in _collision )
			builder = builder.AddCollisionBox( extents, center, rotation );

		return any ? builder.Create() : null;
	}
}
