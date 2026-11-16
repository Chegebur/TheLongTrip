using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LongTrip.World.Props;

namespace LongTrip.World;

/// <summary>
/// Streams the procedural world (terrain + road) in and out around a target.
/// <list type="bullet">
/// <item>Geometry is computed on worker threads; only the final model creation happens on the main thread.</item>
/// <item>Chunks get coarser the further they are (LOD), and are rebuilt as you move closer.</item>
/// <item>Only chunks near the target get a collision mesh, and only near ones cast shadows.</item>
/// </list>
/// Put this on one GameObject at the world origin.
/// </summary>
public sealed class WorldStreamer : Component
{
	[Property, Group( "World" )] public int Seed { get; set; } = 1337;

	/// <summary>Who the world loads around. Falls back to the scene's player, then the main camera.</summary>
	[Property, Group( "World" )] public GameObject Target { get; set; }

	/// <summary>In play mode, keep the target frozen at the road start until the ground under it exists, so it can't fall through the world.</summary>
	[Property, Group( "World" )] public bool HoldTargetUntilGroundReady { get; set; } = true;

	[Property, Group( "Chunks" )] public float ChunkSize { get; set; } = 8192f;

	/// <summary>Quads per chunk side at full detail. Each LOD step halves it.</summary>
	[Property, Group( "Chunks" ), Range( 32, 128 )] public int Resolution { get; set; } = 64;

	/// <summary>How many chunks to keep loaded in every direction. Far ones are cheap thanks to LOD.</summary>
	[Property, Group( "Chunks" ), Range( 2, 16 )] public int ViewRadius { get; set; } = 10;

	[Property, Group( "Chunks" )] public int MaxModelsPerFrame { get; set; } = 2;

	/// <summary>How many chunks may be generating on worker threads at once.</summary>
	[Property, Group( "Chunks" ), Range( 1, 32 )] public int MaxConcurrentBuilds { get; set; } = 6;

	// ---- Level of detail: distances are in chunks (Chebyshev) from the target's chunk -------------

	/// <summary>Chunks up to this far are full detail (LOD 0).</summary>
	[Property, Group( "Detail" ), Range( 0, 8 )] public int Lod0Radius { get; set; } = 1;

	/// <summary>Up to here: half the quads (LOD 1).</summary>
	[Property, Group( "Detail" ), Range( 0, 12 )] public int Lod1Radius { get; set; } = 3;

	/// <summary>Up to here: a quarter (LOD 2). Beyond: an eighth (LOD 3).</summary>
	[Property, Group( "Detail" ), Range( 0, 16 )] public int Lod2Radius { get; set; } = 6;

	/// <summary>Chunks this close get a collision mesh. Keep small: collision is the expensive part.</summary>
	[Property, Group( "Detail" ), Range( 0, 6 )] public int CollisionRadius { get; set; } = 1;

	/// <summary>Chunks beyond this don't cast shadows. Far shadows are invisible and cost a lot.</summary>
	[Property, Group( "Detail" ), Range( 0, 16 )] public int ShadowRadius { get; set; } = 3;

	// ---- Props ---------------------------------------------------------------------------------

	[Property, Group( "Props" )] public bool SpawnProps { get; set; } = true;

	/// <summary>Houses appear in chunks this close (a chunk is about 200 m).</summary>
	[Property, Group( "Props" ), Range( 0, 10 )] public int HouseRadius { get; set; } = 5;

	/// <summary>Cacti are many and small, so only the nearest chunks get them.</summary>
	[Property, Group( "Props" ), Range( 0, 6 )] public int CactusRadius { get; set; } = 2;

	/// <summary>Cacti per chunk in the nearest chunks (fewer further out).</summary>
	[Property, Group( "Props" ), Range( 0, 80 )] public int CactiPerChunk { get; set; } = 18;

	/// <summary>Rocks, dead trees, barrels, tyres, lamps... (see data/scatter.json) appear in chunks this close.</summary>
	[Property, Group( "Props" ), Range( 0, 6 )] public int ScatterRadius { get; set; } = 2;

	/// <summary>Scales how much of that scatter there is (0 = none).</summary>
	[Property, Group( "Props" ), Range( 0f, 3f )] public float ScatterDensity { get; set; } = 1f;

	[Property, Group( "Road" )] public float RoadWidth { get; set; } = 300f;
	[Property, Group( "Biomes" )] public float BiomeLength { get; set; } = 120000f;
	[Property, Group( "Biomes" )] public float BiomeTransition { get; set; } = 14000f;

