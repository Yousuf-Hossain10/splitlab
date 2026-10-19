using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Splitlab.Sdk.Bucketing;

/// <summary>
/// MurmurHash3, x86 32-bit variant. Non-cryptographic, fast and stable across
/// platforms, which is what deterministic bucketing needs (see ADR-0003).
/// </summary>
public static class MurmurHash3
{
    private const uint C1 = 0xCC9E2D51;
    private const uint C2 = 0x1B873593;

    /// <summary>Hashes the UTF-8 encoding of <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static uint Hash32(string value, uint seed = 0)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Hash32(Encoding.UTF8.GetBytes(value), seed);
    }

    /// <summary>Hashes a block of bytes.</summary>
    public static uint Hash32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        unchecked
        {
            uint h1 = seed;
            int blockCount = data.Length / 4;

            for (int i = 0; i < blockCount; i++)
            {
                uint k1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4));
                h1 ^= MixK(k1);
                h1 = BitOperations.RotateLeft(h1, 13);
                h1 = (h1 * 5) + 0xE6546B64;
            }

            ReadOnlySpan<byte> tail = data[(blockCount * 4)..];
            uint tailValue = 0;
            switch (tail.Length)
            {
                case 3:
                    tailValue ^= (uint)tail[2] << 16;
                    goto case 2;
                case 2:
                    tailValue ^= (uint)tail[1] << 8;
                    goto case 1;
                case 1:
                    tailValue ^= tail[0];
                    h1 ^= MixK(tailValue);
                    break;
            }

            h1 ^= (uint)data.Length;
            return FinalMix(h1);
        }
    }

    private static uint MixK(uint k)
    {
        unchecked
        {
            k *= C1;
            k = BitOperations.RotateLeft(k, 15);
            k *= C2;
            return k;
        }
    }

    private static uint FinalMix(uint h)
    {
        unchecked
        {
            h ^= h >> 16;
            h *= 0x85EBCA6B;
            h ^= h >> 13;
            h *= 0xC2B2AE35;
            h ^= h >> 16;
            return h;
        }
    }
}
