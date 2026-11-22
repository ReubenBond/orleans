using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Execution;

/// <summary>
/// Full out-of-process execution tests. Each builds a real console-application closure (app +
/// third-party dependency + controlled API + shim), stages an instrumented copy with the
/// orchestrator, and launches both the original and the staged executables as independent
/// processes. Together they prove the central build/tool integration guarantee - an <em>enabled staged
/// executable dispatches to the test shim</em> while a <em>normal executable does not</em> - across
/// Debug/Release, symbols present/absent, config-loaded rules, a rejected call, an excluded
/// dependency, an incremental rebuild, and a signed closure.
/// </summary>
public sealed class ProcessExecutionTests
{
    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void BuiltInRulesRewriteThirdPartySynchronizationAndEveryDelayOverload(bool optimize)
    {
        using var fixture = BuiltInProcessFixture.Create(optimize);

        AppRunResult source = fixture.RunSource();
        Assert.True(
            source.ExitCode == 0,
            $"Source process failed ({source.ExitCode}):\n{source.StandardOutput}\n{source.StandardError}");
        Assert.Contains("monitor=real", source.Output);
        Assert.Contains("lock=System.Threading.Lock", source.Output);
        Assert.Contains("semaphore=signaled", source.Output);
        Assert.Contains("delays=6", source.Output);
        Assert.Contains("timer=System.Threading.Timer", source.Output);

        InstrumentationResult instrumentation = fixture.Instrument();
        Assert.True(instrumentation.Succeeded, string.Join("\n", instrumentation.Errors));
        Assert.Equal(new Version(Environment.Version.Major, Environment.Version.Minor), instrumentation.TargetRuntime);
        AppRunResult staged = fixture.RunStaged();
        Assert.True(
            staged.ExitCode == 0,
            $"Staged process failed ({staged.ExitCode}):\n{staged.StandardOutput}\n{staged.StandardError}");
        Assert.Contains("monitor=rejected", staged.Output);
        Assert.Contains("lock=Orleans.Testing.Simulation.Shims.System.Threading.ControlledLock", staged.Output);
        Assert.Contains("semaphore=signaled", staged.Output);
        Assert.Contains("delays=6", staged.Output);
        Assert.Contains("timer=Orleans.Testing.Simulation.Shims.System.Threading.ControlledTimer", staged.Output);
    }

    [Fact]
    public void SimulationStartRejectsAnAppRewrittenForAnotherRuntime()
    {
        // Rules classified for .NET 10 rewrite the app as they would for a net10.0 app that rolls forward onto this runtime.
        using var fixture = BuiltInProcessFixture.Create(optimize: false);
        InstrumentationResult instrumentation = fixture.Instrument(new Version(10, 0));
        Assert.True(instrumentation.Succeeded, string.Join("\n", instrumentation.Errors));

        AppRunResult staged = fixture.RunStaged();

        Assert.NotEqual(0, staged.ExitCode);
        Assert.Contains("UnsupportedRuntimeException", staged.StandardError);
        Assert.Contains(
            $"rewritten by Clockwork for .NET 10.0, but this process runs .NET {Environment.Version.Major}.{Environment.Version.Minor}",
            staged.StandardError);
    }

    [Fact]
    public void RewrittenCodeLoadedAfterTheSimulationStartsIsRejectedBeforeTheNextStepAndAfter()
    {
        using var fixture = BuiltInProcessFixture.Create(optimize: false, BuiltInProcessFixture.DelegatedAppSource);
        InstrumentationResult instrumentation = fixture.Instrument(new Version(10, 0), rewriteApp: false);
        Assert.True(instrumentation.Succeeded, string.Join("\n", instrumentation.Errors));

        AppRunResult staged = fixture.RunStaged();

        Assert.True(staged.ExitCode == 0, $"Staged process failed ({staged.ExitCode}):\n{staged.StandardOutput}\n{staged.StandardError}");
        Assert.Contains("attempt 1: rejected ThirdParty.Sync", staged.Output);
        Assert.Contains("attempt 2: rejected ThirdParty.Sync", staged.Output);
    }

    [Fact]
    public void NormalExecutableDoesNotDispatchToShim()
    {
        using var fixture = ExecutionClosureFixture.Create();

        AppRunResult run = fixture.RunSource();

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("app.ticks=100", run.Output);
        Assert.Contains("dep.ticks=100", run.Output);
        Assert.Contains("app.value=3", run.Output);
        // The shim sentinel values must never appear in an ordinary build.
        Assert.DoesNotContain("999", run.Output);
        Assert.DoesNotContain("app.value=7", run.Output);
    }