	/// <summary>Leave empty to use <c>materials/world/terrain_pbr.vmat</c>.</summary>
	[Property, Group( "Rendering" )] public Material TerrainMaterial { get; set; }

	/// <summary>Leave empty to use <c>materials/world/road_pbr.vmat</c>.</summary>
	[Property, Group( "Rendering" )] public Material RoadMaterial { get; set; }

	public WorldGenerator Generator { get; private set; }

	/// <summary>What a chunk should look like right now, given how far it is from the target.</summary>
	private readonly record struct Wanted( int Lod, bool Collision, bool Shadows );

	private sealed class Chunk
	{
		public GameObject Object;
		public Wanted State;
	}

	private sealed class Pending
	{
		public Task<ChunkGeometry> Task;
		public Wanted State;
		public bool Cancelled;
	}

	private readonly Dictionary<Vector2Int, Chunk> _terrain = new();
	private readonly Dictionary<Vector2Int, Pending> _terrainPending = new();
	private readonly Dictionary<int, Chunk> _road = new();
	private readonly Dictionary<int, Pending> _roadPending = new();

	private Material _terrainMaterial;
	private Material _roadMaterial;
	private Material _guardrailMaterial;
	private GameObject _root;

	private readonly Dictionary<Vector2Int, GameObject> _houses = new();
	private readonly Dictionary<Vector2Int, GameObject> _cacti = new();
	private readonly Dictionary<int, Model> _cactusModels = new();
	private readonly Dictionary<Vector2Int, GameObject> _scatter = new();

	private bool _holdingTarget;
	private GameObject _heldTarget;
	private Vector3 _holdPosition;

	protected override void OnEnabled()
	{
		Components.GetOrCreate<GroundGuard>();   // lifts anything that ends up under the ground
		Rebuild();
	}

	protected override void OnDisabled()
	{
		Clear();
	}

	/// <summary>Throw everything away and regenerate with the current settings.</summary>
	[Button( "Regenerate" ), Group( "World" )]
	public void Rebuild()
	{
		Clear();
		HouseBuilder.ClearCache();
		ScatterLibrary.Reset();

		Generator = new WorldGenerator( new WorldSettings
		{
			Seed = Seed,
			RoadWidth = RoadWidth,
			BiomeLength = BiomeLength,
			BiomeTransition = BiomeTransition,
		} );

		_terrainMaterial = TerrainMaterial ?? Material.Load( "materials/world/terrain_pbr.vmat" );
		_roadMaterial = RoadMaterial ?? Material.Load( "materials/world/road_pbr.vmat" );
		_guardrailMaterial = Material.Load( "materials/world/buildings/guardrail_steel.vmat" );
		_root = new GameObject( true, "World Chunks" );
		_root.Parent = GameObject;
		_root.LocalPosition = Vector3.Zero;
		_root.LocalRotation = Rotation.Identity;
		_root.Flags |= GameObjectFlags.NotSaved; // generated at runtime, never serialised into the scene

		if ( Game.IsPlaying && HoldTargetUntilGroundReady )
			TeleportTo( 0f );
	}

	private void Clear()
	{
		foreach ( var p in _terrainPending.Values ) p.Cancelled = true;
		foreach ( var p in _roadPending.Values ) p.Cancelled = true;
		_terrainPending.Clear();
		_roadPending.Clear();
		_terrain.Clear();
		_road.Clear();
		_houses.Clear();
		_cacti.Clear();
		_scatter.Clear();

		if ( _root.IsValid() ) _root.Destroy();
		_root = null;
	}

	/// <summary>
	/// Move the target (the player) to a point on the road and freeze it until the ground there has loaded.
	/// Handy for testing far-away biomes.
	/// </summary>
	public void TeleportTo( float distanceAlongRoad )
	{
		var target = ResolveTarget();
		if ( !target.IsValid() ) return;

		_holdPosition = GetSpawnPoint( distanceAlongRoad );
		target.WorldPosition = _holdPosition;
		target.Enabled = false;
		_heldTarget = target;
		_holdingTarget = true;
	}

	/// <summary>The explicit target, or the scene's player if none was set.</summary>
	private GameObject ResolveTarget()
	{
		if ( Target.IsValid() ) return Target;
		return Scene.GetAllComponents<PlayerController>().FirstOrDefault()?.GameObject;
	}

