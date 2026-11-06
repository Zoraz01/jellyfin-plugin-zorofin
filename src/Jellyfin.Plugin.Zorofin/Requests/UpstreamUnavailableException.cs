using System;

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>Raised when a configured upstream service cannot answer.</summary>
public class UpstreamUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpstreamUnavailableException"/> class.
    /// </summary>
    /// <param name="status">Upstream status, or 0 when unreachable.</param>
    public UpstreamUnavailableException(int status)
        : base("Upstream service unavailable (" + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").")
    {
        Status = status;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpstreamUnavailableException"/> class.
    /// </summary>
    public UpstreamUnavailableException()
        : this(0)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpstreamUnavailableException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Inner exception.</param>
    public UpstreamUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the upstream status, or 0 when unreachable.</summary>
    public int Status { get; }
}
