using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Sidecue.Core.Contracts;

namespace Sidecue.Infrastructure.Transcription;

public interface IDeepgramConnectionFactory
{
    ValueTask<IDeepgramConnection> OpenAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken);
}

/// <summary>One connection with at most one active send and one active receive.</summary>
public interface IDeepgramConnection : IDisposable
{
    /// <summary>The sender owns the audio until this operation completes, including after cancellation.</summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken);

    ValueTask SendCloseStreamAsync(CancellationToken cancellationToken);
    ValueTask SendKeepAliveAsync(CancellationToken cancellationToken);

    /// <summary>Receives a complete text message, or null for a normal peer close.</summary>
    ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Aborts the connection and cancels pending sends and receives.</summary>
    void Abort();
}

/// <summary>Safe transport failure without credentials, provider messages, or raw platform exceptions.</summary>
public sealed class DeepgramTransportException(EventErrorCategory category, bool retryable, HttpStatusCode? statusCode = null)
    : Exception(GetSafeMessage(category))
{
    public EventErrorCategory Category { get; } = category;
    public bool Retryable { get; } = retryable;
    public HttpStatusCode? StatusCode { get; } = statusCode;

    private static string GetSafeMessage(EventErrorCategory category) => category switch
    {
        EventErrorCategory.ProviderAuthentication => "Deepgram authentication failed. Check the configured API key and permissions.",
        EventErrorCategory.ProviderRateLimit => "Deepgram rate-limited this connection.",
        EventErrorCategory.UnsupportedCapability => "Deepgram streaming configuration or endpoint was rejected.",
        EventErrorCategory.ProtocolConflict => "Deepgram returned an invalid or unsupported response.",
        _ => "The Deepgram streaming connection is unavailable."
    };
}

/// <summary>Connects only to Deepgram Listen v1 over TLS. Redirects and cookies are disabled.</summary>
public sealed class DeepgramWebSocketConnectionFactory : IDeepgramConnectionFactory
{
    public async ValueTask<IDeepgramConnection> OpenAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "wss" || endpoint.Port != 443 ||
            !string.Equals(endpoint.IdnHost, DeepgramStreamingOptions.EndpointHost, StringComparison.Ordinal) ||
            endpoint.AbsolutePath != DeepgramStreamingOptions.EndpointPath ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new DeepgramTransportException(EventErrorCategory.UnsupportedCapability, retryable: false);
        }

        if (string.IsNullOrEmpty(apiKey) || apiKey.Length > 4096 || !apiKey.All(static character => character is > ' ' and <= '~'))
        {
            throw new DeepgramTransportException(EventErrorCategory.ProviderAuthentication, retryable: false);
        }

        var socket = new ClientWebSocket();
        var invoker = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        bool transferred = false;
        try
        {
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.SetRequestHeader("Authorization", "Token " + apiKey);
            await socket.ConnectAsync(endpoint, invoker, cancellationToken).ConfigureAwait(false);
            var connection = new Connection(socket, invoker);
            transferred = true;
            return connection;
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw ClassifyUpgrade(socket.HttpStatusCode);
        }
        finally
        {
            if (!transferred)
            {
                socket.Dispose();
                invoker.Dispose();
            }
        }
    }

    private static DeepgramTransportException ClassifyUpgrade(HttpStatusCode statusCode)
    {
        int status = (int)statusCode;
        return status switch
        {
            0 => new(EventErrorCategory.ProviderUnavailable, retryable: true),
            401 or 403 => new(EventErrorCategory.ProviderAuthentication, retryable: false, statusCode),
            429 => new(EventErrorCategory.ProviderRateLimit, retryable: true, statusCode),
            408 or >= 500 and <= 599 => new(EventErrorCategory.ProviderUnavailable, retryable: true, statusCode),
            _ => new(EventErrorCategory.UnsupportedCapability, retryable: false, statusCode)
        };
    }

    private sealed class Connection(ClientWebSocket socket, HttpMessageInvoker invoker) : IDeepgramConnection
    {
        private const int MaxResponseBytes = 256 * 1024;
        private const int MaxReceiveOperations = 1024;
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly byte[] CloseStream = "{\"type\":\"CloseStream\"}"u8.ToArray();
        private static readonly byte[] KeepAlive = "{\"type\":\"KeepAlive\"}"u8.ToArray();

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken) =>
            SendAsync(audio, WebSocketMessageType.Binary, cancellationToken);

        public ValueTask SendCloseStreamAsync(CancellationToken cancellationToken) =>
            SendAsync(CloseStream, WebSocketMessageType.Text, cancellationToken);

        public ValueTask SendKeepAliveAsync(CancellationToken cancellationToken) =>
            SendAsync(KeepAlive, WebSocketMessageType.Text, cancellationToken);

        public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            byte[] message = ArrayPool<byte>.Shared.Rent(MaxResponseBytes);
            int length = 0;
            try
            {
                for (int operation = 0; operation < MaxReceiveOperations; operation++)
                {
                    ValueWebSocketReceiveResult result = await socket.ReceiveAsync(
                        message.AsMemory(length, MaxResponseBytes - length), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return ReadPeerClose(socket.CloseStatus);
                    }

                    length += result.Count;
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        throw InvalidResponse();
                    }

                    if (result.EndOfMessage)
                    {
                        return StrictUtf8.GetString(message, 0, length);
                    }

                    if (length == MaxResponseBytes)
                    {
                        throw InvalidResponse();
                    }
                }

                throw InvalidResponse();
            }
            catch (DecoderFallbackException)
            {
                throw InvalidResponse();
            }
            catch (Exception error) when (IsSocketFailure(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new DeepgramTransportException(EventErrorCategory.ProviderUnavailable, retryable: true);
            }
            finally
            {
                // An aborted receive may have written into the destination before throwing.
                ArrayPool<byte>.Shared.Return(message, clearArray: true);
            }
        }

        public void Abort() => socket.Abort();

        public void Dispose()
        {
            socket.Dispose();
            invoker.Dispose();
        }

        private async ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type,
            CancellationToken cancellationToken)
        {
            try
            {
                await socket.SendAsync(payload, type, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsSocketFailure(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new DeepgramTransportException(EventErrorCategory.ProviderUnavailable, retryable: true);
            }
        }

        private static string? ReadPeerClose(WebSocketCloseStatus? status)
        {
            switch (status)
            {
                case WebSocketCloseStatus.NormalClosure:
                    return null;
                case WebSocketCloseStatus.ProtocolError or WebSocketCloseStatus.InvalidMessageType or
                    WebSocketCloseStatus.InvalidPayloadData or WebSocketCloseStatus.MessageTooBig:
                    throw InvalidResponse();
                case WebSocketCloseStatus.PolicyViolation:
                    throw new DeepgramTransportException(EventErrorCategory.UnsupportedCapability, retryable: false);
                default:
                    throw new DeepgramTransportException(EventErrorCategory.ProviderUnavailable, retryable: true);
            }
        }

        private static bool IsSocketFailure(Exception error) => error is WebSocketException or IOException or
            ObjectDisposedException or OperationCanceledException;

        private static DeepgramTransportException InvalidResponse() => new(EventErrorCategory.ProtocolConflict, retryable: false);
    }
}
