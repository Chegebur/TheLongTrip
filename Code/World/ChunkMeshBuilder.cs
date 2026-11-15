using System;
using System.Collections.Generic;

namespace LongTrip.World;

/// <summary>
/// Raw geometry for one chunk, built off the main thread. Turning it into a <see cref="Model"/>
/// has to happen on the main thread (see <see cref="ChunkMeshBuilder.CreateModel"/>).
/// </summary>
public sealed class ChunkGeometry
{
	public List<Vertex> Vertices = new();
	public List<int> Indices = new();

	/// <summary>Collision is separate from the render mesh: lighter, and only built for chunks near the player.</summary>
	public List<Vector3> CollisionPositions = new();
	public List<int> CollisionIndices = new();

	/// <summary>An optional second mesh with its own material (road chunks put the guardrails here).</summary>
	public List<Vertex> Vertices2 = new();
	public List<int> Indices2 = new();

	public BBox Bounds;

	public bool IsEmpty => Indices.Count == 0;
	public bool HasCollision => CollisionIndices.Count > 0;
}

public static class ChunkMeshBuilder
{
	// s&box treats counter-clockwise (seen from above) as the up-facing side for both rendering and collision.
	// Verified with scene_trace: with false the collision normals pointed down and rays from above passed through.
	private static readonly bool FlipWinding = true;

	/// <summary>
	/// Terrain grid for the square chunk starting at <paramref name="origin"/> (world XY).
	/// </summary>
	/// <param name="resolution">Quads per side (LOD). Must be even when <paramref name="withCollision"/> is set.</param>
	/// <param name="withCollision">Also build a collision mesh at half the render resolution.</param>
	/// <param name="skirtDepth">
	/// A curtain hanging below every edge. Neighbouring chunks can be at different LODs, so their edges don't
	/// match exactly; the curtain stops the gap showing the sky.
	/// </param>
	public static ChunkGeometry BuildTerrain( WorldGenerator gen, Vector2 origin, float size, int resolution, bool withCollision, float skirtDepth )
	{
		var geo = new ChunkGeometry();
		int verts = resolution + 1;
		float step = size / resolution;

		// Heights with a one-sample border so normals at the edges match the neighbouring chunk.
		int hs = verts + 2;
		var heights = new float[hs * hs];
		for ( int iy = 0; iy < hs; iy++ )
			for ( int ix = 0; ix < hs; ix++ )
				heights[iy * hs + ix] = gen.GetHeight( origin.x + (ix - 1) * step, origin.y + (iy - 1) * step );

		float minZ = float.MaxValue, maxZ = float.MinValue;

		for ( int iy = 0; iy < verts; iy++ )
		{
			for ( int ix = 0; ix < verts; ix++ )
			{
				int h = (iy + 1) * hs + (ix + 1);
				float z = heights[h];
				float dzdx = (heights[h + 1] - heights[h - 1]) / (2f * step);
				float dzdy = (heights[h + hs] - heights[h - hs]) / (2f * step);
				var normal = new Vector3( -dzdx, -dzdy, 1f ).Normal;

				float wx = origin.x + ix * step;
				float wy = origin.y + iy * step;
				var local = new Vector3( ix * step, iy * step, z );

				var tangent = new Vector3( 1f, 0f, dzdx ).Normal;

				// Material weights for terrain_pbr.shader:
				//   Color     = biome weights
				//   TexCoord0 = (base, alt)    TexCoord1 = (slope, shoulder)
				var splat = gen.GetSplat( wx, wy, normal.z );
				var v = new Vertex( local, normal, tangent, new Vector4( splat.Base, splat.Alt, 0, 0 ) );
				v.TexCoord1 = new Vector4( splat.Slope, splat.Shoulder, 0, 0 );
				v.Color = splat.BiomeBytes;

				geo.Vertices.Add( v );

				minZ = MathF.Min( minZ, z );
				maxZ = MathF.Max( maxZ, z );
			}
		}

		for ( int iy = 0; iy < resolution; iy++ )
		{
			for ( int ix = 0; ix < resolution; ix++ )
			{
				int a = iy * verts + ix;
				int b = a + 1;
				int c = a + verts + 1;
				int d = a + verts;
				AddQuad( geo.Indices, a, b, c, d );
			}
		}

		if ( skirtDepth > 0f )
		{
			AddSkirt( geo, verts, skirtDepth );
			minZ -= skirtDepth;
		}

		if ( withCollision && resolution >= 2 && resolution % 2 == 0 )
			AddTerrainCollision( geo, verts );

		geo.Bounds = new BBox( new Vector3( 0, 0, minZ ), new Vector3( size, size, maxZ ) );
		return geo;
	}

