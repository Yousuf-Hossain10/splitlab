using System.Text;
using Splitlab.Sdk.Bucketing;

namespace Splitlab.Sdk.Tests.Bucketing;

public class MurmurHash3Tests
{
    // Reference values from the canonical MurmurHash3_x86_32 implementation.
    [Theory]
    [InlineData("", 0u, 0x00000000u)]
    [InlineData("", 1u, 0x514E28B7u)]
    [InlineData("", 0xFFFFFFFFu, 0x81F16F39u)]
    [InlineData("a", 0u, 0x3C2569B2u)]
    [InlineData("ab", 0u, 0x9BBFD75Fu)]
    [InlineData("abc", 0u, 0xB3DD93FAu)]
    [InlineData("test", 0u, 0xBA6BD213u)]
    [InlineData("test", 0x9747B28Cu, 0x704B81DCu)]
    [InlineData("Hello, world!", 0u, 0xC0363E43u)]
    [InlineData("aaaa", 0x9747B28Cu, 0x5A97808Au)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0u, 0x2E4FF723u)]
    public void Hash32MatchesReferenceVectors(string input, uint seed, uint expected)
    {
        Assert.Equal(expected, MurmurHash3.Hash32(input, seed));
    }

    [Fact]
    public void StringOverloadHashesUtf8Bytes()
    {
        const string value = "ünïcode-用户";

        uint fromString = MurmurHash3.Hash32(value, 1u);
        uint fromBytes = MurmurHash3.Hash32(Encoding.UTF8.GetBytes(value), 1u);

        Assert.Equal(fromBytes, fromString);
    }

    [Fact]
    public void DefaultSeedIsZero()
    {
        Assert.Equal(MurmurHash3.Hash32("test", 0u), MurmurHash3.Hash32("test"));
    }

    [Fact]
    public void NullStringIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => MurmurHash3.Hash32((string)null!, 0u));
    }
}
