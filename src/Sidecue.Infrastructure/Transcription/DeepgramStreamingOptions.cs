using System.Globalization;
using Sidecue.Core.Contracts;

namespace Sidecue.Infrastructure.Transcription;

/// <summary>One explicitly enabled, versioned Listen v1 configuration. Contains no credential.</summary>
public sealed class DeepgramStreamingOptions
{
    public const string ProviderId = "deepgram";
    internal const string EndpointHost = "api.deepgram.com";
    internal const string EndpointPath = "/v1/listen";

    public DeepgramStreamingOptions(string configurationVersion, string model, string modelVersion,
        int? endpointingMilliseconds = 300, int maximumReconnects = 2, TimeSpan? connectTimeout = null,
        TimeSpan? sendTimeout = null, TimeSpan? drainTimeout = null, TimeSpan? retryDelay = null,
        TimeSpan? keepAliveInterval = null)
    {
        ConfigurationVersion = RequireIdentifier(configurationVersion, nameof(configurationVersion));
        Model = RequireIdentifier(model, nameof(model));
        ModelVersion = RequireIdentifier(modelVersion, nameof(modelVersion));
        if (string.Equals(ModelVersion, "latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A specific speech model version must be configured.", nameof(modelVersion));
        }

        if (endpointingMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointingMilliseconds));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maximumReconnects);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumReconnects, 5);
        EndpointingMilliseconds = endpointingMilliseconds;
        MaximumReconnects = maximumReconnects;
        ConnectTimeout = RequireDuration(connectTimeout ?? TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), nameof(connectTimeout));
        SendTimeout = RequireDuration(sendTimeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), nameof(sendTimeout));
        DrainTimeout = RequireDuration(drainTimeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), nameof(drainTimeout));
        RetryDelay = RequireDuration(retryDelay ?? TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5), nameof(retryDelay), allowZero: true);
        KeepAliveInterval = RequireDuration(keepAliveInterval ?? TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), nameof(keepAliveInterval));
    }

    public string ConfigurationVersion { get; }
    public string Model { get; }
    public string ModelVersion { get; }
    public int? EndpointingMilliseconds { get; }
    public int MaximumReconnects { get; }
    public TimeSpan ConnectTimeout { get; }
    public TimeSpan SendTimeout { get; }
    public TimeSpan DrainTimeout { get; }
    public TimeSpan RetryDelay { get; }
    public TimeSpan KeepAliveInterval { get; }

    public Uri CreateEndpoint(TranscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProviderConfigurationReference reference = request.ProviderConfiguration;
        AudioFormat format = request.Format;
        if (!string.Equals(reference.ProviderId, ProviderId, StringComparison.Ordinal) ||
            !string.Equals(reference.ConfigurationVersion, ConfigurationVersion, StringComparison.Ordinal) ||
            !IsIdentifier(request.Language) ||
            format.SampleRate != 16_000 || format.Channels != 1 ||
            format.Representation != AudioSampleRepresentation.Pcm16 || format.ByteOrder != AudioByteOrder.LittleEndian)
        {
            throw new DeepgramTransportException(EventErrorCategory.UnsupportedCapability, retryable: false);
        }

        string endpointing = EndpointingMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "false";
        return new UriBuilder("wss", EndpointHost, -1, EndpointPath)
        {
            Query = $"model={Model}&version={ModelVersion}&language={request.Language}" +
                $"&encoding=linear16&sample_rate=16000&channels=1&interim_results=true&endpointing={endpointing}" +
                "&diarize_model=v1&utterance_end_ms=1000"
        }.Uri;
    }

    private static string RequireIdentifier(string value, string name)
    {
        if (!IsIdentifier(value))
        {
            throw new ArgumentException("A valid speech configuration identifier is required.", name);
        }

        return value;
    }

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static TimeSpan RequireDuration(TimeSpan duration, TimeSpan maximum, string name, bool allowZero = false)
    {
        if (duration < (allowZero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(1)) || duration > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return duration;
    }
}
