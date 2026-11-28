using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Services.Sync.Contracts;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

if (args.Length != 2) throw new ArgumentException("Usage: SyncContracts <node-fixture.json> <dotnet-result.json>");
var options = SyncProtocol.CreateJsonOptions();
foreach (var value in new[] { "0", "1", "9007199254740993", "9223372036854775807" })
    Check(SyncProtocol.IsSequence(value), "Valid sequence rejected");
foreach (var value in new[] { "", "01", "-1", "1.0", "1e3", "9223372036854775808" })
    Check(!SyncProtocol.IsSequence(value), "Invalid sequence accepted");

var operation = new OperationIdentity(new("account", "device", "instance"), "operation");
OperationResult[] outcomes = [
    new AcceptedOperation(operation, new(operation, "digest", "9007199254740993", "chat", "2", "3", "2026-10-25T00:00:00.000Z", "1")),
    new UnknownOperation(operation),
    new RetryableOperation(operation, OperationErrorCode.Unavailable, 1000),
    new RejectedOperation(operation, OperationErrorCode.OperationConflict)
];
foreach (var result in outcomes)
{
    var json = JsonSerializer.Serialize(result, options);
    Check(JsonSerializer.Deserialize<OperationResult>(json, options) == result, "Outcome round trip failed");
}
var contractTypes = typeof(DeviceContext).Assembly.GetTypes().Where(t => t.Namespace == typeof(DeviceContext).Namespace).ToArray();
foreach (var enumType in contractTypes.Where(t => t.IsEnum))
{
    foreach (var name in Enum.GetNames(enumType))
    {
        var wire = JsonSerializer.Serialize(name);
        Check(JsonSerializer.Serialize(JsonSerializer.Deserialize(wire, enumType, options), enumType, options) == wire, "Enum round trip changed name");
        foreach (var invalid in new[] { name.ToLowerInvariant(), " " + name, name + " ", name + ", " + name })
        {
            try
            {
                JsonSerializer.Deserialize(JsonSerializer.Serialize(invalid), enumType, options);
                throw new InvalidOperationException($"Invalid {enumType.Name} accepted: {invalid}");
            }
            catch (JsonException) { }
        }
    }
    foreach (var invalid in new[] { "1", "null", "\"1\"", "\"UnknownValue\"" })
    {
        try
        {
            JsonSerializer.Deserialize(invalid, enumType, options);
            throw new InvalidOperationException($"Invalid {enumType.Name} accepted: {invalid}");
        }
        catch (JsonException) { }
    }
}
Check(JsonSerializer.Deserialize<DeviceContext>("{\"accountContextId\":\"a\",\"deviceRegistrationId\":\"d\",\"localInstanceId\":\"i\",\"futureField\":true}", options) == new DeviceContext("a", "d", "i"), "Additive field rejected");

using var fixture = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = fixture.RootElement;
byte[] Bytes(string property) => Convert.FromBase64String(root.GetProperty(property).GetString()!);
var incomingOutcomes = JsonSerializer.Deserialize<OperationResult[]>(root.GetProperty("outcomes"), options)!;
Check(incomingOutcomes.SequenceEqual(outcomes), "TypeScript outcome fixtures differ");
var positionBatches = JsonSerializer.Deserialize<SyncPositionBatch[]>(root.GetProperty("positionBatches"), options)!;
Check(positionBatches.Length > 1 && positionBatches.Sum(b => b.Positions.Count) == 301, "Missing resume positions");
for (var index = 0; index < positionBatches.Length; index++)
{
    var batch = positionBatches[index];
    Check(batch.BatchIndex == index && batch.Final == (index == positionBatches.Length - 1), "Invalid ordered exchange completion");
    Check(batch.Positions.Count <= SyncProtocol.MaxPositionBatchItems, "Position batch exceeds count limit");
}
var boundaryPages = JsonSerializer.Deserialize<SyncBoundaryPage[]>(root.GetProperty("boundaryPages"), options)!;
Check(boundaryPages.Length > 1 && boundaryPages.Sum(p => p.Boundaries.Count) == 301, "Missing fixed boundaries");
Check(boundaryPages.All(p => p.Complete == (p.NextCursor is null)), "Boundary continuation mismatch");

var recovery = root.GetProperty("recovery");
var kdf = recovery.GetProperty("kdf");
var recoveryAad = JsonSerializer.SerializeToUtf8Bytes(new[] { "chat-recovery-v1", "1",
    recovery.GetProperty("accountContextId").GetString()!, recovery.GetProperty("version").GetString()!,
    "argon2id", "19", kdf.GetProperty("salt").GetString()!,
    kdf.GetProperty("memoryKiB").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
    kdf.GetProperty("iterations").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
    kdf.GetProperty("parallelism").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) });
