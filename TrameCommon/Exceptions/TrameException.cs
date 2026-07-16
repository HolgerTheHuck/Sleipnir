using TrameCommon.Models;

namespace TrameCommon.Exceptions;

/// <summary>
/// Unified exception for all Trame transport and invocation errors.
/// </summary>
public class TrameException : Exception
{
    /// <summary>
    /// Structured error details, if available.
    /// </summary>
    public TrameError? Error { get; }

    public TrameException(Exception innerException)
        : base(innerException.Message, innerException)
    {
    }

    public TrameException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public TrameException(TrameError error, Exception? innerException = null)
        : base(error.Message, innerException)
    {
        Error = error;
    }
}
