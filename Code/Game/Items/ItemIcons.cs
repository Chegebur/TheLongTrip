namespace LongTrip.Gameplay;

/// <summary>
/// Small pictures of items for the inventory and the hotbar. They are <b>baked</b>: <c>Assets/ui/icons/&lt;item id&gt;.png</c> is loaded when it exists,
/// so nothing is drawn while playing. An item without a baked picture (a brand new one) is drawn once from its model, one per frame, by a hidden camera
/// far from anywhere. <see cref="ExportAll"/> draws every item and writes the pictures out, to be copied into <c>Assets/ui/icons</c> (done with the
/// "bake icons" helper; run it again after adding or changing items).
/// </summary>
public sealed class ItemIcons : Component
{
	public static ItemIcons Instance { get; private set; }

	private const int Size = 128;
	private static readonly Vector3 Studio = new( 60000f, 60000f, 60000f );

	private readonly Dictionary<string, Texture> _ready = new();
	private readonly Queue<string> _queue = new();
	private readonly HashSet<string> _queued = new();

	private const string StudioTag = "icon_studio";
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

		// the baked picture, if there is one
		string path = $"ui/icons/{itemId}.png";
		try
		{
			if ( FileSystem.Mounted.FileExists( path ) )
			{
				var baked = Texture.Load( FileSystem.Mounted, path, false );
				if ( baked is not null && baked.IsValid() ) { _ready[itemId] = baked; return baked; }
			}
		}
		catch ( System.Exception ) { }

		if ( _queued.Add( itemId ) ) _queue.Enqueue( itemId );
		return null;
	}

	public static Texture Icon( string itemId ) => Instance?.Get( itemId );

	protected override void OnUpdate()
	{
		// one icon a frame
		if ( _queue.Count > 0 )
		{
			var id = _queue.Dequeue();
			_ready[id] = Render( id );
		}
	}

	private int _serial;

	/// <summary>Set the studio up for one item and draw it with <paramref name="draw"/>. Every picture gets its own spot in the studio, the camera only sees a short way, and the stage is switched off at once, so pictures never show through each other.</summary>
	private bool Draw( string itemId, Action<CameraComponent> draw )
	{
		var def = ItemLibrary.Get( itemId );
		var model = def is null ? null : ItemLibrary.GetModel( def );
		if ( model is null || !model.IsValid() ) return false;

		if ( !_cameraObject.IsValid() )
		{
			_cameraObject = Scene.CreateObject();
			_cameraObject.Name = "Icon camera";
			_cameraObject.Flags |= GameObjectFlags.NotSaved;
			_camera = _cameraObject.Components.Create<CameraComponent>();
			_camera.IsMainCamera = false;
			_camera.FieldOfView = 28f;
			_camera.ZNear = 1f;
			_camera.BackgroundColor = new Color( 0f, 0f, 0f, 0f );
			_camera.Enabled = false;   // only draws when asked to
			_camera.RenderTags.Add( StudioTag );   // draws only what is tagged for the studio: no sky, ground or fog behind the item
		}

		var spot = Studio + new Vector3( (_serial++ % 100) * 3000f, 0f, 0f );
		var stage = Scene.CreateObject();
		stage.Name = "Icon stage";
		stage.Flags |= GameObjectFlags.NotSaved;
		stage.WorldPosition = spot;
		stage.Tags.Add( StudioTag );

		var bounds = model.Bounds;
		float scale = def.ModelScale;
		var holder = Scene.CreateObject();
		holder.SetParent( stage, false );
		holder.Tags.Add( StudioTag );
		holder.LocalScale = scale;
		holder.LocalPosition = -bounds.Center * scale;
		var renderer = holder.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		float radius = MathF.Max( 2f, bounds.Size.Length * 0.5f * scale );
		float distance = radius / MathF.Sin( 14f * MathF.PI / 180f ) * 1.05f;
		var direction = new Vector3( 1f, -0.85f, 0.65f ).Normal;
		_camera.ZFar = distance + radius * 2f;   // sees only its own stage
		_cameraObject.WorldPosition = spot + direction * distance;
		_cameraObject.WorldRotation = Rotation.LookAt( -direction );

		// lit by lights of its own, tagged for the studio, so an icon looks the same in any weather
		var lightObject = Scene.CreateObject();
		lightObject.SetParent( stage, false );
		lightObject.Tags.Add( StudioTag );
		lightObject.WorldPosition = _cameraObject.WorldPosition + Vector3.Up * distance * 0.4f + new Vector3( 0f, -distance * 0.3f, 0f );
		var light = lightObject.Components.Create<PointLight>();
		light.LightColor = new Color( 1f, 0.97f, 0.92f );
		light.Radius = distance * 4f;
		light.Intensity = 1.2f;
		light.Shadows = false;

		_camera.Enabled = true;
		draw( _camera );
		_camera.Enabled = false;

		stage.Enabled = false;   // gone from the studio at once (Destroy only happens at the end of the frame)
		stage.Destroy();
		return true;
	}

	private Texture Render( string itemId )
	{
		var texture = Texture.CreateRenderTarget( "icon_" + itemId, ImageFormat.RGBA8888, new Vector2( Size, Size ) );
		return Draw( itemId, cam => cam.RenderToTexture( texture ) ) ? texture : null;
	}

	/// <summary>
	/// Draw every item and write the pictures as PNG files into the game's data folder (<c>icons/&lt;id&gt;.png</c>). Returns the folder's full path and
	/// how many were written. Copy them to <c>Assets/ui/icons</c> and the game loads them instead of drawing anything.
	/// </summary>
	public (string Folder, int Count) ExportAll()
	{
		FileSystem.Data.CreateDirectory( "icons" );
		int count = 0;
		foreach ( var def in ItemLibrary.All.ToList() )
		{
			var bitmap = new Bitmap( Size * 2, Size * 2 );
			if ( !Draw( def.Id, cam => cam.RenderToBitmap( bitmap ) ) ) continue;
			FileSystem.Data.WriteAllBytes( $"icons/{def.Id}.png", bitmap.ToPng() );
			count++;
		}
		return (FileSystem.Data.GetFullPath( "icons" ), count);
	}
}
