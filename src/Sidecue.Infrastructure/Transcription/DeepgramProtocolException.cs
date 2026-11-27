namespace Sidecue.Infrastructure.Transcription;

/// <summary>Safe categories for rejected or unsupported untrusted provider messages.</summary>
public enum DeepgramProtocolError { Malformed, UnsupportedTiming, Conflict, CapacityExceeded, UnsupportedMessage }

/// <summary>Contains no provider payload or transcript text.</summary>
public sealed class DeepgramProtocolException(DeepgramProtocolError error) : Exception("The speech provider message could not be normalized.")
{
    public DeepgramProtocolError Error { get; } = error;
}
