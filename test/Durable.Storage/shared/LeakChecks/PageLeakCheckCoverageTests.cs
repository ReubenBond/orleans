using System.Reflection;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Keeps page leak checks from depending on each test class remembering to opt in: in an assembly whose tests own
/// buffers, every test class holds an <see cref="ArcBufferLeakCheck"/> and disposes it, or names why it needs none
/// (docs/spec/verification.md §11.1).
/// </summary>
[NoPageLeakCheck("It reflects over the assembly's test classes and rents no pages.")]
public sealed class PageLeakCheckCoverageTests
{
    [Fact]
    public void EveryTestClassChecksForPageLeaksOrNamesWhyNot()
    {
        var testClasses = typeof(PageLeakCheckCoverageTests).Assembly.GetTypes().Where(IsTestClass).ToList();
        var missing = testClasses
            .Where(static type => type.GetCustomAttribute<NoPageLeakCheckAttribute>() is null && !Checks(type))
            .Select(static type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(
            missing.Count == 0,
            $"These test classes neither hold and dispose an {nameof(ArcBufferLeakCheck)} nor carry [NoPageLeakCheck(reason)]: {string.Join(", ", missing)}");
        Assert.All(
            testClasses.Select(static type => type.GetCustomAttribute<NoPageLeakCheckAttribute>()).OfType<NoPageLeakCheckAttribute>(),
            static exemption => Assert.False(string.IsNullOrWhiteSpace(exemption.Reason)));
    }

    private static bool IsTestClass(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Any(static method => method.IsDefined(typeof(FactAttribute), inherit: true));

    private static bool Checks(Type type) =>
        (typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type))
        && type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(static field => field.FieldType == typeof(ArcBufferLeakCheck));
}