Check(recoveryAad.SequenceEqual(Bytes("recoveryAad")), "Recovery AAD bytes differ");
var recoveryPlaintext = new byte[Bytes("recoveryCiphertext").Length];
using (var recoveryAes = new AesGcm(Bytes("recoveryKey"), 16))
{
    recoveryAes.Decrypt(Bytes("recoveryNonce"), Bytes("recoveryCiphertext"), Bytes("recoveryTag"), recoveryPlaintext, recoveryAad);
    Check(Encoding.UTF8.GetString(recoveryPlaintext) == "recovery fixture", "Recovery cross-runtime decrypt failed");
    var changedAad = recoveryAad.ToArray(); changedAad[2] ^= 1;
    try
    {
        recoveryAes.Decrypt(Bytes("recoveryNonce"), Bytes("recoveryCiphertext"), Bytes("recoveryTag"), recoveryPlaintext, changedAad);
        throw new InvalidOperationException("Changed recovery binding accepted");
    }
    catch (CryptographicException) { }
}

var plaintext = new byte[Bytes("ciphertext").Length];
using var aes = new AesGcm(Bytes("key"), 16);
aes.Decrypt(Bytes("nonce"), Bytes("ciphertext"), Bytes("tag"), plaintext, Bytes("aad"));
Check(Encoding.UTF8.GetString(plaintext) == root.GetProperty("plaintext").GetString(), "AES-GCM cross-runtime decrypt failed");
var ciphertext = new byte[plaintext.Length];
var tag = new byte[16];
aes.Encrypt(Bytes("nonce"), plaintext, ciphertext, tag, Bytes("aad"));
Check(ciphertext.SequenceEqual(Bytes("ciphertext")) && tag.SequenceEqual(Bytes("tag")), "AES-GCM bytes differ");
var badTag = Bytes("tag"); badTag[0] ^= 1;
try
{
    aes.Decrypt(Bytes("nonce"), ciphertext, badTag, plaintext, Bytes("aad"));
    throw new InvalidOperationException("Tampered tag accepted");
}
catch (CryptographicException) { }

using var author = ECDsa.Create();
author.ImportSubjectPublicKeyInfo(Bytes("publicKey"), out _);
Check(author.VerifyData(Bytes("signingInput"), Bytes("signature"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "Node ES256 signature rejected");
var tampered = Bytes("signingInput"); tampered[0] ^= 1;
Check(!author.VerifyData(tampered, Bytes("signature"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "Tampered JWS accepted");
author.ImportPkcs8PrivateKey(Bytes("privateKey"), out _);
var signature = author.SignData(Bytes("signingInput"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
Check(signature.Length == 64, "ES256 must use R || S, not DER");

var types = contractTypes;
var records = types.Where(t => t.IsClass && !t.IsAbstract && t != typeof(SyncProtocol)).ToDictionary(
    t => t.Name,
    t => t.GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order().ToArray());
var nullability = new NullabilityInfoContext();
string TypeScriptType(Type type)
{
    var nullableType = Nullable.GetUnderlyingType(type);
    if (nullableType is not null) return TypeScriptType(nullableType) + " | null";
    if (type == typeof(string)) return "string";
    if (type == typeof(int)) return "number";
    if (type == typeof(bool)) return "boolean";
    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        return "ReadonlyArray<" + TypeScriptType(type.GetGenericArguments()[0]) + ">";
    return type.Name;
}
var recordTypes = types.Where(t => t.IsClass && !t.IsAbstract && t != typeof(SyncProtocol)).ToDictionary(
    t => t.Name,
    t => t.GetProperties().ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p =>
        TypeScriptType(p.PropertyType) + (!p.PropertyType.IsValueType && nullability.Create(p).ReadState == NullabilityState.Nullable ? " | null" : "")));
var enums = types.Where(t => t.IsEnum).ToDictionary(t => t.Name, Enum.GetNames);
var constants = typeof(SyncProtocol).GetFields(BindingFlags.Public | BindingFlags.Static)
    .ToDictionary(f => JsonNamingPolicy.CamelCase.ConvertName(f.Name), f => f.GetRawConstantValue());
File.WriteAllText(args[1], JsonSerializer.Serialize(new { signature = Convert.ToBase64String(signature), records, recordTypes, enums, constants, outcomes, positionBatches, boundaryPages, recoveryAad = Convert.ToBase64String(recoveryAad) }, options));
Console.WriteLine(".NET 8: wire outcomes, paged sync, strict enums, additive fields, message/recovery AES-GCM and ES256 passed.");
