namespace Splitlab.Sdk.Config;

/// <summary>The datafile could not be parsed or described an invalid configuration.</summary>
public sealed class DatafileException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public DatafileException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    public DatafileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
