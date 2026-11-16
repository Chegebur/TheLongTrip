using System;
using System.Collections.Generic;

namespace LongTrip.World.Props;

/// <summary>
/// Makes proper collision for any model, from its actual geometry, instead of one box around its bounds.
/// <list type="number">
/// <item>If the model came with its own collision (authored physics), that is used as it is.</item>
/// <item>Otherwise the triangles are rasterised into a voxel grid, the air outside is flood-filled away (so a sealed hollow is solid but a door or
/// an arch stays open), and the solid voxels are merged into a few regions. Each region becomes one <b>convex hull</b> fitted to the model's own
/// vertices inside it (so a rock is round, not boxy), reduced to a few dozen points, and the hulls become a collision-only model for a
/// <see cref="ModelCollider"/>. A handful of tight convex pieces is far cheaper for the physics engine than a triangle mesh or dozens of boxes. It works for static things (rocks, fences, walls) and for loose items on a rigid body alike.</item>
/// <item>If a single box would fit the shape about as well (a crate, a floor tile), nothing is generated and the caller keeps its box.</item>
/// </list>
/// Results are cached per model, so a thousand rocks share one generated collision.
/// </summary>
public static class ColliderGenerator
{
	/// <summary>The most convex pieces one model may get; the voxels are coarsened until it fits.</summary>
	public const int MaxBoxes = 10;

	/// <summary>How full its bounding box must be before one box is considered good enough.</summary>
	public const float SingleBoxFill = 0.74f;

	private static readonly Dictionary<Model, Model> _cache = new();
	private static readonly Dictionary<Model, Model> _generatedOnly = new();
	private static readonly Dictionary<Model, int> _boxes = new();

	public static void Reset() { _cache.Clear(); _generatedOnly.Clear(); _boxes.Clear(); }

	/// <summary>How many boxes the generated collision for this model has (0 if none was needed).</summary>
	public static int BoxCount( Model model ) => model is not null && _boxes.TryGetValue( model, out var n ) ? n : 0;

	/// <summary>
	/// The model to give a <see cref="ModelCollider"/>: its own authored collision, or the generated one. Null when a plain box does the job
	/// (or when the model's geometry cannot be read).
	/// </summary>
	public static Model For( Model model, bool allowAuthored = true )
	{
		if ( model is null || !model.IsValid() || model.IsError ) return null;
		var cache = allowAuthored ? _cache : _generatedOnly;
		if ( cache.TryGetValue( model, out var cached ) ) return cached;

		Model result = null;
		try
		{
			if ( allowAuthored && model.Physics is { } physics && physics.Parts.Count > 0 ) result = model;   // it ships with its own collision
			else result = Generate( model );
		}
		catch ( Exception e ) { Log.Warning( $"[collider] could not generate collision for {model.Name} ({e.Message})" ); }

		cache[model] = result;
		return result;
	}

	/// <summary>Put the right collision on an object: generated or authored if the shape needs it, else a box from the bounds (scaled by <paramref name="boxScale"/>).</summary>
	public static Collider Add( GameObject go, Model model, Vector3? boxScale = null, bool isTrigger = false )
	{
		var shaped = For( model );
		if ( shaped is not null )
		{
			var c = go.Components.Create<ModelCollider>();
			c.Model = shaped;
			c.IsTrigger = isTrigger;
			return c;
		}

		var box = go.Components.Create<BoxCollider>();
		box.Center = model.Bounds.Center;
		box.Scale = model.Bounds.Size * (boxScale ?? Vector3.One);
		box.IsTrigger = isTrigger;
		return box;
	}

	// ---- generation -----------------------------------------------------------------------------------------------------------

