using System.Runtime.CompilerServices;
using Xunit;

namespace Sidecue.Infrastructure.Tests.Transcripts;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SqlCipherFactAttribute : FactAttribute
{
    internal const string NativePathVariable = "SIDECUE_SQLCIPHER_NATIVE_PATH";

    public SqlCipherFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NativePathVariable)))
        {
            Skip = $"Set {NativePathVariable} to the absolute path of the reviewed SQLCipher DLL.";
        }
    }
}
