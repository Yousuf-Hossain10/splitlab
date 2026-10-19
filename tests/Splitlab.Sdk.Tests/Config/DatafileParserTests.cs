using Splitlab.Sdk.Config;

namespace Splitlab.Sdk.Tests.Config;

public class DatafileParserTests
{
    internal const string Valid = """
        { "schemaVersion": 1, "version": "7",
          "experiments": [
            { "key": "checkout_flow",
              "variations": [ { "key": "control", "trafficBasisPoints": 5000 },
                              { "key": "treatment", "trafficBasisPoints": 5000 } ] } ] }
        """;

    [Fact]
    public void ParsesExperimentsAndVersion()
    {
        ConfigSnapshot snapshot = DatafileParser.Parse(Valid, "\"abc\"");

        Assert.Equal("7", snapshot.Version);
        Assert.Equal("\"abc\"", snapshot.ETag);
        Assert.Equal(1, snapshot.ExperimentCount);
        Assert.True(snapshot.TryGetExperiment("checkout_flow", out var experiment));
        Assert.Equal(2, experiment.Variations.Count);
    }

    [Fact]
    public void UnknownSchemaVersionIsRejected()
    {
        string json = Valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        Assert.Throws<DatafileException>(() => DatafileParser.Parse(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "schemaVersion": 1, "version": "1", "experiments": [ { "key": "x", "variations": [] } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "1", "experiments": [ { "key": "x", "variations": [ { "key": "a", "trafficBasisPoints": 20000 } ] } ] }""")]
    public void MalformedOrInvalidDatafilesAreRejected(string json)
    {
        Assert.Throws<DatafileException>(() => DatafileParser.Parse(json));
    }
}
