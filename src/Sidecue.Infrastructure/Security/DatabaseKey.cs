using System.Security.Cryptography;

namespace Sidecue.Infrastructure.Security;

/// <summary>An in-memory database key. The owner must dispose it promptly.</summary>
public sealed class DatabaseKey : IDisposable
{
    private byte[]? bytes;

    internal DatabaseKey(byte[] bytes) => this.bytes = bytes;

    internal byte[] Bytes => bytes ?? throw new ObjectDisposedException(nameof(DatabaseKey));

    public void Dispose()
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
            bytes = null;
        }
    }
}
