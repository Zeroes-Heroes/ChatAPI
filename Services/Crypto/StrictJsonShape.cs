using System.Text.Json;

namespace Services.Crypto;

/// <summary>
/// Validates that a JSON object's shape is exactly a declared set of required fields - no unknown
/// fields, no duplicate fields, no missing fields, at every nesting level - before anything is
/// deserialized. Used for the signed message payload: docs/sync-contract-v1.md requires rejecting
/// "duplicate or unknown ... signed-payload fields", which <c>JsonUnmappedMemberHandling.Skip</c>
/// (the project-wide outer-DTO forward-compatibility default in SyncProtocol.CreateJsonOptions)
/// deliberately does not do, and which ordinary deserialization does not detect for duplicates
/// either (the last occurrence silently wins). Only object/string shapes are needed for the message
/// payload today; this is intentionally not a general JSON schema validator.
/// </summary>
internal static class StrictJsonShape
{
	internal abstract record Field;
	internal sealed record StringField : Field;
	internal sealed record NumberField : Field;
	internal sealed record ObjectField(ObjectShape Shape) : Field;
	internal sealed record ObjectShape(IReadOnlyDictionary<string, Field> Fields);

	public static void Validate(ReadOnlySpan<byte> json, ObjectShape rootShape)
	{
		try
		{
			var readerOptions = new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };
			var reader = new Utf8JsonReader(json, readerOptions);

			if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
			{
				throw new JwsFormatException("Signed payload must be a JSON object.");
			}

			ValidateObjectBody(ref reader, rootShape, "$");

			if (reader.Read())
			{
				throw new JwsFormatException("Signed payload has trailing content after the JSON object.");
			}
		}
		catch (JsonException ex)
		{
			throw new JwsFormatException($"Malformed signed payload: {ex.Message}");
		}
	}

	private static void ValidateObjectBody(ref Utf8JsonReader reader, ObjectShape shape, string path)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);

		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				throw new JwsFormatException($"Malformed signed payload at {path}.");
			}

			string key = reader.GetString() ?? throw new JwsFormatException($"Malformed signed payload at {path}.");
			if (!seen.Add(key))
			{
				throw new JwsFormatException($"Duplicate signed payload field '{path}.{key}'.");
			}

			if (!shape.Fields.TryGetValue(key, out Field? field))
			{
				throw new JwsFormatException($"Unknown signed payload field '{path}.{key}'.");
			}

			if (!reader.Read())
			{
				throw new JwsFormatException($"Malformed signed payload at {path}.{key}.");
			}

			switch (field)
			{
				case StringField:
					if (reader.TokenType != JsonTokenType.String)
					{
						throw new JwsFormatException($"Signed payload field '{path}.{key}' must be a string.");
					}
					break;
				case NumberField:
					if (reader.TokenType != JsonTokenType.Number)
					{
						throw new JwsFormatException($"Signed payload field '{path}.{key}' must be a number.");
					}
					break;
				case ObjectField obj:
					if (reader.TokenType != JsonTokenType.StartObject)
					{
						throw new JwsFormatException($"Signed payload field '{path}.{key}' must be an object.");
					}
					ValidateObjectBody(ref reader, obj.Shape, $"{path}.{key}");
					break;
			}
		}

		if (reader.TokenType != JsonTokenType.EndObject)
		{
			throw new JwsFormatException($"Malformed signed payload at {path}.");
		}

		if (seen.Count != shape.Fields.Count)
		{
			string missing = string.Join(", ", shape.Fields.Keys.Except(seen, StringComparer.Ordinal));
			throw new JwsFormatException($"Signed payload object at {path} is missing required field(s): {missing}.");
		}
	}
}
