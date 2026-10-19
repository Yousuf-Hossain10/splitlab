using System.Text.RegularExpressions;

namespace Splitlab.Domain;

/// <summary>A business rule was violated.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Shared validation for identifiers that appear in URLs and datafiles.</summary>
public static partial class Keys
{
    public const int MaxLength = 64;

    /// <summary>Returns <paramref name="value"/> if it is a valid key: lowercase letters, digits, '_' and '-', starting with a letter or digit.</summary>
    public static string Validate(string? value, string what)
    {
        if (value is null || !KeyPattern().IsMatch(value))
        {
            throw new DomainException(
                $"{what} must be 1-{MaxLength} characters: lowercase letters, digits, '_' or '-', starting with a letter or digit.");
        }

        return value;
    }

    public static string RequireName(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            throw new DomainException($"{what} is required and must be at most 200 characters.");
        }

        return value.Trim();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex KeyPattern();
}
