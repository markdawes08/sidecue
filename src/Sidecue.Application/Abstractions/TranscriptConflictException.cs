namespace Sidecue.Application.Abstractions;

/// <summary>A normalized transcript event conflicts with committed evidence.</summary>
public sealed class TranscriptConflictException : Exception
{
    public TranscriptConflictException() : base("The transcript event conflicts with the durable transcript or its event contract.") { }
}