	/// <summary>Ground height + road start helper, e.g. for placing the player.</summary>
	public Vector3 GetSpawnPoint( float distanceAlongRoad = 0f )
	{
		var gen = Generator ?? new WorldGenerator( new WorldSettings { Seed = Seed, RoadWidth = RoadWidth } );
		return new Vector3( distanceAlongRoad, gen.RoadCenterY( distanceAlongRoad ), gen.RoadHeight( distanceAlongRoad ) + 40f );
	}

	/// <summary>True once the world exists and the starting target has been released onto solid ground.</summary>
	public bool IsReady => Generator is not null && _root.IsValid() && !_holdingTarget;

	/// <summary>Whether the ground (terrain and road) at this point has a collision mesh yet, i.e. things can rest on it.</summary>
	public bool HasCollisionAt( Vector3 position )
	{
		var roadKey = (int)MathF.Floor( position.x / ChunkSize );
		var groundKey = new Vector2Int( roadKey, (int)MathF.Floor( position.y / ChunkSize ) );
		return _road.TryGetValue( roadKey, out var road ) && road.State.Collision
			&& _terrain.TryGetValue( groundKey, out var ground ) && ground.State.Collision;
	}

	protected override void OnUpdate()
	{
		if ( Generator is null || !_root.IsValid() ) return;

		var focus = FocusPosition();
		int budget = MaxModelsPerFrame;

		UpdateTerrain( focus, ref budget );
		UpdateRoad( focus, ref budget );
		if ( SpawnProps ) UpdateProps( focus );

		if ( _holdingTarget )
		{
			var roadKey = (int)MathF.Floor( _holdPosition.x / ChunkSize );
			var groundKey = new Vector2Int( roadKey, (int)MathF.Floor( _holdPosition.y / ChunkSize ) );

			// Only release once the ground under the target has *collision*, not just a mesh.
			if ( !_road.TryGetValue( roadKey, out var road ) || !road.State.Collision ) return;
			if ( !_terrain.TryGetValue( groundKey, out var ground ) || !ground.State.Collision ) return;

			_holdingTarget = false;
			if ( _heldTarget.IsValid() ) _heldTarget.Enabled = true;
			_heldTarget = null;
		}
	}

	private Vector3 FocusPosition()
	{
		if ( _heldTarget.IsValid() ) return _heldTarget.WorldPosition;
		var target = ResolveTarget();
		if ( target.IsValid() ) return target.WorldPosition;
		if ( Scene.Camera.IsValid() ) return Scene.Camera.WorldPosition;
		return Vector3.Zero;
	}

	// ---- Level of detail -------------------------------------------------------------------

	private Wanted WantedFor( int ring )
	{
		int lod = ring <= Lod0Radius ? 0 : ring <= Lod1Radius ? 1 : ring <= Lod2Radius ? 2 : 3;
		return new Wanted( lod, ring <= CollisionRadius, ring <= ShadowRadius );
	}

	/// <summary>Quads per side for a LOD. Never below 8, and always even (collision needs that).</summary>
	private int ResolutionFor( int lod ) => Math.Max( 8, (Resolution >> lod) & ~1 );

	/// <summary>The curtain under chunk edges, deeper for coarser chunks (their edges are further off).</summary>
	private static float SkirtDepthFor( int lod ) => 120f * (1 << lod);

	// ---- Terrain ---------------------------------------------------------------------------