	/// <summary>Collision at half the render resolution: roughly a quarter of the triangles, and smoother to drive over.</summary>
	private static void AddTerrainCollision( ChunkGeometry geo, int verts )
	{
		int cverts = (verts - 1) / 2 + 1;
		for ( int iy = 0; iy < verts; iy += 2 )
			for ( int ix = 0; ix < verts; ix += 2 )
				geo.CollisionPositions.Add( geo.Vertices[iy * verts + ix].Position );

		for ( int iy = 0; iy < cverts - 1; iy++ )
		{
			for ( int ix = 0; ix < cverts - 1; ix++ )
			{
				int a = iy * cverts + ix;
				AddQuad( geo.CollisionIndices, a, a + 1, a + cverts + 1, a + cverts );
			}
		}
	}

	/// <summary>Hang a double-sided curtain below all four edges of a terrain grid.</summary>
	private static void AddSkirt( ChunkGeometry geo, int verts, float depth )
	{
		// Edge vertex indices, walking around the chunk.
		var edges = new List<int>[4];
		for ( int e = 0; e < 4; e++ ) edges[e] = new List<int>( verts );
		for ( int i = 0; i < verts; i++ )
		{
			edges[0].Add( i );                          // y = 0
			edges[1].Add( i * verts + (verts - 1) );    // x = max
			edges[2].Add( (verts - 1) * verts + i );    // y = max
			edges[3].Add( i * verts );                  // x = 0
		}

		foreach ( var edge in edges )
		{
			int first = geo.Vertices.Count;
			foreach ( int idx in edge )
			{
				var v = geo.Vertices[idx];
				v.Position = new Vector3( v.Position.x, v.Position.y, v.Position.z - depth );
				geo.Vertices.Add( v );
			}

			for ( int i = 0; i < edge.Count - 1; i++ )
			{
				int top0 = edge[i], top1 = edge[i + 1];
				int low0 = first + i, low1 = first + i + 1;

				// Both windings: the curtain is seen from outside, from either side of the seam.
				geo.Indices.Add( top0 ); geo.Indices.Add( low0 ); geo.Indices.Add( low1 );
				geo.Indices.Add( top0 ); geo.Indices.Add( low1 ); geo.Indices.Add( top1 );
				geo.Indices.Add( top0 ); geo.Indices.Add( low1 ); geo.Indices.Add( low0 );
				geo.Indices.Add( top0 ); geo.Indices.Add( top1 ); geo.Indices.Add( low1 );
			}
		}
	}

