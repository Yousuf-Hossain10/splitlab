using System.Text.Json;
using System.Text.Json.Nodes;

namespace Splitlab.Api.Tests;

/// <summary>
/// The dashboard's TypeScript client is generated from <c>dashboard/openapi/splitlab.v1.json</c> (ADR-0008). This test fails
/// when the API changes without that file being updated, so a contract change is always visible in review.
/// To update the file: <c>UPDATE_OPENAPI=1 dotnet test --filter OpenApiContractTests</c>, then regenerate the client
/// with <c>npm run generate:api</c> in <c>dashboard/</c>.
/// </summary>
public sealed class OpenApiContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string CommittedPath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Splitlab.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir is null
            ? throw new InvalidOperationException("Could not find the repository root (Splitlab.sln).")
            : Path.Combine(dir, "dashboard", "openapi", "splitlab.v1.json");
    }

    private async Task<string> CurrentAsync()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync(OpenApiSetup.DocumentPath);
        response.EnsureSuccessStatusCode();
        JsonNode document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return document.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";
    }

    [Fact]
    public async Task TheCommittedOpenApiDocumentMatchesTheApi()
    {
        string current = await CurrentAsync();
        string path = CommittedPath();

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, current);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing. Run with UPDATE_OPENAPI=1 to create it.");
        string committed = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        Assert.True(
            committed == current,
            "The API's OpenAPI document differs from dashboard/openapi/splitlab.v1.json. Re-run with UPDATE_OPENAPI=1 and regenerate the dashboard client.");
    }

    [Fact]
    public async Task EveryEndpointHasAStableOperationIdAndIsDocumented()
    {
        JsonNode document = JsonNode.Parse(await CurrentAsync())!;
        var operations = document["paths"]!.AsObject()
            .SelectMany(p => p.Value!.AsObject().Select(m => (Path: p.Key, Method: m.Key, Op: m.Value!.AsObject())))
            .ToList();

        Assert.NotEmpty(operations);
        Assert.All(operations, o => Assert.False(string.IsNullOrEmpty(o.Op["operationId"]?.GetValue<string>()), $"{o.Method} {o.Path} has no operationId"));
        Assert.Equal(operations.Count, operations.Select(o => o.Op["operationId"]!.GetValue<string>()).Distinct().Count());
        // The dashboard depends on these being typed, not on inferred shapes.
        Assert.NotNull(document["components"]!["schemas"]!["ResultsResponse"]);
        Assert.NotNull(document["components"]!["schemas"]!["ExperimentResponse"]);
        Assert.NotNull(document["components"]!["schemas"]!["ProblemDetails"]);
    }

    [Fact]
    public async Task EnumsAreDocumentedAsTheStringsTheApiActuallySends()
    {
        JsonNode document = JsonNode.Parse(await CurrentAsync())!;
        JsonNode kind = document["components"]!["schemas"]!["RecommendationKind"]!;

        Assert.Equal("string", kind["type"]!.GetValue<string>());
        Assert.Equal(
            ["Untrustworthy", "KeepCollecting", "Leader", "Inconclusive"],
            kind["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
    }
}