	private void UpdateTerrain( Vector3 focus, ref int budget )
	{
		int cx = (int)MathF.Floor( focus.x / ChunkSize );
		int cy = (int)MathF.Floor( focus.y / ChunkSize );
		int r = ViewRadius;
		int unload = r + 1;

		// Unload far chunks (a little hysteresis so we don't thrash at the border).
		List<Vector2Int> remove = null;
		foreach ( var key in _terrain.Keys )
		{
			if ( Math.Abs( key.x - cx ) > unload || Math.Abs( key.y - cy ) > unload )
				(remove ??= new()).Add( key );
		}
		if ( remove != null )
		{
			foreach ( var key in remove )
			{
				if ( _terrain.Remove( key, out var chunk ) && chunk.Object.IsValid() )
					chunk.Object.Destroy();
			}
		}

		// Finish chunks whose geometry is ready. Results that no longer match what's wanted are dropped.
		List<Vector2Int> done = null;
		foreach ( var (key, pending) in _terrainPending )
		{
			if ( pending.Task.IsCompleted ) (done ??= new()).Add( key );
		}
		if ( done != null )
		{
			foreach ( var key in done )
			{
				if ( budget <= 0 ) break;
				var pending = _terrainPending[key];
				_terrainPending.Remove( key );

				if ( pending.Cancelled || pending.Task.IsFaulted ) continue;

				int ring = Math.Max( Math.Abs( key.x - cx ), Math.Abs( key.y - cy ) );
				if ( ring > unload ) continue;
				if ( pending.State != WantedFor( ring ) ) continue; // moved on while it was building; re-requested below

				var origin = new Vector3( key.x * ChunkSize, key.y * ChunkSize, 0f );
				SpawnChunk( _terrain, key, pending.Task.Result, origin, $"Terrain {key.x},{key.y}", _terrainMaterial, pending.State );
				budget--;
			}
		}

		// Work out which chunks are missing or at the wrong detail. Missing ones first, then nearest first.
		var todo = new List<(Vector2Int key, int ring, bool missing)>();
		for ( int dy = -r; dy <= r; dy++ )
		{
			for ( int dx = -r; dx <= r; dx++ )
			{
				var key = new Vector2Int( cx + dx, cy + dy );
				if ( _terrainPending.ContainsKey( key ) ) continue;

				int ring = Math.Max( Math.Abs( dx ), Math.Abs( dy ) );
				bool has = _terrain.TryGetValue( key, out var chunk );
				if ( has && chunk.State == WantedFor( ring ) ) continue;
				todo.Add( (key, ring, !has) );
			}
		}
		todo.Sort( ( a, b ) => a.missing != b.missing ? (a.missing ? -1 : 1) : a.ring.CompareTo( b.ring ) );

		// Don't flood the thread pool.
		int slots = Math.Max( 0, MaxConcurrentBuilds - _terrainPending.Count );
		for ( int i = 0; i < todo.Count && i < slots; i++ )
		{
			var key = todo[i].key;
			var state = WantedFor( todo[i].ring );
			var gen = Generator;
			float size = ChunkSize;
			int res = ResolutionFor( state.Lod );
			float skirt = SkirtDepthFor( state.Lod );
			bool collision = state.Collision;
			var origin = new Vector2( key.x * size, key.y * size );

			_terrainPending[key] = new Pending
			{
				State = state,
				Task = GameTask.RunInThreadAsync( () => ChunkMeshBuilder.BuildTerrain( gen, origin, size, res, collision, skirt ) )
			};
		}
	}

	// ---- Road ------------------------------------------------------------------------------

	/// <summary>Road quads get longer with distance. Dashes are still drawn, just coarser, and the detail is invisible there.</summary>
	private static float RoadStepFor( int lod ) => lod <= 1 ? 64f : lod == 2 ? 128f : 256f;

	private void UpdateRoad( Vector3 focus, ref int budget )
	{
		int cx = (int)MathF.Floor( focus.x / ChunkSize );
		int r = ViewRadius;
		int unload = r + 1;

		List<int> remove = null;
		foreach ( var key in _road.Keys )
		{
			if ( Math.Abs( key - cx ) > unload ) (remove ??= new()).Add( key );
		}
		if ( remove != null )
		{
			foreach ( var key in remove )
			{
				if ( _road.Remove( key, out var chunk ) && chunk.Object.IsValid() )
					chunk.Object.Destroy();
			}
		}

		List<int> done = null;
		foreach ( var (key, pending) in _roadPending )
		{
			if ( pending.Task.IsCompleted ) (done ??= new()).Add( key );
		}
		if ( done != null )
		{
			foreach ( var key in done )
			{
				if ( budget <= 0 ) break;
				var pending = _roadPending[key];
				_roadPending.Remove( key );

				if ( pending.Cancelled || pending.Task.IsFaulted ) continue;

				int ring = Math.Abs( key - cx );
				if ( ring > unload ) continue;
				if ( pending.State != RoadWantedFor( ring ) ) continue;

				var origin = new Vector3( key * ChunkSize, 0f, 0f );
				SpawnChunk( _road, key, pending.Task.Result, origin, $"Road {key}", _roadMaterial, pending.State );
				budget--;
			}
		}

		for ( int dx = -r; dx <= r; dx++ )
		{
			int key = cx + dx;
			if ( _roadPending.ContainsKey( key ) ) continue;

			int ring = Math.Abs( dx );
			var state = RoadWantedFor( ring );
			if ( _road.TryGetValue( key, out var chunk ) && chunk.State == state ) continue;

			var gen = Generator;
			float start = key * ChunkSize;
			float len = ChunkSize;
			float step = RoadStepFor( state.Lod );
			bool collision = state.Collision;
			bool rails = state.Lod <= 1; // guardrails are thin and only matter up close
			_roadPending[key] = new Pending
			{
				State = state,
				Task = GameTask.RunInThreadAsync( () => ChunkMeshBuilder.BuildRoad( gen, start, len, collision, rails, step ) )
			};
		}
	}

