using System.Globalization;
using Services.Sync.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Services.Sync.Contracts;

/// <summary>Opt-in v1 wire contract; the legacy hub is not changed by this stage.</summary>
public static class SyncProtocol
{
    public const int Version = 1;
    public const int MaxTextUtf8Bytes = 8192;
    public const int MaxSignedPackageBytes = 24576;
    public const int MaxHubMessageBytes = 32768;
    public const int MaxPageItems = 100;
    public const int MaxPositionBatchItems = 25;
    public const int MaxBoundaryPageItems = 25;
    public const int MaxPageBytes = 1048576;
    public const int MaxReceiptBatchItems = 50;
    public const int MaxPriorityChats = 10;
    public const int MaxCursorBytes = 2048;
    public const int MaxClockSkewMs = 60000;

    // Recovery KDF (stage 06). docs/sync-contract-v1.md pins the algorithm/output/salt and the
    // candidate resource parameters ("memory=65536KiB, iterations=3, parallelism=1 require phone
    // measurement") and requires resource parameters to be "bound before derivation" - the Min/Max
    // values below are exactly that bound, shared by the client (which refuses to derive outside them)
    // and the server (which refuses to store outside them).
    public const int RecoveryKdfSaltBytes = 16;
    public const int RecoveryKdfOutputBytes = 32;
    public const int RecoveryKdfVersion = 19;
    public const int RecoveryKdfCandidateMemoryKiB = 65536;
    public const int RecoveryKdfCandidateIterations = 3;
    public const int RecoveryKdfCandidateParallelism = 1;
    public const int RecoveryKdfMinMemoryKiB = 8192;
    public const int RecoveryKdfMaxMemoryKiB = 1048576;
    public const int RecoveryKdfMinIterations = 1;
    public const int RecoveryKdfMaxIterations = 10;
    public const int RecoveryKdfMinParallelism = 1;
    public const int RecoveryKdfMaxParallelism = 4;

    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            NumberHandling = JsonNumberHandling.Strict
        };
        options.Converters.Add(new StrictStringEnumConverter());
        return options;
    }

    /// <summary>Sequences and versions are nonnegative Int64 decimal strings on the wire.</summary>
    public static bool IsSequence(string? value) =>
        value is not null && value.Length is > 0 and <= 19 &&
        (value.Length == 1 || value[0] != '0') &&
        value.All(c => c is >= '0' and <= '9') &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
