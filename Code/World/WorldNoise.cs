using System;

namespace LongTrip.World;

/// <summary>
/// Small, seeded, allocation-free 2D gradient noise. Pure math, so it is safe to call from
/// worker threads while chunks are generated in the background.
/// All outputs are roughly in the range [-1, 1] unless stated otherwise.
/// </summary>
public readonly struct WorldNoise
{
	private readonly int _seed;

	public WorldNoise( int seed ) => _seed = seed;

	/// <summary>Integer hash -> [0, 1).</summary>
	public static float Hash01( int seed, int x, int y = 0 )
	{
		unchecked
		{
			uint h = (uint)seed * 0x9E3779B1u;
			h ^= (uint)x * 0x85EBCA77u;
			h = (h << 13) | (h >> 19);
			h ^= (uint)y * 0xC2B2AE3Du;
			h ^= h >> 16;
			h *= 0x7FEB352Du;
			h ^= h >> 15;
			h *= 0x846CA68Bu;
			h ^= h >> 16;
			return (h & 0x00FFFFFF) / 16777216f;
		}
	}

	private float Gradient( int ix, int iy, float dx, float dy )
	{
		float a = Hash01( _seed, ix, iy ) * MathF.PI * 2f;
		return MathF.Cos( a ) * dx + MathF.Sin( a ) * dy;
	}

	private static float Fade( float t ) => t * t * t * (t * (t * 6f - 15f) + 10f);

	/// <summary>Single octave gradient noise, roughly [-1, 1].</summary>
	public float Sample( float x, float y )
	{
		int x0 = (int)MathF.Floor( x );
		int y0 = (int)MathF.Floor( y );
		float fx = x - x0;
		float fy = y - y0;

		float n00 = Gradient( x0, y0, fx, fy );
		float n10 = Gradient( x0 + 1, y0, fx - 1f, fy );
		float n01 = Gradient( x0, y0 + 1, fx, fy - 1f );
		float n11 = Gradient( x0 + 1, y0 + 1, fx - 1f, fy - 1f );

		float u = Fade( fx );
		float v = Fade( fy );
		float nx0 = n00 + (n10 - n00) * u;
		float nx1 = n01 + (n11 - n01) * u;
		return (nx0 + (nx1 - nx0) * v) * 1.414f;
	}

	/// <summary>Fractal (fBm) noise, roughly [-1, 1].</summary>
	public float Fbm( float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f )
	{
		float sum = 0f, amp = 1f, norm = 0f;
		for ( int i = 0; i < octaves; i++ )
		{
			// Offset each octave so they don't line up at the origin.
			sum += Sample( x + i * 17.31f, y - i * 9.73f ) * amp;
			norm += amp;
			amp *= gain;
			x *= lacunarity;
			y *= lacunarity;
		}
		return sum / norm;
	}

	/// <summary>
	/// Ridged noise in [0, 1] - crests good for dunes and ridgelines. The crest is slightly rounded
	/// (smooth abs instead of a hard fold), so it leaves no sharp creases to drive over.
	/// </summary>
	public float Ridged( float x, float y, int octaves )
	{
		float sum = 0f, amp = 1f, norm = 0f;
		for ( int i = 0; i < octaves; i++ )
		{
			float s = Sample( x + i * 5.1f, y + i * 3.7f );
			float n = MathF.Max( 0f, 1f - (MathF.Sqrt( s * s + 0.03f ) - 0.1732f) );
			sum += n * n * amp;
			norm += amp;
			amp *= 0.5f;
			x *= 2f;
			y *= 2f;
		}
		return sum / norm;
	}
}
