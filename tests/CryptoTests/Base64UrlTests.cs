using Services.Crypto;
using Xunit;

namespace CryptoTests;

public class Base64UrlTests
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(10)]
	public void RoundTrips_ArbitraryByteLengths(int length)
	{
		byte[] bytes = new byte[length];
		for (int i = 0; i < length; i++) bytes[i] = (byte)((i * 37 + 5) % 256);

		string encoded = Base64Url.Encode(bytes);
		Assert.DoesNotContain('=', encoded);
		Assert.DoesNotContain('+', encoded);
		Assert.DoesNotContain('/', encoded);

		Assert.True(Base64Url.TryDecode(encoded, out byte[] decoded));
		Assert.Equal(bytes, decoded);
	}

	[Theory]
	[InlineData("f", "Zg")]
	[InlineData("fo", "Zm8")]
	[InlineData("foo", "Zm9v")]
	[InlineData("foob", "Zm9vYg")]
	[InlineData("fooba", "Zm9vYmE")]
	[InlineData("foobar", "Zm9vYmFy")]
	public void MatchesKnownVectors(string input, string expected)
	{
		byte[] bytes = System.Text.Encoding.ASCII.GetBytes(input);
		Assert.Equal(expected, Base64Url.Encode(bytes));
		Assert.True(Base64Url.TryDecode(expected, out byte[] decoded));
		Assert.Equal(bytes, decoded);
	}

	[Theory]
	[InlineData("")]
	[InlineData("Zm9v=")]
	[InlineData("ab+c")]
	[InlineData("ab/c")]
	[InlineData("abcde")]
	[InlineData("Zm9v ")]
	[InlineData("Zm9v\n")]
	[InlineData(null)]
	public void RejectsNonCanonicalInput(string? input)
	{
		Assert.False(Base64Url.TryDecode(input, out _));
	}

	[Fact]
	public void RejectsNonZeroPaddingBits_TwoCharTail()
	{
		// "Zh" is alphabet-valid and length-valid but its trailing 4 padding bits are not zero.
		Assert.False(Base64Url.TryDecode("Zh", out _));
	}

	[Fact]
	public void RejectsNonZeroPaddingBits_ThreeCharTail()
	{
		Assert.True(Base64Url.TryDecode("Zm8", out byte[] canonical));
		Assert.Equal(new byte[] { 0x66, 0x6f }, canonical);
		Assert.False(Base64Url.TryDecode("AAB", out _));
	}
}