	/// <summary>
	/// The road ribbon is cheap, so it reaches one chunk further than terrain collision: the road is what you
	/// actually drive on, and it should be solid before the ground beside it.
	/// </summary>
	private Wanted RoadWantedFor( int ring )
	{
		var w = WantedFor( ring );
		return w with { Collision = ring <= CollisionRadius + 1 };
	}

	// ---- Props: houses and cacti -------------------------------------------------------------

	private void UpdateProps( Vector3 focus )
	{
		int cx = (int)MathF.Floor( focus.x / ChunkSize );
		int cy = (int)MathF.Floor( focus.y / ChunkSize );

		RemoveFarProps( _houses, cx, cy, HouseRadius + 1 );
		RemoveFarProps( _cacti, cx, cy, CactusRadius + 1 );
		RemoveFarProps( _scatter, cx, cy, ScatterRadius + 1 );

		// Add at most one chunk's worth of props per frame, nearest first, so streaming never hitches.
		int radius = Math.Max( Math.Max( HouseRadius, CactusRadius ), ScatterRadius );
		for ( int ring = 0; ring <= radius; ring++ )
		{
			for ( int dy = -ring; dy <= ring; dy++ )
			{
				for ( int dx = -ring; dx <= ring; dx++ )
				{
					if ( Math.Max( Math.Abs( dx ), Math.Abs( dy ) ) != ring ) continue;
					var key = new Vector2Int( cx + dx, cy + dy );

					if ( ring <= HouseRadius && !_houses.ContainsKey( key ) )
					{
						SpawnHouses( key );
						return;
					}

					if ( ring <= CactusRadius && !_cacti.ContainsKey( key ) )
					{
						SpawnCacti( key, ring );
						return;
					}

					if ( ring <= ScatterRadius && !_scatter.ContainsKey( key ) )
					{
						SpawnScatter( key, ring );
						return;
					}
				}
			}
		}
	}

	private static void RemoveFarProps( Dictionary<Vector2Int, GameObject> props, int cx, int cy, int radius )
	{
		List<Vector2Int> remove = null;
		foreach ( var key in props.Keys )
		{
			if ( Math.Abs( key.x - cx ) > radius || Math.Abs( key.y - cy ) > radius )
				(remove ??= new()).Add( key );
		}
		if ( remove is null ) return;

		foreach ( var key in remove )
		{
			if ( props.Remove( key, out var go ) && go.IsValid() )
				go.Destroy();
		}
	}

	private GameObject NewPropRoot( string name )
	{
		var go = new GameObject( true, name );
		go.Parent = _root;
		go.WorldPosition = Vector3.Zero;
		go.Flags |= GameObjectFlags.NotSaved;
		return go;
	}

	private void SpawnHouses( Vector2Int key )
	{
		// An (often empty) root is stored so a chunk with no houses is only computed once.
		var root = NewPropRoot( $"Houses {key.x},{key.y}" );
		_houses[key] = root;

		foreach ( var inst in PropPlacer.Houses( Generator, key, ChunkSize ) )
		{
			var info = HouseBuilder.Get( inst.Variant );
			if ( !info.Model.IsValid() ) continue;

			var go = new GameObject( true, "House" );
			go.Parent = root;
			go.WorldPosition = inst.Position;
			go.WorldRotation = Rotation.FromYaw( inst.Yaw );
			go.WorldScale = inst.Scale;

			var renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = info.Model;

			// The model carries its own collision (walls, floors, stairs, furniture), so you can walk inside.
			go.AddComponent<ModelCollider>().Model = info.Model;

			go.AddComponent<HouseLights>().Setup( info.Lights, info.LightRadius, info.Furniture );
		}
	}