    [Fact]
    public void EnabledStagedExecutableDispatchesToShim()
    {
        using var fixture = ExecutionClosureFixture.Create();

        InstrumentationResult result = fixture.Instrument();
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        AppRunResult staged = fixture.RunStaged();
        Assert.Equal(0, staged.ExitCode);
        Assert.Contains("app.ticks=999", staged.Output);
        Assert.Contains("dep.ticks=999", staged.Output);
        Assert.Contains("app.value=7", staged.Output);

        // Instrumenting must never mutate the source: the original still behaves normally.
        AppRunResult source = fixture.RunSource();
        Assert.Contains("app.ticks=100", source.Output);
    }

    [Fact]
    public void ReleaseClosureWithoutSymbolsDispatchesToShim()
    {
        using var fixture = ExecutionClosureFixture.Create(FixtureSymbols.None, optimize: true);

        Assert.True(fixture.Instrument().Succeeded);

        AppRunResult staged = fixture.RunStaged();
        Assert.Equal(0, staged.ExitCode);
        Assert.Contains("app.ticks=999", staged.Output);
        Assert.Contains("dep.ticks=999", staged.Output);
    }

    [Fact]
    public void EmbeddedSymbolsClosureDispatchesToShim()
    {
        using var fixture = ExecutionClosureFixture.Create(FixtureSymbols.Embedded);

        Assert.True(fixture.Instrument().Succeeded);

        AppRunResult staged = fixture.RunStaged();
        Assert.Equal(0, staged.ExitCode);
        Assert.Contains("app.value=7", staged.Output);
    }

    [Fact]
    public void ExcludedDependencyRetainsOriginalBehavior()
    {
        using var fixture = ExecutionClosureFixture.Create();

        var configuration = new InstrumentationConfiguration
        {
            ExcludePatterns = [ExecutionClosureFixture.ThirdPartyAssemblyName + ".dll"],
        };
        Assert.True(fixture.Instrument(configuration).Succeeded);

        AppRunResult staged = fixture.RunStaged();
        Assert.Equal(0, staged.ExitCode);
        // The application assembly was rewritten, but the excluded dependency was copied verbatim.
        Assert.Contains("app.ticks=999", staged.Output);
        Assert.Contains("dep.ticks=100", staged.Output);
    }

    [Fact]
    public void RuleSetLoadedFromJsonConfigDispatchesToShim()
    {
        using var fixture = ExecutionClosureFixture.Create();

        // Author the rule set as a serialized JSON document and drive instrumentation through the
        // configuration's rule-set-loading path, exactly as the build task and CLI do.
        string ruleSetPath = Path.Combine(fixture.SourceDirectory, "clockwork.rules.json");
        File.WriteAllText(ruleSetPath, RuleSetJson.Write(ExecutionClosureFixture.StandardRuleSet()));

        var configuration = new InstrumentationConfiguration { RuleSetPaths = [ruleSetPath] };
        RewriteRuleSet merged = RuleSetMerge.LoadAndMerge(configuration).RuleSet;
        Assert.True(fixture.Instrument(configuration, merged).Succeeded);

        AppRunResult staged = fixture.RunStaged();
        Assert.Equal(0, staged.ExitCode);
        Assert.Contains("app.ticks=999", staged.Output);
    }

    [Fact]
    public void RejectedCallFailsStagedProcessAtRuntime()
    {
        using var fixture = ExecutionClosureFixture.Create(appSource: ExecutionClosureFixture.RejectionAppSource);

        // The uninstrumented application runs the (no-op) real API to completion.
        AppRunResult source = fixture.RunSource();
        Assert.Equal(0, source.ExitCode);
        Assert.Contains("reached-end", source.Output);

        Assert.True(fixture.Instrument().Succeeded);

        // The instrumented application throws deterministically before the rejected call executes.
        AppRunResult staged = fixture.RunStaged();
        Assert.NotEqual(0, staged.ExitCode);
        Assert.DoesNotContain("reached-end", staged.Output);
        Assert.Contains("Rejected", staged.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalRebuildLeavesRunnableClosure()
    {
        using var fixture = ExecutionClosureFixture.Create();

        Assert.False(fixture.Instrument().WasIncrementalHit);

        // A second run with identical inputs is a verified no-op that retains the staged closure.
        InstrumentationResult second = fixture.Instrument();
        Assert.True(second.WasIncrementalHit);

        AppRunResult staged = fixture.RunStaged();
        Assert.Equal(0, staged.ExitCode);
        Assert.Contains("app.ticks=999", staged.Output);
    }

    [Fact]
    public void SignedClosureExecutesAfterIdentityStripping()
    {
        using var fixture = ExecutionClosureFixture.Create(strongName: true);

        InstrumentationResult result = fixture.Instrument();
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        Assert.All(result.Assemblies, assembly => Assert.False(assembly.WasReSigned));
        AppRunResult staged = fixture.RunStaged();
        Assert.Equal(0, staged.ExitCode);
        Assert.Contains("app.ticks=999", staged.Output);
        Assert.Contains("dep.ticks=999", staged.Output);
    }
}
