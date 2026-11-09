namespace NKeeper.Plugins.Packaging;

/// <summary>A verification step failed. Only used inside the verifier; callers get a <see cref="PackageVerification"/>.</summary>
internal sealed class PackageRejectedException : Exception
{
    public PackageRejectedException()
    {
        ReasonCode = ReasonCodes.BadZip;
    }

    public PackageRejectedException(string message)
        : base(message)
    {
        ReasonCode = ReasonCodes.BadZip;
    }

    public PackageRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = ReasonCodes.BadZip;
    }

    public PackageRejectedException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
