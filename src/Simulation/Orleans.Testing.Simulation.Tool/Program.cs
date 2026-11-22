using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Replay;

namespace Orleans.Testing.Simulation.Tool;

/// <summary>
/// Entry point for the <c>orleans-simulation</c> CLI. It dispatches to a command, maps every failure class to a
/// distinct <see cref="ExitCode"/>, and keeps output deterministic (no timestamps, stable ordering) so
/// it is scriptable and testable.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) => (int)Run(args, Console.Out, Console.Error);

    /// <summary>Runs the CLI with explicit streams; used directly by tests.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="output">The standard output writer.</param>
    /// <param name="error">The standard error writer.</param>
    /// <returns>The process exit code.</returns>
    public static ExitCode Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(output);
            return args.Length == 0 ? ExitCode.UsageError : ExitCode.Success;
        }

        if (args[0] is "--version")
        {
            output.WriteLine(Instrumentation.Rewriting.RewriteEngine.EngineVersion);
            return ExitCode.Success;
        }

        string command = args[0];
        string[] rest = args[1..];
        try
        {
            return command switch
            {
                "instrument" => InstrumentCommand.Run(rest, output, error),
                "inspect" => InspectCommand.Run(rest, output, error),
                "record" => ReplayCommands.RunRecord(rest, output),
                "replay" => ReplayCommands.RunReplay(rest, output),
                "explore" => ReplayCommands.RunExplore(rest, output),
                "minimize" => ReplayCommands.RunMinimize(rest, output),
                "trace" => ReplayCommands.RunTrace(rest, output),
                _ => Fail(error, $"Unknown command '{command}'.", ExitCode.UsageError),
            };
        }
        catch (UsageException ex)
        {
            return Fail(error, ex.Message, ExitCode.UsageError);
        }
        catch (ConfigurationException ex)
        {
            return Fail(error, ex.Message, ExitCode.ConfigurationError);
        }
        catch (Instrumentation.Orchestration.ClosureManifestFormatException ex)
        {
            return Fail(error, ex.Message, ExitCode.ReplayError);
        }
        catch (ClosureException ex)
        {
            return Fail(error, ex.Message, ExitCode.ClosureError);
        }
        catch (ReplayMinimizationException ex)
        {
            return Fail(error, ex.Message, ExitCode.MinimizationError);
        }
        catch (Exception ex) when (
            ex is ReplayArtifactFormatException or
            ReplayCompatibilityException or
            SimulationDecisionReplayMismatchException or
            ReplayOutcomeMismatchException)
        {
            return Fail(error, ex.Message, ExitCode.ReplayError);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or BadImageFormatException)
        {
            return Fail(error, ex.Message, ExitCode.IoError);
        }
    }

    private static ExitCode Fail(TextWriter error, string message, ExitCode code)
    {
        error.WriteLine($"dotnet-orleans-simulation: {message}");
        return code;
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "help";

    private static void WriteUsage(TextWriter output)
    {
        output.WriteLine("dotnet-orleans-simulation - deterministic Orleans simulation tool");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  dotnet orleans-simulation instrument --source <dir> --output <dir> [options]");
        output.WriteLine("  dotnet orleans-simulation inspect <assembly|dir>... [options]");
        output.WriteLine("  dotnet orleans-simulation record --assembly <path> --scenario-type <type> --artifact <path> --simulation-seed <int> [options]");
        output.WriteLine("  dotnet orleans-simulation replay <artifact> --assembly <path> --scenario-type <type> [options]");
        output.WriteLine("  dotnet orleans-simulation explore --assembly <path> --scenario-type <type> --output <dir> --simulation-seed <int> [options]");
        output.WriteLine("  dotnet orleans-simulation minimize <artifact> --assembly <path> --scenario-type <type> [options]");
        output.WriteLine("  dotnet orleans-simulation trace show <artifact> [--json]");
        output.WriteLine();
        output.WriteLine("instrument options:");
        output.WriteLine("  --source <dir>              application output/publish directory to read (required)");
        output.WriteLine("  --output <dir>             staging directory to write the instrumented closure (required unless --dry-run)");
        output.WriteLine("  --config <path>            JSON instrumentation configuration file");
        output.WriteLine("  --rule-set <path>          rule-set JSON document (repeatable; appended to config)");
        output.WriteLine("  --include <glob>           include pattern (repeatable)");
        output.WriteLine("  --exclude <glob>           exclude pattern (repeatable)");
        output.WriteLine("  --entry <name>             entry assembly simple name (else auto-detected)");
        output.WriteLine("  --manifest <path>          manifest output path (else a sibling of --output)");
        output.WriteLine("  --mode <Controlled|RaceExploration> instrumentation granularity (default Controlled)");
        output.WriteLine("  --target-runtime <major.minor> runtime rules are evaluated against (default: the entry's runtimeconfig.json)");
        output.WriteLine("  --builtin <id|all>         built-in rule set (repeatable)");
        output.WriteLine("  --builtin-include <family> include built-in family (repeatable)");
        output.WriteLine("  --builtin-exclude <family> exclude built-in family (repeatable)");
        output.WriteLine("  --dry-run                  report the planned transformation without writing");
        output.WriteLine("  --json                     emit JSON instead of text");
        output.WriteLine();
        output.WriteLine("inspect options:");
        output.WriteLine("  <assembly|dir>...          assemblies or directories to inspect");
        output.WriteLine("  --config <path>            configuration file (to report the merged rule set)");
        output.WriteLine("  --rule-set <path>          rule-set document (repeatable)");
        output.WriteLine("  --builtin <id|all>         built-in rule set (repeatable)");
        output.WriteLine("  --builtin-include <family> include built-in family (repeatable)");
        output.WriteLine("  --builtin-exclude <family> exclude built-in family (repeatable)");
        output.WriteLine("  --include <glob>           configuration include pattern (repeatable)");
        output.WriteLine("  --exclude <glob>           configuration exclude pattern (repeatable)");
        output.WriteLine("  --mode <Controlled|RaceExploration> configuration instrumentation granularity");
        output.WriteLine("  --target-runtime <major.minor> configuration target runtime");
        output.WriteLine("  --json                     emit JSON instead of text");
        output.WriteLine();
        output.WriteLine();
        output.WriteLine("replay/exploration options:");
        output.WriteLine("  --assembly <path>          explicit scenario harness assembly");
        output.WriteLine("  --scenario-type <type>     public IReplayScenario implementation with a public parameterless constructor");
        output.WriteLine("  --manifest <path>          closure instrumentation manifest used for compatibility checks");
        output.WriteLine("  --simulation-seed <int>    stable application/environment seed");
        output.WriteLine("  --schedule-seed <int>      explicit scheduler seed");
        output.WriteLine("  --strategy <name>          fifo|round-robin|priority|seeded-random");
        output.WriteLine("  --max-steps <int>          controlled step bound per execution");
        output.WriteLine("  --count <int>              maximum exploration iterations");
        output.WriteLine("  --max-failures <int>       exploration failure limit");
        output.WriteLine("  --time-limit <timespan>    wall-clock limit checked between iterations");
        output.WriteLine("  --json                     emit deterministic JSON");
        output.WriteLine();
        output.WriteLine("Exit codes: 0 success, 1 usage, 2 configuration, 3 closure, 4 instrumentation, 5 I/O,");
        output.WriteLine("            6 scenario failure, 7 replay/compatibility/divergence, 8 minimization.");
    }
}
