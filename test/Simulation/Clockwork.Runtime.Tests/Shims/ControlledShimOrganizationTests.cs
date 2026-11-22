using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

public sealed partial class ControlledShimOrganizationTests
{
    [Fact]
    public void ControlledTypesMirrorFrameworkNamesAndNamespaces()
    {
        Type[] frameworkTypes =
        [
            .. new[]
            {
                typeof(object).Assembly,
                typeof(System.Diagnostics.Stopwatch).Assembly,
                typeof(System.Threading.Barrier).Assembly,
                typeof(System.Threading.Tasks.Parallel).Assembly,
                typeof(System.Collections.Concurrent.Partitioner).Assembly,
                typeof(RandomNumberGenerator).Assembly,
                typeof(System.Timers.Timer).Assembly,
                typeof(System.Threading.Channels.ChannelReader<>).Assembly,
                typeof(System.Linq.ParallelEnumerable).Assembly,
                typeof(System.Collections.Concurrent.BlockingCollection<>).Assembly,
                typeof(System.ComponentModel.BackgroundWorker).Assembly,
                typeof(Microsoft.Extensions.Hosting.BackgroundService).Assembly,
                typeof(System.Threading.Tasks.Dataflow.DataflowBlock).Assembly,
                typeof(System.Net.WebSockets.WebSocket).Assembly,
                typeof(System.Net.Http.HttpClient).Assembly,
                typeof(System.Net.Http.Json.HttpClientJsonExtensions).Assembly,
            }
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes()),
        ];
        Type[] controlledTypes = typeof(SimulationRuntimeDispatch).Assembly
            .GetTypes()
            .Where(type => type.Name.StartsWith("Controlled", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(controlledTypes);
        foreach (Type controlledType in controlledTypes)
        {
            string shimNamespace = Assert.IsType<string>(controlledType.Namespace);
            Assert.StartsWith("Orleans.Testing.Simulation.Shims.", shimNamespace, StringComparison.Ordinal);

            string frameworkNamespace = shimNamespace["Orleans.Testing.Simulation.Shims.".Length..];
            string frameworkName = TrimGenericArity(controlledType.Name["Controlled".Length..]);
            Assert.True(
                frameworkTypes.Any(
                    type => type.Namespace == frameworkNamespace && TrimGenericArity(type.Name) == frameworkName),
                $"{controlledType.FullName} does not mirror a framework type named {frameworkNamespace}.{frameworkName}.");
        }
    }

    [Fact]
    public void ControlledTypeDeclarationsAreUnderShimDirectory()
    {
        string runtimeRoot = Path.Combine(GetSimulationKernelProjectDirectory(), "Runtime");
        string shimRoot = Path.Combine(runtimeRoot, "Shims") + Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (string sourceFile in Directory.EnumerateFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (!ControlledTypeDeclaration().IsMatch(File.ReadAllText(sourceFile)))
            {
                continue;
            }

            Assert.True(
                Path.GetFullPath(sourceFile).StartsWith(shimRoot, pathComparison),
                $"Controlled type declaration must be beneath '{shimRoot}': {sourceFile}");
        }
    }

    [Fact]
    public void SimulationKernelSourcePathComesFromEvaluatedProjectReference()
    {
        string projectPath = GetSimulationKernelProjectPath();

        Assert.True(Path.IsPathFullyQualified(projectPath), $"Expected a fully qualified project path: {projectPath}");
        Assert.True(File.Exists(projectPath), $"Evaluated kernel ProjectReference does not exist: {projectPath}");
        Assert.True(
            Directory.Exists(Path.Combine(Path.GetDirectoryName(projectPath)!, "Runtime", "Shims")),
            $"Evaluated kernel ProjectReference does not contain Runtime/Shims: {projectPath}");
    }

    private static string TrimGenericArity(string typeName)
    {
        int arityMarker = typeName.IndexOf('`', StringComparison.Ordinal);
        return arityMarker < 0 ? typeName : typeName[..arityMarker];
    }

    private static string GetSimulationKernelProjectDirectory() =>
        Path.GetDirectoryName(GetSimulationKernelProjectPath())
        ?? throw new InvalidOperationException("The simulation kernel ProjectReference has no directory.");

    private static string GetSimulationKernelProjectPath() =>
        typeof(ControlledShimOrganizationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SimulationKernelProjectPath")
            .Value
        ?? throw new InvalidOperationException("The simulation kernel ProjectReference path is missing.");

    [GeneratedRegex(
        @"^\s*(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref)\s+)*(?:class|struct|record(?:\s+(?:class|struct))?|interface|enum)\s+Controlled\w+",
        RegexOptions.Multiline)]
    private static partial Regex ControlledTypeDeclaration();
}