	private void SpawnCacti( Vector2Int key, int ring )
	{
		var root = NewPropRoot( $"Cacti {key.x},{key.y}" );
		_cacti[key] = root;

		int count = ring <= 1 ? CactiPerChunk : Math.Max( 0, CactiPerChunk / 2 );
		foreach ( var inst in PropPlacer.Cacti( Generator, key, ChunkSize, count ) )
		{
			var def = CactusModels.All[inst.Variant];
			if ( !_cactusModels.TryGetValue( inst.Variant, out var model ) || !model.IsValid() )
				_cactusModels[inst.Variant] = model = Model.Load( def.Path );
			if ( model is null ) continue;

			var go = new GameObject( true, "Cactus" );
			go.Parent = root;
			go.WorldPosition = inst.Position;
			go.WorldRotation = Rotation.FromYaw( inst.Yaw );
			go.WorldScale = inst.Scale;

			var renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = model;
			renderer.RenderType = ring <= 1 ? ModelRenderer.ShadowRenderType.On : ModelRenderer.ShadowRenderType.Off;

			if ( def.Kind != CactusKind.Pear || def.Height > 18f )
			{
				if ( ColliderGenerator.For( model ) is not null ) ColliderGenerator.Add( go, model );
				else
				{
					var box = go.AddComponent<BoxCollider>();
					box.Scale = new Vector3( def.Width * 0.5f, def.Width * 0.5f, def.Height );
					box.Center = new Vector3( 0, 0, def.Height * 0.5f );
				}
			}
		}
	}

	private void SpawnScatter( Vector2Int key, int ring )
	{
		var root = NewPropRoot( $"Scatter {key.x},{key.y}" );
		_scatter[key] = root;
		if ( ScatterDensity <= 0.01f ) return;

		foreach ( var inst in ScatterLibrary.Place( Generator, key, ChunkSize, ScatterDensity * (ring <= 1 ? 1f : 0.6f) ) )
		{
			var model = ScatterLibrary.GetModel( inst.Def );
			if ( model is null ) continue;

			var go = new GameObject( true, inst.Def.Id );
			go.Parent = root;
			go.WorldPosition = inst.Position;
			go.WorldRotation = Rotation.FromYaw( inst.Yaw ) * Rotation.From( inst.Lean, 0f, inst.Lean * 0.6f );
			go.WorldScale = inst.Scale;

			var renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = model;
			renderer.RenderType = ring <= 1 ? ModelRenderer.ShadowRenderType.On : ModelRenderer.ShadowRenderType.Off;

			// a bin has its own lid: on it, or knocked off and lying beside it
			if ( !string.IsNullOrEmpty( inst.Def.LidOn ) )
			{
				bool off = (inst.Yaw * 0.0137f) % 1f < inst.Def.LidOffChance;
				var lidModel = ScatterLibrary.GetModel( off ? inst.Def.LidOff : inst.Def.LidOn );
				if ( lidModel is not null )
				{
					var lid = new GameObject( true, "Lid" );
					lid.Parent = root;
					lid.WorldScale = inst.Scale;
					if ( off )
					{
						var side = Rotation.FromYaw( inst.Yaw + 70f ).Forward * 55f * inst.Scale;
						lid.WorldPosition = inst.Position + side + Vector3.Up * lidModel.Bounds.Size.z * 0.5f * inst.Scale;
						lid.WorldRotation = Rotation.FromYaw( inst.Yaw * 3f );
					}
					else
					{
						lid.WorldPosition = inst.Position;
						lid.WorldRotation = go.WorldRotation;
					}
					var lr = lid.AddComponent<ModelRenderer>();
					lr.Model = lidModel;
					lr.RenderType = renderer.RenderType;
				}
			}

			if ( inst.Def.Collide == "none" ) continue;
			// real collision from the model's geometry (rocks, fences and so on get their actual shape); a box only where a box fits
			ColliderGenerator.Add( go, model, inst.Def.Collide == "upright" ? new Vector3( 0.7f, 0.7f, 1f ) : Vector3.One );
		}
	}

	// ---- Shared ----------------------------------------------------------------------------

	private void SpawnChunk<TKey>( Dictionary<TKey, Chunk> into, TKey key, ChunkGeometry geo, Vector3 origin, string name, Material material, Wanted state )
	{
		var model = ChunkMeshBuilder.CreateModel( geo, material, name, _guardrailMaterial );
		if ( model is null ) return;

		var go = new GameObject( true, name );
		go.Parent = _root;
		go.WorldPosition = origin;
		go.Flags |= GameObjectFlags.NotSaved;

		var renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = state.Shadows ? ModelRenderer.ShadowRenderType.On : ModelRenderer.ShadowRenderType.Off;

		if ( geo.HasCollision )
		{
			var collider = go.AddComponent<ModelCollider>();
			collider.Model = model;
		}

		// Swap in the new version, then drop the one it replaces (no flicker, no gap to fall through).
		if ( into.TryGetValue( key, out var old ) && old.Object.IsValid() )
			old.Object.Destroy();

		into[key] = new Chunk { Object = go, State = state };
	}
}
