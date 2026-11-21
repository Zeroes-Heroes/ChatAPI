using System.Security.Cryptography;
using System.Text;
using Services.Crypto;
using Services.Crypto.Models;
using Xunit;

namespace CryptoTests;

public class CompactJwsTests
{
	[Fact]
	public void Parse_ValidPackage_ReturnsExpectedShape()
	{
		(string compact, ECDsa publicKey) = TestJwsBuilder.BuildWithFreshKey("author-key-1", TestJwsBuilder.RawPayload(new { hello = "world" }));
		ParsedJws parsed = CompactJws.Parse(compact);

		Assert.Equal("author-key-1", parsed.Kid);
		Assert.Equal(64, parsed.SignatureBytes.Length);
		Assert.Equal(compact.Split('.')[0] + "." + compact.Split('.')[1], parsed.SigningInputAscii);
		publicKey.Dispose();
	}

	[Theory]
	[InlineData("onlyone")]
	[InlineData("a.b.c.d")]
	[InlineData(".b.c")]
	[InlineData("a..c")]
	[InlineData("a.b.")]
	[InlineData("")]
	public void Parse_RejectsMalformedStructure(string input)
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(input));
	}

	private static string BuildWithHeader(string headerJson)
	{
		string headerSegment = Base64Url.Encode(Encoding.UTF8.GetBytes(headerJson));
		string payloadSegment = Base64Url.Encode(Encoding.UTF8.GetBytes("{}"));
		string signature = Base64Url.Encode(new byte[64]);
		return $"{headerSegment}.{payloadSegment}.{signature}";
	}

	[Fact]
	public void Parse_RejectsWrongAlg()
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(BuildWithHeader("""{"alg":"HS256","typ":"chat-message+jws","kid":"k1"}""")));
	}

	[Fact]
	public void Parse_RejectsWrongTyp()
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(BuildWithHeader("""{"alg":"ES256","typ":"jwt","kid":"k1"}""")));
	}

	[Fact]
	public void Parse_RejectsUnknownField_Crit()
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(BuildWithHeader("""{"alg":"ES256","typ":"chat-message+jws","kid":"k1","crit":["b64"]}""")));
	}

	[Fact]
	public void Parse_RejectsB64False()
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(BuildWithHeader("""{"alg":"ES256","typ":"chat-message+jws","kid":"k1","b64":false}""")));
	}

	[Fact]
	public void Parse_RejectsDuplicateField()
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(BuildWithHeader("""{"alg":"ES256","alg":"ES256","typ":"chat-message+jws","kid":"k1"}""")));
	}

	[Fact]
	public void Parse_RejectsMissingField()
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(BuildWithHeader("""{"alg":"ES256","typ":"chat-message+jws"}""")));
	}

	[Fact]
	public void Parse_RejectsTrailingContentAfterHeaderObject()
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(BuildWithHeader("""{"alg":"ES256","typ":"chat-message+jws","kid":"k1"}{}""")));
	}

	[Fact]
	public void Parse_RejectsNonRestrictedKid()
	{
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(BuildWithHeader("""{"alg":"ES256","typ":"chat-message+jws","kid":"has space"}""")));
	}

	[Fact]
	public void Parse_RejectsWrongLengthSignature()
	{
		string headerSegment = Base64Url.Encode(Encoding.UTF8.GetBytes("""{"alg":"ES256","typ":"chat-message+jws","kid":"k1"}"""));
		string payloadSegment = Base64Url.Encode(Encoding.UTF8.GetBytes("{}"));
		string shortSignature = Base64Url.Encode(new byte[63]);
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse($"{headerSegment}.{payloadSegment}.{shortSignature}"));
	}

	[Fact]
	public void Parse_RejectsNonCanonicalBase64Segment()
	{
		string payloadSegment = Base64Url.Encode(Encoding.UTF8.GetBytes("{}"));
		string signature = Base64Url.Encode(new byte[64]);
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse($"not-valid-base64url===.{payloadSegment}.{signature}"));
	}

	[Fact]
	public void Parse_RejectsAnOversizedCompactJws_CheaplyBeforeAnyDecodeOrParseWork()
	{
		string oversized = new string('a', Services.Sync.Contracts.SyncProtocol.MaxSignedPackageBytes + 1) + ".b.c";
		Assert.Throws<JwsFormatException>(() => CompactJws.Parse(oversized));
	}
}
