namespace VNotch.Services;

/// <summary>
/// Marks an animation failure that has been contained locally and is safe for
/// the dispatcher exception handler to recover from.
/// </summary>
public sealed class RecoverableAnimationException : Exception
{
    public RecoverableAnimationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Marks a media integration failure that has been contained locally and is
/// safe for the dispatcher exception handler to recover from.
/// </summary>
public sealed class RecoverableMediaException : Exception
{
    public RecoverableMediaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
