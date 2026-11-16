namespace LongTrip.Gameplay;

/// <summary>
/// Small pictures of items for the inventory and the hotbar. Each icon is the item's own model drawn once, from a fixed angle, into a little
/// texture by a hidden camera far from anywhere; the textures are kept, so every item is only ever drawn once. Icons are made a few per frame
/// so opening the inventory never hitches.
/// </summary>
public sealed class ItemIcons : Component
{
	public static ItemIcons Instance { get; private set; }

	private const int Size = 128;
	private static readonly Vector3 Studio = new( 60000f, 60000f, 60000f );

	private readonly Dictionary<string, Texture> _ready = new();
	private readonly Queue<string> _queue = new();
	private readonly HashSet<string> _queued = new();

	private GameObject _stage;
	private GameObject _cameraObject;
	private CameraComponent _camera;

	protected override void OnEnabled() => Instance = this;
	protected override void OnStart() => Instance = this;
	protected override void OnDestroy()
	{
		if ( Instance == this ) Instance = null;
		if ( _stage.IsValid() ) _stage.Destroy();
		if ( _cameraObject.IsValid() ) _cameraObject.Destroy();
	}

	/// <summary>The icon of an item, or null while it is still being made (ask again next frame).</summary>
	public Texture Get( string itemId )
	{
		if ( string.IsNullOrEmpty( itemId ) ) return null;
		if ( _ready.TryGetValue( itemId, out var texture ) ) return texture;
		if ( _queued.Add( itemId ) ) _queue.Enqueue( itemId );
		return null;
	}

	public static Texture Icon( string itemId ) => Instance?.Get( itemId );

	protected override void OnUpdate()
	{
		// a few icons a frame
		for ( int n = 0; n < 3 && _queue.Count > 0; n++ )
		{
			var id = _queue.Dequeue();
			_ready[id] = Render( id );
		}
	}

	private Texture Render( string itemId )
	{
		var def = ItemLibrary.Get( itemId );
		var model = def is null ? null : ItemLibrary.GetModel( def );
		if ( model is null || !model.IsValid() ) return null;

		if ( !_cameraObject.IsValid() )
		{
			_cameraObject = Scene.CreateObject();
			_cameraObject.Name = "Icon camera";
			_cameraObject.Flags |= GameObjectFlags.NotSaved;
			_camera = _cameraObject.Components.Create<CameraComponent>();
			_camera.IsMainCamera = false;
			_camera.FieldOfView = 28f;
			_camera.ZNear = 1f;
			_camera.ZFar = 4000f;
			_camera.BackgroundColor = new Color( 0f, 0f, 0f, 0f );
			_camera.Enabled = false;   // only draws when asked to
		}

		if ( _stage.IsValid() ) _stage.Destroy();
		_stage = Scene.CreateObject();
		_stage.Name = "Icon stage";
		_stage.Flags |= GameObjectFlags.NotSaved;
		_stage.WorldPosition = Studio;

		var bounds = model.Bounds;
		float scale = def.ModelScale;
		var holder = Scene.CreateObject();
		holder.SetParent( _stage, false );
		holder.LocalScale = scale;
		holder.LocalPosition = -bounds.Center * scale;
		// a three-quarter view, tipped a little so long things (tools) read well
		holder.LocalRotation = Rotation.From( 0f, 0f, 0f );
		var renderer = holder.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		float radius = MathF.Max( 2f, bounds.Size.Length * 0.5f * scale );
		float distance = radius / MathF.Sin( 14f * MathF.PI / 180f ) * 1.05f;
		var direction = new Vector3( 1f, -0.85f, 0.65f ).Normal;
		_cameraObject.WorldPosition = Studio + direction * distance;
		_cameraObject.WorldRotation = Rotation.LookAt( -direction );

		// a light of its own, so an icon looks the same in any weather or time of day
		var lightObject = Scene.CreateObject();
		lightObject.SetParent( _stage, false );
		lightObject.WorldPosition = _cameraObject.WorldPosition + Vector3.Up * distance * 0.4f + new Vector3( 0f, -distance * 0.3f, 0f );
		var light = lightObject.Components.Create<PointLight>();
		light.LightColor = new Color( 1f, 0.97f, 0.92f );
		light.Radius = distance * 4f;
		light.Intensity = 9f;
		light.Shadows = false;

		var texture = Texture.CreateRenderTarget( "icon_" + itemId, ImageFormat.RGBA8888, new Vector2( Size, Size ) );
		_camera.Enabled = true;
		_camera.RenderToTexture( texture );
		_camera.Enabled = false;

		_stage.Destroy();
		_stage = null;
		return texture;
	}
}
