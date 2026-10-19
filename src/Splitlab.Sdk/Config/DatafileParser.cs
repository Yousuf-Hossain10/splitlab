using System.Text.Json;
using Splitlab.Sdk.Bucketing;

namespace Splitlab.Sdk.Config;

/// <summary>
/// Parses the JSON datafile served by <c>GET /v1/config/{environmentKey}</c> (ADR-0007).
/// The format is a public contract, so it carries a <c>schemaVersion</c> and unknown versions are rejected.
/// </summary>
/// <remarks>
/// Shape: <c>{ "schemaVersion": 1, "version": "42", "experiments": [ { "key": "checkout_flow",
/// "variations": [ { "key": "control", "trafficBasisPoints": 5000 } ] } ] }</c>
/// </remarks>
public static class DatafileParser
{
    /// <summary>The datafile schema version this SDK understands.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Builds a snapshot from datafile JSON.</summary>
    /// <exception cref="DatafileException">The JSON is malformed, an unsupported version, or describes an invalid experiment.</exception>
    public static ConfigSnapshot Parse(string json, string? etag = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
            if (schemaVersion != SupportedSchemaVersion)
            {
                throw new DatafileException(
                    $"Unsupported datafile schemaVersion {schemaVersion}; this SDK supports {SupportedSchemaVersion}.");
            }

            string version = root.GetProperty("version").GetString()
                ?? throw new DatafileException("Datafile 'version' must be a string.");

            var experiments = new List<Experiment>();
            foreach (JsonElement item in root.GetProperty("experiments").EnumerateArray())
            {
                var variations = new List<Variation>();
                foreach (JsonElement v in item.GetProperty("variations").EnumerateArray())
                {
                    variations.Add(new Variation(
                        v.GetProperty("key").GetString()!,
                        v.GetProperty("trafficBasisPoints").GetInt32()));
                }

                experiments.Add(new Experiment(item.GetProperty("key").GetString()!, variations));
            }

            return new ConfigSnapshot(version, etag, experiments);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                       or FormatException or ArgumentException)
        {
            throw new DatafileException("The datafile is not valid: " + ex.Message, ex);
        }
    }
}