	private static Model Generate( Model model )
	{
		var verts = model.GetVertices();
		var indices = model.GetIndices();
		if ( verts is null || indices is null || verts.Length == 0 || indices.Length < 3 ) return null;

		var bounds = model.Bounds;
		var size = bounds.Size;
		float longest = MathF.Max( size.x, MathF.Max( size.y, size.z ) );
		if ( longest < 0.01f ) return null;

		// does one box fit well enough? Judged on a fine grid, so the answer does not depend on how coarsely the pieces are cut below
		Voxelise( verts, indices, bounds, longest / 28f, out float fineFill, out _ );
		if ( fineFill >= SingleBoxFill ) { _boxes[model] = 0; return null; }

		// start at about 8 cells along the longest side and refine while the piece count stays small: fewer, bigger pieces are cheaper
		for ( float cells = 14f; cells >= 3f; cells *= 0.75f )
		{
			var boxes = Voxelise( verts, indices, bounds, longest / cells, out float fill, out var ownerOf );
			if ( boxes.Count == 0 ) return null;
			if ( boxes.Count > MaxBoxes && cells > 3.5f ) continue;

			// the model's own vertices, grouped by the region they fall in
			var groups = new List<Vector3>[boxes.Count];
			for ( int i = 0; i < groups.Length; i++ ) groups[i] = new List<Vector3>();
			foreach ( var v in verts )
			{
				int g = ownerOf( v.Position );
				if ( g >= 0 ) groups[g].Add( v.Position );
			}

			var builder = Model.Builder;
			int pieces = 0;
			for ( int i = 0; i < boxes.Count; i++ )
			{
				var points = Reduce( groups[i] );
				if ( points.Count >= 5 && !Flat( points ) ) builder = builder.AddCollisionHull( points );
				else builder = builder.AddCollisionBox( boxes[i].Half, boxes[i].Centre, Rotation.Identity );
				pieces++;
			}
			var made = builder.Create();
			if ( made is not null ) _boxes[model] = pieces;
			return made;
		}
		return null;
	}

	/// <summary>True when all the points lie (nearly) in a plane or a line: no volume for a hull to have.</summary>
	private static bool Flat( List<Vector3> points )
	{
		var lo = points[0]; var hi = points[0];
		foreach ( var p in points ) { lo = Vector3.Min( lo, p ); hi = Vector3.Max( hi, p ); }
		var d = hi - lo;
		float biggest = MathF.Max( d.x, MathF.Max( d.y, d.z ) );
		return d.x < biggest * 0.02f || d.y < biggest * 0.02f || d.z < biggest * 0.02f;
	}

	private static readonly Vector3[] Directions = BuildDirections();

	private static Vector3[] BuildDirections()
	{
		var list = new List<Vector3>();
		for ( int x = -1; x <= 1; x++ )
		for ( int y = -1; y <= 1; y++ )
		for ( int z = -1; z <= 1; z++ )
			if ( x != 0 || y != 0 || z != 0 ) list.Add( new Vector3( x, y, z ).Normal );
		return list.ToArray();
	}

	/// <summary>The points that matter for a convex hull: the extreme ones in each of 26 directions (at most 26, no duplicates).</summary>
	private static List<Vector3> Reduce( List<Vector3> points )
	{
		var kept = new List<Vector3>();
		if ( points.Count == 0 ) return kept;
		foreach ( var dir in Directions )
		{
			var best = points[0];
			float bestDot = float.MinValue;
			foreach ( var p in points )
			{
				float d = Vector3.Dot( p, dir );
				if ( d > bestDot ) { bestDot = d; best = p; }
			}
			if ( !kept.Contains( best ) ) kept.Add( best );
		}
		return kept;
	}

