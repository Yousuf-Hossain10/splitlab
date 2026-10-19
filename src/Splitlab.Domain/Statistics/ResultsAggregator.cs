namespace Splitlab.Domain.Statistics;

/// <param name="Counts">Per configured variation, in the order given: unique exposed users and how many converted.</param>
/// <param name="ExcludedUsers">Users exposed to more than one variation. They are left out of every count (ADR-0006).</param>
/// <param name="UnknownVariationUsers">Users whose only exposure was to a variation key the experiment does not define.</param>
public sealed record AggregatedCounts(
    IReadOnlyList<(string Key, long Exposed, long Converted)> Counts,
    long ExcludedUsers,
    long UnknownVariationUsers);

/// <summary>
/// Turns raw exposure and conversion events into per-variation user counts. Order-independent, so events can be
/// fed as they come off the database.
/// <list type="bullet">
/// <item>A user is counted once, in the variation they were exposed to.</item>
/// <item>A user exposed to several variations is excluded entirely.</item>
/// <item>A conversion only counts if it happened at or after the user's first exposure.</item>
/// </list>
/// </summary>
public sealed class ResultsAggregator(IReadOnlyList<string> variationKeys)
{
    private readonly Dictionary<string, UserState> _users = new(StringComparer.Ordinal);

    public void AddExposure(string userId, string variationKey, DateTimeOffset at)
    {
        UserState user = Get(userId);
        if (user.Variation is null)
        {
            user.Variation = variationKey;
            user.FirstExposure = at;
        }
        else if (string.Equals(user.Variation, variationKey, StringComparison.Ordinal))
        {
            if (at < user.FirstExposure)
            {
                user.FirstExposure = at;
            }
        }
        else
        {
            user.Conflicted = true;
        }
    }

    public void AddConversion(string userId, DateTimeOffset at)
    {
        UserState user = Get(userId);
        if (user.LastConversion is null || at > user.LastConversion)
        {
            user.LastConversion = at;
        }
    }

    public AggregatedCounts Complete()
    {
        var exposed = variationKeys.ToDictionary(k => k, _ => 0L, StringComparer.Ordinal);
        var converted = variationKeys.ToDictionary(k => k, _ => 0L, StringComparer.Ordinal);
        long excluded = 0;
        long unknown = 0;

        foreach (UserState user in _users.Values)
        {
            if (user.Variation is null)
            {
                continue; // only conversions, never exposed
            }

            if (user.Conflicted)
            {
                excluded++;
            }
            else if (!exposed.TryGetValue(user.Variation, out long count))
            {
                unknown++;
            }
            else
            {
                exposed[user.Variation] = count + 1;
                if (user.LastConversion >= user.FirstExposure)
                {
                    converted[user.Variation]++;
                }
            }
        }

        return new AggregatedCounts(
            variationKeys.Select(k => (k, exposed[k], converted[k])).ToList(), excluded, unknown);
    }

    private UserState Get(string userId)
    {
        if (!_users.TryGetValue(userId, out UserState? user))
        {
            user = new UserState();
            _users[userId] = user;
        }

        return user;
    }

    private sealed class UserState
    {
        public string? Variation { get; set; }

        public DateTimeOffset FirstExposure { get; set; }

        public bool Conflicted { get; set; }

        public DateTimeOffset? LastConversion { get; set; }
    }
}
