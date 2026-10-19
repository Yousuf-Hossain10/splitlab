using Splitlab.Domain.Events;

namespace Splitlab.Domain.Tests;

public class EventRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static string? Try(
        string? eventId = "evt-1",
        string? type = "exposure",
        string? experiment = "checkout",
        string? variation = "control",
        string? name = null,
        double? value = null,
        string? user = "user-1",
        DateTimeOffset? timestamp = null,
        bool useDefaultTimestamp = true)
    {
        EventRecord.TryCreate(
            Guid.NewGuid(), Guid.NewGuid(), eventId, type, experiment, variation, name, value, user,
            useDefaultTimestamp ? timestamp ?? Now : timestamp, Now, out _, out string? error);
        return error;
    }

    [Fact]
    public void ValidExposureIsAccepted()
    {
        bool ok = EventRecord.TryCreate(
            Guid.NewGuid(), Guid.NewGuid(), "e1", "exposure", "checkout", "control", "ignored", 5, "u1", Now, Now,
            out EventRecord? record, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(EventKind.Exposure, record!.Kind);
        Assert.Equal("checkout", record.ExperimentKey);
        Assert.Null(record.Name); // fields of the other kind are dropped
        Assert.Null(record.Value);
    }

    [Fact]
    public void ValidConversionKeepsNameAndValue()
    {
        EventRecord.TryCreate(
            Guid.NewGuid(), Guid.NewGuid(), "e1", "conversion", "ignored", "ignored", "purchase", 19.99, "u1", Now, Now,
            out EventRecord? record, out _);

        Assert.Equal(EventKind.Conversion, record!.Kind);
        Assert.Equal("purchase", record.Name);
        Assert.Equal(19.99, record.Value);
        Assert.Null(record.ExperimentKey);
    }

    [Fact]
    public void TimestampIsNormalisedToUtcMilliseconds()
    {
        var local = new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234);

        EventRecord.TryCreate(
            Guid.NewGuid(), Guid.NewGuid(), "e1", "exposure", "x", "y", null, null, "u", local, Now, out EventRecord? record, out _);

        Assert.Equal(TimeSpan.Zero, record!.OccurredAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), record.OccurredAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EventIdIsRequired(string? id) => Assert.NotNull(Try(eventId: id));

    [Fact]
    public void FieldLengthsAreCapped()
    {
        Assert.NotNull(Try(eventId: new string('a', 101)));
        Assert.NotNull(Try(user: new string('a', 201)));
        Assert.Null(Try(eventId: new string('a', 100), user: new string('a', 200)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("click")]
    [InlineData("Exposure")]
    public void TypeMustBeKnown(string? type) => Assert.NotNull(Try(type: type));

    [Fact]
    public void ExposureNeedsValidKeys()
    {
        Assert.NotNull(Try(experiment: null));
        Assert.NotNull(Try(variation: ""));
        Assert.NotNull(Try(experiment: "Has Space"));
    }

    [Fact]
    public void ConversionNeedsAMetricNameAndAFiniteValue()
    {
        Assert.NotNull(Try(type: "conversion", name: null));
        Assert.NotNull(Try(type: "conversion", name: "purchase", value: double.NaN));
        Assert.NotNull(Try(type: "conversion", name: "purchase", value: double.PositiveInfinity));
        Assert.Null(Try(type: "conversion", name: "purchase", value: -3));
    }

    [Fact]
    public void TimestampMustBePresentAndWithinTheAllowedWindow()
    {
        Assert.NotNull(Try(timestamp: null, useDefaultTimestamp: false));
        Assert.NotNull(Try(timestamp: Now - EventRecord.MaxAge - TimeSpan.FromSeconds(1)));
        Assert.Null(Try(timestamp: Now - EventRecord.MaxAge + TimeSpan.FromSeconds(1)));
        Assert.NotNull(Try(timestamp: Now + EventRecord.MaxClockSkew + TimeSpan.FromSeconds(1)));
        Assert.Null(Try(timestamp: Now + EventRecord.MaxClockSkew - TimeSpan.FromSeconds(1)));
    }
}
