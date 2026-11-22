using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// Qualifies the pinned .NET 11 GA runtime with preview-language lowering from the same SDK compiler
/// used by consumers. The union-generated shape and its async state machine must both survive the
/// normal controlled-task rewrite in Debug and Release.
/// </summary>
public sealed class PreviewUnionConformanceTests : IDisposable
{
    private const string Source = """
        using System.Threading.Tasks;

        namespace Conf;

        public union PreviewValue(int, string);

        public static class PreviewUnionProbe
        {
            public static async Task<int> MeasureAsync(bool number)
            {
                await Task.Yield();
                PreviewValue value = number ? 42 : "clockwork";
                return value switch
                {
                    int item => item,
                    string item => item.Length,
                };
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();

    public static TheoryData<bool> Optimizations => new() { false, true };

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void PreviewUnionLoweringRunsUnderControlledAsync(bool optimize)
    {
        Assert.Equal(11, Environment.Version.Major);
        Assert.StartsWith(".NET 11.", RuntimeInformation.FrameworkDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("-", RuntimeInformation.FrameworkDescription, StringComparison.Ordinal);
        StagedProbe probe = _fixture.StagePreviewControlledTasks(
            $"Conf.PreviewUnion.{(optimize ? "Rel" : "Dbg")}",
            "Conf.PreviewUnionProbe",
            Source,
            optimize);
        using var host = new SimulationHost(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(42, Result<int>(host.Invoke(probe.Method("MeasureAsync"), true)));
        Assert.Equal(9, Result<int>(host.Invoke(probe.Method("MeasureAsync"), false)));
    }

    private static T Result<T>(object? task)
    {
        var typed = (Task)task!;
        Assert.True(typed.IsCompleted);
        return (T)typed.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)!.GetValue(typed)!;
    }

    public void Dispose() => _fixture.Dispose();
}