	/// <summary>
	/// Road ribbon covering world X from <paramref name="startX"/> to <paramref name="startX"/> + <paramref name="length"/>.
	/// Vertices are relative to (startX, 0, 0).
	/// </summary>
	public static ChunkGeometry BuildRoad( WorldGenerator gen, float startX, float length, bool withCollision, bool withRails, float step = 64f )
	{
		var geo = new ChunkGeometry();
		var s = gen.Settings;
		float hw = s.RoadWidth * 0.5f;

		const float lineWidth = 10f;      // painted line width
		const float centerHalf = 5f;      // half width of the centre line
		const float dashLength = 120f;    // ~3 m dash
		const float dashPeriod = 480f;    // ~12 m dash + gap
		const float lift = 0.5f;          // above the levelled ground (which sits GroundBelowRoad under the surface)

		// Vertex colour for road_pbr.shader: rgb = paint colour (sRGB bytes), a = paint amount.
		var asphalt = new Color32( 0, 0, 0, 0 );
		var white = new Color32( 222, 222, 214, 255 );
		var yellow = new Color32( 229, 180, 36, 255 );

		// Strips across the road, from right edge (-) to left edge (+) along the road's left vector.
		var strips = new (float from, float to, int kind)[]
		{
			(-hw, -hw + lineWidth, 1),                  // right edge line
			(-hw + lineWidth, -centerHalf, 0),          // right lane
			(-centerHalf, centerHalf, 2),               // centre dashes
			(centerHalf, hw - lineWidth, 0),            // left lane
			(hw - lineWidth, hw, 1),                    // left edge line
		};

		int steps = Math.Max( 1, (int)MathF.Ceiling( length / step ) );
		float minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;

		// Precompute centre, height and left vector for each cross-section.
		var centers = new Vector3[steps + 1];
		var lefts = new Vector3[steps + 1];
		for ( int i = 0; i <= steps; i++ )
		{
			float x = startX + MathF.Min( i * step, length );
			var dir = gen.RoadDirection( x );
			lefts[i] = new Vector3( -dir.y, dir.x, 0f );
			centers[i] = new Vector3( x - startX, gen.RoadCenterY( x ), gen.RoadHeight( x ) + lift );
		}

		for ( int i = 0; i < steps; i++ )
		{
			float midX = startX + (i + 0.5f) * step;
			bool dashOn = Mod( midX, dashPeriod ) < dashLength;

			foreach ( var strip in strips )
			{
				Color32 col = strip.kind switch
				{
					1 => white,
					2 => dashOn ? yellow : asphalt,
					_ => asphalt,
				};

				var p0 = centers[i] + lefts[i] * strip.from;
				var p1 = centers[i + 1] + lefts[i + 1] * strip.from;
				var p2 = centers[i + 1] + lefts[i + 1] * strip.to;
				var p3 = centers[i] + lefts[i] * strip.to;

				var forward = (p1 - p0).Normal;
				var normal = Vector3.Cross( forward, (p3 - p0).Normal ).Normal;
				if ( normal.z < 0 ) normal = -normal;

				int baseIndex = geo.Vertices.Count;
				foreach ( var p in new[] { p0, p1, p2, p3 } )
				{
					var v = new Vertex( p, normal, forward, Vector4.Zero );
					v.Color = col;
					geo.Vertices.Add( v );

					minY = MathF.Min( minY, p.y ); maxY = MathF.Max( maxY, p.y );
					minZ = MathF.Min( minZ, p.z ); maxZ = MathF.Max( maxZ, p.z );
				}

				// p0 -> p1 runs along +X, p3 is to the left (+Y): same layout as a terrain quad.
				AddQuad( geo.Indices, baseIndex, baseIndex + 1, baseIndex + 2, baseIndex + 3 );
			}
		}

		// Collision is one plain ribbon (right edge to left edge), not the painted strips: a tenth of the triangles.
		if ( withCollision )
		{
			for ( int i = 0; i <= steps; i++ )
			{
				geo.CollisionPositions.Add( centers[i] - lefts[i] * hw );
				geo.CollisionPositions.Add( centers[i] + lefts[i] * hw );
			}

			for ( int i = 0; i < steps; i++ )
			{
				int a = i * 2;
				// right(i), right(i+1), left(i+1), left(i): same layout as a terrain quad.
				AddQuad( geo.CollisionIndices, a, a + 2, a + 3, a + 1 );
			}
		}

		if ( withRails )
		{
			AddGuardrails( gen, geo, startX, length, hw, withCollision );
			if ( geo.Vertices2.Count > 0 )
			{
				minY = MathF.Min( minY, -hw - 80f ); maxY = MathF.Max( maxY, hw + 80f );
				maxZ += 80f;
			}
		}

		geo.Bounds = new BBox( new Vector3( 0, minY, minZ ), new Vector3( length, maxY, maxZ ) );
		return geo;
	}

	// ---- Guardrails ------------------------------------------------------------------------

	private const float RailSpacing = 75f;     // post spacing (about 6 ft)
	private const float RailSegment = 6400f;   // rails come and go in stretches this long
	private const float RailOffset = 45f;      // from the asphalt edge
	private const float RailClearStart = 2600f; // keep the start of the road open

	/// <summary>Whether a stretch of road has a guardrail on a side (side is -1 or +1).</summary>
	private static bool RailsAt( int seed, int segment, int side )
	{
		if ( WorldNoise.Hash01( seed + 777, segment, 1 ) > 0.32f ) return false;
		float s = WorldNoise.Hash01( seed + 778, segment, 2 );
		return s < 0.55f || (s < 0.78f ? side > 0 : side < 0);
	}

