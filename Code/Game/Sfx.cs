namespace LongTrip.Gameplay;

/// <summary>
/// The game's basic sounds. They are the engine's own stock sounds (UI blips for the inventory, melee impacts for hits and building), played
/// through one place so they can be changed or muted together. Every call is safe: a missing sound only logs once and is skipped.
/// </summary>
public static class Sfx
{
	public const string Press = "sounds/kenney/ui/ui.button.press.sound";
	public const string DragStart = "sounds/kenney/ui/ui.drag.start.sound";
	public const string DragStop = "sounds/kenney/ui/ui.drag.stop.sound";
	public const string Deny = "sounds/kenney/ui/ui.button.deny.sound";
	public const string Open = "sounds/kenney/ui/ui.popup.message.open.sound";
	public const string Close = "sounds/kenney/ui/ui.popup.message.close.sound";
	public const string Craft = "sounds/kenney/ui/ui.favourite.sound";
	public const string Cloth = "sounds/Impacts/Melee/impact-melee-cloth.sound";
	public const string Wood = "sounds/Impacts/Melee/impact-melee-wood.sound";
	public const string Metal = "sounds/Impacts/Melee/impact-melee-metal.sound";
	public const string Flesh = "sounds/Impacts/Melee/impact-melee-flesh.sound";
	public const string Glass = "sounds/Impacts/Melee/impact-melee-glass.sound";
	public const string Concrete = "sounds/Impacts/Melee/impact-melee-concrete.sound";

	// from the s&box catalog (see CREDITS.md); the packages are listed in the project file
	public const string FlashlightClick = "audio/flashlight-click.sound";
	public const string Rain = "sounds/ambient/rain1_fall_metal_roof.sound";
	public const string WindLoop = "sounds/subzero-studios/ambience/wind/wind-outdoors.sound";
	public const string Birds = "sounds/subzero-studios/ambience/wind/morning-birds.sound";
	public const string NightHum = "sound effects/amb_void_loop (wav)/ambvoidhollowhum.sound";
	public const string LampBuzz = "sounds/fluorescent_light.sound";
	public static readonly string[] Thunder = { "sounds/csz/nature/thunder1.sound", "sounds/csz/nature/thunder2.sound", "sounds/csz/nature/thunder3.sound", "sounds/csz/nature/thunder4.sound" };
	public static readonly string[] Explosions = { "sound/explosion1.sound", "sounds/explosion_urban.sound" };

	/// <summary>One of several sounds, at random.</summary>
	public static string Pick( string[] sounds ) => sounds[Game.Random.Int( sounds.Length - 1 )];

	/// <summary>Start a sound and keep hold of it (to set its volume or stop it). Null if it could not be played.</summary>
	public static SoundHandle Loop( string sound )
	{
		if ( _failed.Contains( sound ) ) return null;
		try { return Sandbox.Sound.Play( sound ); }
		catch ( System.Exception e ) { _failed.Add( sound ); Log.Warning( $"[sfx] {sound}: {e.Message}" ); return null; }
	}

	/// <summary>Start a sound in the world and keep hold of it.</summary>
	public static SoundHandle Loop( string sound, Vector3 position )
	{
		if ( _failed.Contains( sound ) ) return null;
		try { return Sandbox.Sound.Play( sound, position ); }
		catch ( System.Exception e ) { _failed.Add( sound ); Log.Warning( $"[sfx] {sound}: {e.Message}" ); return null; }
	}

	private static readonly HashSet<string> _failed = new();

	/// <summary>A sound with no position (heard the same everywhere: menus and the like).</summary>
	public static void Play( string sound )
	{
		if ( _failed.Contains( sound ) ) return;
		try { Sandbox.Sound.Play( sound ); }
		catch ( System.Exception e ) { _failed.Add( sound ); Log.Warning( $"[sfx] {sound}: {e.Message}" ); }
	}

	/// <summary>A sound in the world.</summary>
	public static void Play( string sound, Vector3 position )
	{
		if ( _failed.Contains( sound ) ) return;
		try { Sandbox.Sound.Play( sound, position ); }
		catch ( System.Exception e ) { _failed.Add( sound ); Log.Warning( $"[sfx] {sound}: {e.Message}" ); }
	}

	/// <summary>Which impact sound suits an item (by its category): metal tools, soft food and cloth, everything else wood.</summary>
	public static string ImpactFor( ItemDefinition def ) => def?.Category switch
	{
		"Weapon" or "Junk" or "Fuel" or "Prop" => Metal,
		"Food" or "Drink" or "Armor" or "Backpack" => Cloth,
		_ => Wood,
	};
}
