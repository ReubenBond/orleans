using System.Globalization;
using System.Security.Cryptography;
using SharpFuzz;

namespace Orleans.Durable.Storage.Fuzz;

/// <summary>
/// Runs a decoder's target under libFuzzer, replays inputs through it, or writes its seeds (verification.md §6.1).
/// <c>eng/fuzz.ps1</c> runs campaigns.
/// </summary>
internal static class Program
{
    private const string Usage =
        """
        Usage:
          Orleans.Durable.Storage.Fuzz <target>                       Fuzz the target under libfuzzer-dotnet (eng/fuzz.ps1 starts it).
          Orleans.Durable.Storage.Fuzz replay <target> <path>...      Check each input file, or every file below each directory.
          Orleans.Durable.Storage.Fuzz seed <target> <directory>      Write the target's seeds from the golden vectors.
          Orleans.Durable.Storage.Fuzz list                           List the targets and the longest input each campaign generates.
        """;

    /// <summary>
    /// The variable libfuzzer-dotnet sets for the shared memory through which it passes inputs.
    /// </summary>
    private const string LibFuzzerVariable = "__LIBFUZZER_SHM_ID";

    public static int Main(string[] args)
    {
        FuzzTarget.IsolatedProcess = true;
        return args switch
        {
            ["list"] => Instrumented(List),
            ["seed", var name, var directory] => Run(name, target => Instrumented(() => Seed(target, directory))),
            ["replay", var name, .. var paths] when paths.Length > 0 => Run(name, target => Instrumented(() => Replay(target, paths))),
            [var name] when name is not ("list" or "seed" or "replay") => Run(name, Fuzz),
            _ => Fail(Usage),
        };
    }

    private static int Run(string name, Func<FuzzTarget, int> command) =>
        FuzzTargets.Find(name) is { } target ? command(target) : Fail($"There is no target '{name}'. {Usage}");

    /// <summary>
    /// Runs a command which may reach a decoder assembly SharpFuzz instrumented. Instrumented code counts coverage in a
    /// map which only a fuzzer's run sets up; outside a fuzzer, <see cref="Fuzzer.Run(Action{Stream})"/> runs its action
    /// once with a map of its own, so a command runs in an instrumented publish as in an ordinary build.
    /// </summary>
    private static int Instrumented(Func<int> command)
    {
        var exitCode = 1;
        Fuzzer.Run(_ => exitCode = command());
        return exitCode;
    }

    private static int List()
    {
        var vectors = GoldenVectors.Find();
        foreach (var target in FuzzTargets.All)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{target.Name}\t{FuzzTargets.MaxLength(target, vectors)}"));
        }

        return 0;
    }

    private static int Seed(FuzzTarget target, string directory)
    {
        _ = Directory.CreateDirectory(directory);
        var count = 0;
        foreach (var seed in target.Seeds(GoldenVectors.Find()))
        {
            File.WriteAllBytes(Path.Combine(directory, "seed-" + Convert.ToHexStringLower(SHA256.HashData(seed))[..16]), seed);
            count++;
        }

        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {count} seeds of {target.Name} to {directory}."));
        return 0;
    }

    /// <summary>
    /// Checks each input file.
    /// </summary>
    private static int Replay(FuzzTarget target, string[] paths)
    {
        var files = paths
            .SelectMany(static path => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : [path])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var outcomes = new Dictionary<FuzzOutcome, int>();
        foreach (var file in files)
        {
            FuzzOutcome outcome;
            try
            {
                outcome = target.Check(File.ReadAllBytes(file));
            }
            catch (FuzzFindingException finding)
            {
                return Fail($"{file}: {finding}");
            }

            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        var summary = string.Join(", ", outcomes.OrderBy(static pair => pair.Key).Select(static pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Value} {pair.Key}")));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{target.Name}: {files.Length} inputs satisfy the oracle ({summary})."));
        return 0;
    }

    /// <summary>
    /// Serves libfuzzer-dotnet: each input it passes is checked, and a finding ends the process as a crash.
    /// </summary>
    private static int Fuzz(FuzzTarget target)
    {
        if (Environment.GetEnvironmentVariable(LibFuzzerVariable) is null)
        {
            return Fail($"'{target.Name}' runs under libfuzzer-dotnet; run a campaign with eng/fuzz.ps1, or check inputs with 'replay'.");
        }

        Fuzzer.LibFuzzer.Run(input => _ = target.Check(input));
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