	private static void AddGuardrails( WorldGenerator gen, ChunkGeometry geo, float startX, float length, float roadHalfWidth, bool withCollision )
	{
		int seed = gen.Settings.Seed;
		int steps = Math.Max( 1, (int)MathF.Ceiling( length / RailSpacing ) );

		for ( int side = -1; side <= 1; side += 2 )
		{
			// post positions along this side, ground-following
			var posts = new Vector3[steps + 1];
			var fwd = new Vector3[steps + 1];
			var lat = new Vector3[steps + 1];
			for ( int i = 0; i <= steps; i++ )
			{
				float x = startX + MathF.Min( i * RailSpacing, length );
				var dir = gen.RoadDirection( x );
				var left = new Vector3( -dir.y, dir.x, 0f );
				fwd[i] = dir;
				lat[i] = left * side;
				posts[i] = new Vector3( x - startX, gen.RoadCenterY( x ), gen.RoadHeight( x ) ) + lat[i] * (roadHalfWidth + RailOffset);
			}

			for ( int i = 0; i < steps; i++ )
			{
				float x = startX + i * RailSpacing;
				if ( x < RailClearStart ) continue;
				if ( !RailsAt( seed, (int)MathF.Floor( x / RailSegment ), side ) ) continue;

				AddPost( geo, posts[i], fwd[i], lat[i] );
				AddBeam( geo, posts[i], posts[i + 1], lat[i], lat[i + 1] );
				if ( withCollision ) AddRailCollision( geo, posts[i], posts[i + 1] );
			}
		}
	}

	private const float RailBottom = 22f, RailTop = 36f, RailHalfThick = 1.8f;

	private static void AddPost( ChunkGeometry geo, Vector3 basePoint, Vector3 forward, Vector3 lateral )
	{
		const float halfAlong = 2.2f, halfAcross = 3.2f, below = 6f, top = 33f;
		var f = forward * halfAlong;
		var l = lateral * halfAcross;
		var lo = Vector3.Up * -below;
		var hi = Vector3.Up * top;

		Vector3 C( float a, float b, bool up ) => basePoint + f * a + l * b + (up ? hi : lo);

		// +forward, -forward, outer, inner, top
		RailQuad( geo, C( 1, -1, false ), C( 1, 1, false ), C( 1, 1, true ), C( 1, -1, true ), forward );
		RailQuad( geo, C( -1, 1, false ), C( -1, -1, false ), C( -1, -1, true ), C( -1, 1, true ), -forward );
		RailQuad( geo, C( 1, 1, false ), C( -1, 1, false ), C( -1, 1, true ), C( 1, 1, true ), lateral );
		RailQuad( geo, C( -1, -1, false ), C( 1, -1, false ), C( 1, -1, true ), C( -1, -1, true ), -lateral );
		RailQuad( geo, C( -1, -1, true ), C( 1, -1, true ), C( 1, 1, true ), C( -1, 1, true ), Vector3.Up );
	}

	private static void AddBeam( ChunkGeometry geo, Vector3 p0, Vector3 p1, Vector3 lat0, Vector3 lat1 )
	{
		// a thin box between two posts, on the road side of the posts
		Vector3 P( Vector3 p, Vector3 lat, float t, float z ) => p + lat * (-3.4f + t * RailHalfThick * 2f) + Vector3.Up * z;

		var a0 = P( p0, lat0, 0, RailBottom ); var a1 = P( p1, lat1, 0, RailBottom );        // inner, low
		var b0 = P( p0, lat0, 0, RailTop ); var b1 = P( p1, lat1, 0, RailTop );              // inner, high
		var c0 = P( p0, lat0, 1, RailTop ); var c1 = P( p1, lat1, 1, RailTop );              // outer, high
		var d0 = P( p0, lat0, 1, RailBottom ); var d1 = P( p1, lat1, 1, RailBottom );        // outer, low

		RailQuad( geo, a0, a1, b1, b0, -lat0 );                 // inner (faces the road)
		RailQuad( geo, d1, d0, c0, c1, lat0 );                  // outer
		RailQuad( geo, b0, b1, c1, c0, Vector3.Up );            // top
		RailQuad( geo, d0, d1, a1, a0, Vector3.Down );          // bottom
	}

