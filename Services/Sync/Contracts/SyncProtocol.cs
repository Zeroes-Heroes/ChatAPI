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
