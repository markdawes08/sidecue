namespace Sidecue.Core.Contracts;

internal static class ContractGuard
{
    internal static void Guid(Guid value, string parameterName)
    {
        if (value == System.Guid.Empty)
        {
            throw new ArgumentException("An identifier must not be empty.", parameterName);
        }
    }

    internal static void Version(ContractVersion version, string parameterName = "version")
    {
        if (version != ContractVersion.Current)
        {
            throw new ArgumentException("The contract version is unsupported.", parameterName);
        }
    }

    internal static void Text(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
    }
}
