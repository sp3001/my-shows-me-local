namespace Shows.Infrastructure.Tvmaze;

/// <summary>
/// Raised when TVmaze could not be reached or answered with an error that is not "not found". SPEC R14:
/// an unreachable host must surface as a failed task naming the host, never as an empty result — an
/// empty list would report success and leave the database quietly incomplete.
/// </summary>
public sealed class TvmazeUnavailableException : Exception
{
    public TvmazeUnavailableException()
        : base("TVmaze is unavailable.")
    {
    }

    public TvmazeUnavailableException(string message)
        : base(message)
    {
    }

    public TvmazeUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
