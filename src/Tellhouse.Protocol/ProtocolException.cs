namespace Tellhouse.Protocol;

/// <summary>
/// Raised for every protocol level rejection: malformed envelope,
/// failed signature, failed open, replay, wrong recipient, expired
/// challenge. Callers treat any ProtocolException as reject the
/// packet whole, create no event, and write one trail line.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}
