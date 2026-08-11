using SleipnirCommon.Models;

namespace SleipnirCommon.Exceptions;

/// <summary>
/// Unified exception for all Sleipnir transport and invocation errors.
/// </summary>
public class SleipnirException : Exception
{
    /// <summary>
    /// Structured error details, if available.
    /// </summary>
    public SleipnirError? Error { get; }

    public SleipnirException(Exception innerException)
        : base(innerException.Message, innerException)
    {
    }

    public SleipnirException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public SleipnirException(SleipnirError error, Exception? innerException = null)
        : base(error.Message, innerException)
    {
        Error = error;
    }
}
