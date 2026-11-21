using System.Security.Cryptography;
using System.Text.Json;
using Services.Crypto.Interface;
using Services.Crypto.Models;
using Services.Sync.Contracts;
using static Services.Crypto.StrictJsonShape;

namespace Services.Crypto.Service;

public sealed class MessagePackageVerifier : IMessagePackageVerifier
{
	private static readonly ObjectShape DeviceContextShape = new(new Dictionary<string, Field>
	{
		["accountContextId"] = new StringField(),
		["deviceRegistrationId"] = new StringField(),
		["localInstanceId"] = new StringField(),
	});

	private static readonly ObjectShape OperationIdentityShape = new(new Dictionary<string, Field>
	{
		["device"] = new ObjectField(DeviceContextShape),
		["clientOperationId"] = new StringField(),
	});

	private static readonly ObjectShape AuthorKeyReferenceShape = new(new Dictionary<string, Field>
	{
		["accountContextId"] = new StringField(),
		["chatId"] = new StringField(),
		["authorKeyId"] = new StringField(),
		["authorKeyVersion"] = new StringField(),
	});

	private static readonly ObjectShape MessageBindingShape = new(new Dictionary<string, Field>
	{
		["protocolVersion"] = new NumberField(),
		["operation"] = new ObjectField(OperationIdentityShape),
		["chatId"] = new StringField(),
		["authorKey"] = new ObjectField(AuthorKeyReferenceShape),
		["operationKind"] = new StringField(),
	});

	private static readonly ObjectShape EncryptedContentShape = new(new Dictionary<string, Field>
	{
		["nonce"] = new StringField(),
		["ciphertext"] = new StringField(),
		["tag"] = new StringField(),
	});

	private static readonly ObjectShape SignedMessagePayloadShape = new(new Dictionary<string, Field>
	{
		["binding"] = new ObjectField(MessageBindingShape),
		["content"] = new ObjectField(EncryptedContentShape),
	});

	private const int NonceLength = 12;
	private const int TagLength = 16;

	public VerifiedMessagePackage Verify(string signedPackage, MessageBinding expectedBinding, byte[] authorPublicKeySpki)
	{
		Models.ParsedJws parsed = CompactJws.Parse(signedPackage);

		if (!string.Equals(parsed.Kid, expectedBinding.AuthorKey.AuthorKeyId, StringComparison.Ordinal))
		{
			throw new MessageBindingMismatchException("Protected header 'kid' does not match the expected author key id.");
		}

		VerifySignature(parsed, authorPublicKeySpki);

		StrictJsonShape.Validate(parsed.PayloadBytes, SignedMessagePayloadShape);

		SignedMessagePayload payload = JsonSerializer.Deserialize<SignedMessagePayload>(parsed.PayloadBytes, SyncProtocol.CreateJsonOptions())
			?? throw new JwsFormatException("Signed payload deserialized to null.");

		// Defense in depth, parity with ChatAppClient/src/Crypto/MessagePackage.ts's `verify`
		// (review-report.md follow-up, item 5): `expectedBinding` is caller-supplied, not
		// necessarily built via a path that already validated these - reuse the one shared
		// id-token validator (CompactJws.IsRestrictedAsciiId, already used for `kid`) rather than
		// duplicating the [A-Za-z0-9_-]{1,128} rule here.
		RequireIdentityTokens(payload.Binding);

		ValidatePayloadInvariants(payload);
		ValidateAgainstExpected(payload.Binding, expectedBinding);

		if (!Base64Url.TryDecode(payload.Content.Nonce, out byte[] nonce) || nonce.Length != NonceLength)
		{
			throw new JwsFormatException($"EncryptedContent.nonce must decode to exactly {NonceLength} bytes.");
		}
		if (!Base64Url.TryDecode(payload.Content.Tag, out byte[] tag) || tag.Length != TagLength)
		{
			throw new JwsFormatException($"EncryptedContent.tag must decode to exactly {TagLength} bytes.");
		}
		if (!Base64Url.TryDecode(payload.Content.Ciphertext, out byte[] ciphertext))
		{
			throw new JwsFormatException("EncryptedContent.ciphertext is not canonical base64url.");
		}
		// AES-256-GCM of <=8192 UTF-8 bytes never shrinks; reject absurd sizes defensively (actual
		// MaxSignedPackageBytes enforcement happens at the transport layer on the whole package).
		if (ciphertext.Length > SyncProtocol.MaxTextUtf8Bytes)
		{
			throw new JwsFormatException("EncryptedContent.ciphertext exceeds the maximum plaintext size.");
		}

		return new VerifiedMessagePackage(payload.Binding, payload.Content, nonce, ciphertext, tag, ComputeDigest(signedPackage));
	}

