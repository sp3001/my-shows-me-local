using System.Reflection;
using Shows.Application;
using Xunit;

namespace Shows.Tests.Architecture;

/// <summary>
/// Step 1.4 acceptance: no Application interface method returns <c>IQueryable</c> (ARCHITECTURE.md §3).
/// An IQueryable in a public signature leaks the EF provider into the application layer and lets a
/// caller compose untestable queries against the data model.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ApplicationInterfaceTests
{
    [Fact]
    public void No_application_interface_method_returns_iqueryable()
    {
        var offenders = typeof(ITrackedShowRepository).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && t.Namespace == "Shows.Application")
            .SelectMany(i => i.GetMethods())
            .Where(m => ReturnsQueryable(m.ReturnType))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name} -> {m.ReturnType.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    // IQueryable<T> itself, or a Task<IQueryable<T>> / IReadOnlyList-returning method whose element is
    // IQueryable — anything that hands the caller a composable query.
    private static bool ReturnsQueryable(Type returnType)
    {
        if (IsQueryable(returnType))
        {
            return true;
        }

        // Unwrap Task<T>.
        var unwrapped = returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

        return IsQueryable(unwrapped);
    }

    private static bool IsQueryable(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>);
}
