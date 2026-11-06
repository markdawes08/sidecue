using System.Reflection;
using System.Runtime.InteropServices;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Application.Tests.Contracts;

public sealed class AbstractionBoundaryTests
{
    [Fact]
    public void PublicContractsExposeOnlyDomainValuesAndNarrowFrameworkTypes()
    {
        var applicationAssembly = typeof(ISessionStore).Assembly;
        var coreAssembly = typeof(SessionId).Assembly;
        var abstractions = applicationAssembly.GetExportedTypes()
            .Where(type => type.IsInterface && type.Namespace == "Sidecue.Application.Abstractions").ToArray();
        Assert.NotEmpty(abstractions);

        var inspected = new HashSet<Type>();
        foreach (var abstraction in abstractions) { Inspect(abstraction); }

        // Also inspect event variants: an interface returning the abstract base
        // must not accidentally hide a platform type on one of its payloads.
        foreach (var type in coreAssembly.GetExportedTypes()) { Inspect(type); }

        void Inspect(Type type)
        {
            Assert.False(type.IsPointer || type.IsByRef || type == typeof(object) ||
                type == typeof(IntPtr) || type == typeof(UIntPtr) ||
                typeof(SafeHandle).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type),
                $"Opaque or native type exposed: {type}");
            if (!inspected.Add(type)) { return; }
            foreach (var argument in type.GetGenericArguments()) { Inspect(argument); }
            if (type.IsArray) { Inspect(type.GetElementType()!); return; }

            if (type.Assembly == coreAssembly || type.Assembly == applicationAssembly)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    CheckName(property.Name);
                    Inspect(property.PropertyType);
                }
                foreach (var constructor in type.GetConstructors())
                {
                    foreach (var parameter in constructor.GetParameters())
                    {
                        CheckName(parameter.Name);
                        Inspect(parameter.ParameterType);
                    }
                }
                if (type.IsInterface)
                {
                    foreach (var inherited in type.GetInterfaces()) { Inspect(inherited); }
                    foreach (var method in type.GetMethods())
                    {
                        Inspect(method.ReturnType);
                        foreach (var parameter in method.GetParameters())
                        {
                            CheckName(parameter.Name);
                            Inspect(parameter.ParameterType);
                        }
                    }
                }
                return;
            }

            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            Type[] frameworkTypes =
            [
                typeof(void), typeof(bool), typeof(byte), typeof(int), typeof(long), typeof(string),
                typeof(Guid), typeof(DateTimeOffset), typeof(TimeSpan), typeof(CancellationToken),
                typeof(Task), typeof(Task<>), typeof(ValueTask), typeof(ValueTask<>),
                typeof(IDisposable), typeof(IAsyncDisposable), typeof(IAsyncEnumerable<>),
                typeof(IEnumerable<>), typeof(IReadOnlyList<>), typeof(Nullable<>),
                typeof(Memory<>), typeof(ReadOnlyMemory<>), typeof(System.Buffers.IMemoryOwner<>),
                typeof(System.Collections.Immutable.ImmutableArray<>)
            ];
            Assert.Contains(definition, frameworkTypes);
        }
    }

    private static void CheckName(string? name)
    {
        // Strings remain useful for IDs and text, but secrets must be resolved
        // behind adapters rather than added to these public request envelopes.
        string[] credentialNames = ["apikey", "credential", "credentials", "password", "secret", "connectionstring", "accesstoken"];
        Assert.DoesNotContain(name?.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant(), credentialNames);
    }
}