	private static void VerifySignature(Models.ParsedJws parsed, byte[] authorPublicKeySpki)
	{
		using ECDsa ecdsa = ECDsa.Create();
		try
		{
			ecdsa.ImportSubjectPublicKeyInfo(authorPublicKeySpki, out _);
		}
		catch (Exception ex) when (ex is CryptographicException or ArgumentException)
		{
			throw new JwsFormatException("Author public key is not a valid DER SubjectPublicKeyInfo for P-256.");
		}

		// Compare the OID value, not FriendlyName: .NET's own curve-name resolution for an imported
		// key reports "ECDSA_P256" rather than ECCurve.NamedCurves.nistP256's "nistP256" friendly
		// name, even though both are the exact same curve (OID 1.2.840.10045.3.1.7).
		const string NistP256Oid = "1.2.840.10045.3.1.7";
		if (ecdsa.KeySize != 256 || !string.Equals(ecdsa.ExportParameters(false).Curve.Oid.Value, NistP256Oid, StringComparison.Ordinal))
		{
			throw new JwsFormatException("Author public key is not a P-256 key.");
		}

		byte[] signingInput = CompactJws.SigningInputBytes(parsed.SigningInputAscii);
		bool verified = ecdsa.VerifyData(signingInput, parsed.SignatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		if (!verified)
		{
			throw new JwsSignatureException("ES256 signature verification failed.");
		}
	}

	private static void RequireIdentityTokens(MessageBinding binding)
	{
		RequireRestrictedAsciiId(binding.Operation.Device.AccountContextId, "operation.device.accountContextId");
		RequireRestrictedAsciiId(binding.Operation.Device.DeviceRegistrationId, "operation.device.deviceRegistrationId");
		RequireRestrictedAsciiId(binding.Operation.Device.LocalInstanceId, "operation.device.localInstanceId");
		RequireRestrictedAsciiId(binding.Operation.ClientOperationId, "operation.clientOperationId");
		RequireRestrictedAsciiId(binding.ChatId, "chatId");
		RequireRestrictedAsciiId(binding.AuthorKey.AccountContextId, "authorKey.accountContextId");
		RequireRestrictedAsciiId(binding.AuthorKey.ChatId, "authorKey.chatId");
		RequireRestrictedAsciiId(binding.AuthorKey.AuthorKeyId, "authorKey.authorKeyId");
		// docs/sync-contract-v1.md: "Sequences, versions and counts are canonical decimal nonnegative
		// Int64 strings" - AuthorKeyVersion is a *version*, not exempted by the AAD section's separate
		// "restricted ASCII tokens" wording (canonical decimal is a subset of restricted ASCII, not a
		// conflicting rule). Mirrors ChatAppClient/src/Crypto/MessagePackage.ts's `requireSequenceVersion`
		// exactly, reusing the one shared SyncProtocol.IsSequence predicate (already used for
		// RightsVersion/sequence fields elsewhere), not a new validator.
		if (!SyncProtocol.IsSequence(binding.AuthorKey.AuthorKeyVersion))
		{
			throw new JwsFormatException("authorKey.authorKeyVersion must be a canonical decimal Int64 string.");
		}
	}

	private static void RequireRestrictedAsciiId(string value, string fieldName)
	{
		if (!CompactJws.IsRestrictedAsciiId(value))
		{
			throw new JwsFormatException($"{fieldName} must be a restricted ASCII id token.");
		}
	}

	private static void ValidatePayloadInvariants(SignedMessagePayload payload)
	{
		if (payload.Binding.ProtocolVersion != SyncProtocol.Version)
		{
			throw new MessageBindingMismatchException($"Unsupported protocolVersion {payload.Binding.ProtocolVersion}.");
		}
		if (payload.Binding.OperationKind != OperationKind.SendText)
		{
			throw new MessageBindingMismatchException("Unsupported operationKind; v1 only supports SendText.");
		}
		// "AuthorKey account/chat must equal the operation account/binding chat."
		if (!string.Equals(payload.Binding.AuthorKey.AccountContextId, payload.Binding.Operation.Device.AccountContextId, StringComparison.Ordinal))
		{
			throw new MessageBindingMismatchException("authorKey.accountContextId must equal operation.device.accountContextId.");
		}
		if (!string.Equals(payload.Binding.AuthorKey.ChatId, payload.Binding.ChatId, StringComparison.Ordinal))
		{
			throw new MessageBindingMismatchException("authorKey.chatId must equal binding.chatId.");
		}
	}

	private static void ValidateAgainstExpected(MessageBinding actual, MessageBinding expected)
	{
		if (!actual.Equals(expected))
		{
			throw new MessageBindingMismatchException("Signed package binding does not match the expected operation/chat/author-key context.");
		}
	}

	public string ComputeDigest(string signedPackage)
	{
		byte[] asciiBytes = System.Text.Encoding.ASCII.GetBytes(signedPackage);
		byte[] hash = SHA256.HashData(asciiBytes);
		return Base64Url.Encode(hash);
	}
}