	/// <summary>A thin, double-sided sheet along the beam: what the car actually hits.</summary>
	private static void AddRailCollision( ChunkGeometry geo, Vector3 p0, Vector3 p1 )
	{
		int b = geo.CollisionPositions.Count;
		geo.CollisionPositions.Add( p0 + Vector3.Up * 8f );
		geo.CollisionPositions.Add( p1 + Vector3.Up * 8f );
		geo.CollisionPositions.Add( p1 + Vector3.Up * 40f );
		geo.CollisionPositions.Add( p0 + Vector3.Up * 40f );
		geo.CollisionIndices.Add( b ); geo.CollisionIndices.Add( b + 1 ); geo.CollisionIndices.Add( b + 2 );
		geo.CollisionIndices.Add( b ); geo.CollisionIndices.Add( b + 2 ); geo.CollisionIndices.Add( b + 3 );
		geo.CollisionIndices.Add( b ); geo.CollisionIndices.Add( b + 2 ); geo.CollisionIndices.Add( b + 1 );
		geo.CollisionIndices.Add( b ); geo.CollisionIndices.Add( b + 3 ); geo.CollisionIndices.Add( b + 2 );
	}

	/// <summary>Add a quad to the guardrail mesh, wound so it faces <paramref name="normal"/>.</summary>
	private static void RailQuad( ChunkGeometry geo, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal )
	{
		normal = normal.Normal;
		var geometric = Vector3.Cross( b - a, d - a );
		bool flip = Vector3.Dot( geometric, normal ) < 0f;

		int baseIndex = geo.Vertices2.Count;
		var tangent = (b - a).Normal;
		foreach ( var p in new[] { a, b, c, d } )
		{
			var v = new Vertex( p, normal, tangent, Vector4.Zero );
			v.Color = new Color32( 255, 255, 255, 255 );
			geo.Vertices2.Add( v );
		}

		// counter-clockwise from the front: a,b,c / a,c,d; reversed when the quad was given the other way round
		if ( !flip )
		{
			geo.Indices2.Add( baseIndex ); geo.Indices2.Add( baseIndex + 1 ); geo.Indices2.Add( baseIndex + 2 );
			geo.Indices2.Add( baseIndex ); geo.Indices2.Add( baseIndex + 2 ); geo.Indices2.Add( baseIndex + 3 );
		}
		else
		{
			geo.Indices2.Add( baseIndex ); geo.Indices2.Add( baseIndex + 2 ); geo.Indices2.Add( baseIndex + 1 );
			geo.Indices2.Add( baseIndex ); geo.Indices2.Add( baseIndex + 3 ); geo.Indices2.Add( baseIndex + 2 );
		}
	}

	/// <summary>Turn built geometry into a renderable model, with a collision mesh if one was built. Main thread only.</summary>
	public static Model CreateModel( ChunkGeometry geo, Material material, string name, Material secondaryMaterial = null )
	{
		if ( geo.IsEmpty ) return null;

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( geo.Vertices.Count, geo.Vertices );
		mesh.CreateIndexBuffer( geo.Indices.Count, geo.Indices );
		mesh.Bounds = geo.Bounds;

		var builder = Model.Builder.WithName( name ).AddMesh( mesh );

		if ( geo.Indices2.Count > 0 && secondaryMaterial is not null )
		{
			var mesh2 = new Mesh( secondaryMaterial );
			mesh2.CreateVertexBuffer( geo.Vertices2.Count, geo.Vertices2 );
			mesh2.CreateIndexBuffer( geo.Indices2.Count, geo.Indices2 );
			mesh2.Bounds = geo.Bounds;
			builder = builder.AddMesh( mesh2 );
		}

		if ( geo.HasCollision )
			builder = builder.AddCollisionMesh( geo.CollisionPositions, geo.CollisionIndices );

		return builder.Create();
	}

	/// <summary>Quad a-b-c-d where a→b is +X and a→d is +Y.</summary>
	private static void AddQuad( List<int> indices, int a, int b, int c, int d )
	{
		if ( FlipWinding )
		{
			indices.Add( a ); indices.Add( b ); indices.Add( c );
			indices.Add( a ); indices.Add( c ); indices.Add( d );
		}
		else
		{
			indices.Add( a ); indices.Add( c ); indices.Add( b );
			indices.Add( a ); indices.Add( d ); indices.Add( c );
		}
	}

	private static float Mod( float a, float m ) => a - m * MathF.Floor( a / m );
}