	/// <summary>Rasterise, fill, merge. Returns box centres and half sizes (model space), and how much of the bounds is solid.</summary>
	private static List<(Vector3 Centre, Vector3 Half)> Voxelise( Vertex[] verts, uint[] indices, BBox bounds, float cell, out float fill, out Func<Vector3, int> ownerOf )
	{
		var min = bounds.Mins;
		int nx = Math.Max( 1, (int)MathF.Ceiling( bounds.Size.x / cell ) );
		int ny = Math.Max( 1, (int)MathF.Ceiling( bounds.Size.y / cell ) );
		int nz = Math.Max( 1, (int)MathF.Ceiling( bounds.Size.z / cell ) );
		// the cells are stretched a little so the grid exactly covers the bounds
		var step = new Vector3( bounds.Size.x / nx, bounds.Size.y / ny, bounds.Size.z / nz );

		// a padded grid, so the flood fill can walk all the way round the model
		int gx = nx + 2, gy = ny + 2, gz = nz + 2;
		var solid = new bool[gx * gy * gz];
		int I( int x, int y, int z ) => (z * gy + y) * gx + x;

		void Mark( Vector3 p )
		{
			int x = Math.Clamp( (int)MathF.Floor( (p.x - min.x) / step.x ), 0, nx - 1 );
			int y = Math.Clamp( (int)MathF.Floor( (p.y - min.y) / step.y ), 0, ny - 1 );
			int z = Math.Clamp( (int)MathF.Floor( (p.z - min.z) / step.z ), 0, nz - 1 );
			solid[I( x + 1, y + 1, z + 1 )] = true;
		}

		float minStep = MathF.Min( step.x, MathF.Min( step.y, step.z ) );
		for ( int t = 0; t + 2 < indices.Length; t += 3 )
		{
			var a = verts[indices[t]].Position;
			var b = verts[indices[t + 1]].Position;
			var c = verts[indices[t + 2]].Position;

			float longestEdge = MathF.Max( (b - a).Length, MathF.Max( (c - b).Length, (a - c).Length ) );
			int n = Math.Clamp( (int)MathF.Ceiling( longestEdge / (minStep * 0.5f) ), 1, 96 );
			for ( int i = 0; i <= n; i++ )
			for ( int j = 0; j <= n - i; j++ )
			{
				float u = i / (float)n, v = j / (float)n;
				Mark( a + (b - a) * u + (c - a) * v );
			}
		}

		// the air outside: flood from a corner of the padding through everything that is not solid
		var outside = new bool[solid.Length];
		var stack = new Stack<int>();
		stack.Push( 0 );
		outside[0] = true;
		while ( stack.Count > 0 )
		{
			int cur = stack.Pop();
			int cx = cur % gx, cy = cur / gx % gy, cz = cur / (gx * gy);
			void Visit( int x, int y, int z )
			{
				if ( x < 0 || y < 0 || z < 0 || x >= gx || y >= gy || z >= gz ) return;
				int k = I( x, y, z );
				if ( outside[k] || solid[k] ) return;
				outside[k] = true;
				stack.Push( k );
			}
			Visit( cx + 1, cy, cz ); Visit( cx - 1, cy, cz );
			Visit( cx, cy + 1, cz ); Visit( cx, cy - 1, cz );
			Visit( cx, cy, cz + 1 ); Visit( cx, cy, cz - 1 );
		}

		// everything that is not outside air is solid (sealed hollows fill in)
		int count = 0;
		for ( int z = 1; z <= nz; z++ )
		for ( int y = 1; y <= ny; y++ )
		for ( int x = 1; x <= nx; x++ )
		{
			int k = I( x, y, z );
			solid[k] = !outside[k];
			if ( solid[k] ) count++;
		}
		fill = count / (float)(nx * ny * nz);

		// merge into boxes: a run along x, widened along y, then thickened along z
		var used = new bool[solid.Length];
		var owner = new int[solid.Length];
		Array.Fill( owner, -1 );
		var boxes = new List<(Vector3, Vector3)>();
		for ( int z = 1; z <= nz; z++ )
		for ( int y = 1; y <= ny; y++ )
		for ( int x = 1; x <= nx; x++ )
		{
			if ( !solid[I( x, y, z )] || used[I( x, y, z )] ) continue;

			int x1 = x;
			while ( x1 + 1 <= nx && solid[I( x1 + 1, y, z )] && !used[I( x1 + 1, y, z )] ) x1++;

			int y1 = y;
			bool rowOk = true;
			while ( rowOk && y1 + 1 <= ny )
			{
				for ( int xi = x; xi <= x1; xi++ ) if ( !solid[I( xi, y1 + 1, z )] || used[I( xi, y1 + 1, z )] ) { rowOk = false; break; }
				if ( rowOk ) y1++;
			}

			int z1 = z;
			bool layerOk = true;
			while ( layerOk && z1 + 1 <= nz )
			{
				for ( int yi = y; yi <= y1 && layerOk; yi++ )
				for ( int xi = x; xi <= x1; xi++ )
					if ( !solid[I( xi, yi, z1 + 1 )] || used[I( xi, yi, z1 + 1 )] ) { layerOk = false; break; }
				if ( layerOk ) z1++;
			}

			for ( int zi = z; zi <= z1; zi++ )
			for ( int yi = y; yi <= y1; yi++ )
			for ( int xi = x; xi <= x1; xi++ )
			{
				used[I( xi, yi, zi )] = true;
				owner[I( xi, yi, zi )] = boxes.Count;
			}

			var lo = min + new Vector3( (x - 1) * step.x, (y - 1) * step.y, (z - 1) * step.z );
			var hi = min + new Vector3( x1 * step.x, y1 * step.y, z1 * step.z );
			boxes.Add( ((lo + hi) * 0.5f, (hi - lo) * 0.5f) );
		}
		// which region a point of the model falls in (-1: none)
		ownerOf = p =>
		{
			int x = Math.Clamp( (int)MathF.Floor( (p.x - min.x) / step.x ), 0, nx - 1 );
			int y = Math.Clamp( (int)MathF.Floor( (p.y - min.y) / step.y ), 0, ny - 1 );
			int z = Math.Clamp( (int)MathF.Floor( (p.z - min.z) / step.z ), 0, nz - 1 );
			return owner[I( x + 1, y + 1, z + 1 )];
		};
		return boxes;
	}
}
