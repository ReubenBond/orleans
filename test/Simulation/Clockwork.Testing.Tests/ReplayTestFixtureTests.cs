using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Testing;

namespace Orleans.Testing.Simulation.Testing.Tests;

[Collection("Replay test environment")]
public sealed class ReplayTestFixtureTests : IDisposable
{
    private static readonly string[] EnvironmentVariables =
    [
        ReplayTestEnvironment.Artifact,
        ReplayTestEnvironment.SimulationSeed,
        ReplayTestEnvironment.ScheduleSeed,
        ReplayTestEnvironment.ArtifactDirectory,
        ReplayTestEnvironment.ExplorationIterations,
        ReplayTestEnvironment.ExplorationTimeLimit,
        ReplayTestEnvironment.ExplorationMaxFailures,
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "clockwork-testing",
        Guid.NewGuid().ToString("n"));
    private readonly IReadOnlyDictionary<string, string?> _previousEnvironment;

    public ReplayTestFixtureTests()
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string name in EnvironmentVariables)
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        _previousEnvironment = previous;
    }

    [Fact]
    public void EnvironmentVariableNamesUseOrleansSimulationPrefix()
    {
        Assert.Equal("ORLEANS_SIMULATION_REPLAY_ARTIFACT", ReplayTestEnvironment.Artifact);
        Assert.Equal("ORLEANS_SIMULATION_SEED", ReplayTestEnvironment.SimulationSeed);
        Assert.Equal("ORLEANS_SIMULATION_SCHEDULE_SEED", ReplayTestEnvironment.ScheduleSeed);
        Assert.Equal("ORLEANS_SIMULATION_ARTIFACT_DIRECTORY", ReplayTestEnvironment.ArtifactDirectory);
        Assert.Equal("ORLEANS_SIMULATION_EXPLORATION_ITERATIONS", ReplayTestEnvironment.ExplorationIterations);
        Assert.Equal("ORLEANS_SIMULATION_EXPLORATION_TIME_LIMIT", ReplayTestEnvironment.ExplorationTimeLimit);
        Assert.Equal("ORLEANS_SIMULATION_EXPLORATION_MAX_FAILURES", ReplayTestEnvironment.ExplorationMaxFailures);
        Assert.All(EnvironmentVariables, static name =>
            Assert.StartsWith("ORLEANS_SIMULATION_", name, StringComparison.Ordinal));
    }

    [Fact]
    public void ReusesStableSimulationTestIdentitySeed()
    {
        var fixture = new ReplayTestFixture(new ReplayTestConfiguration
        {
            TestClassName = "TestClass",
            TestMethodName = "TestMethod",
        });

        Assert.Equal(322282660, fixture.TestIdentitySeed);
    }

    [Fact]
    public void FailedTestWritesArtifactAndReplayInstructions()
    {
        ReplayTestResult result = CreateFixture("WritesArtifact").Run(
            static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.NotNull(result.ArtifactPath);
        Assert.True(File.Exists(result.ArtifactPath));
        Assert.Contains(ReplayTestEnvironment.Artifact, result.ToFailureMessage(), StringComparison.Ordinal);
        Assert.Contains("Orleans simulation test outcome", result.ToFailureMessage(), StringComparison.Ordinal);
        Assert.Contains(
            "dotnet orleans-simulation replay",
            result.GetReplayCommand("tests.dll", "Tests.Scenario"),
            StringComparison.Ordinal);
        ReplayTestFailureException exception = Assert.Throws<ReplayTestFailureException>(result.ThrowIfFailed);
        Assert.Same(result, exception.Result);
    }

    [Fact]
    public void SuccessfulTestDoesNotWriteArtifactByDefault()
    {
        ReplayTestResult result = CreateFixture("Success").Run(
            static scheduler => scheduler.Schedule("complete", static () => { }),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful);
        Assert.Null(result.ArtifactPath);
    }

    [Fact]
    public void EnvironmentArtifactReplaysExactFailure()
    {
        ReplayTestFixture fixture = CreateFixture("EnvironmentReplay");
        ReplayTestResult recorded = fixture.Run(
            static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
            TestContext.Current.CancellationToken);
        string? previous = Environment.GetEnvironmentVariable(ReplayTestEnvironment.Artifact);
        try
        {
            Environment.SetEnvironmentVariable(ReplayTestEnvironment.Artifact, recorded.ArtifactPath);

            ReplayTestResult replayed = fixture.Run(
                static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
                TestContext.Current.CancellationToken);

            Assert.True(replayed.Execution.Reproduced);
            Assert.Equal(recorded.ArtifactPath, replayed.ArtifactPath);
            Assert.Equal(recorded.SimulationSeed, replayed.SimulationSeed);
            Assert.Equal(recorded.ScheduleSeed, replayed.ScheduleSeed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReplayTestEnvironment.Artifact, previous);
        }
    }

    [Fact]
    public void ExplorationKeepsSimulationSeedAndAdvancesScheduleSeeds()
    {
        var fixture = new ReplayTestFixture(new ReplayTestConfiguration
        {
            TestClassName = nameof(ReplayTestFixtureTests),
            TestMethodName = nameof(ExplorationKeepsSimulationSeedAndAdvancesScheduleSeeds),
            SimulationSeed = 777,
            ScheduleSeed = 100,
            ArtifactDirectory = _root,
        });
        var registrations = 0;

        ReplayTestCampaignResult result = fixture.Explore(
            scheduler =>
            {
                registrations++;
                scheduler.Schedule("complete", static () => { });
            },
            new ReplayTestCampaignOptions
            {
                MaxIterations = 4,
                MaxFailures = 1,
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful);
        Assert.Equal(4, registrations);
        Assert.Equal(777, result.SimulationSeed);
        Assert.Equal(
            [100, 101, 102, 103],
            result.Exploration.Iterations.Select(static iteration => iteration.ScheduleSeed));
        Assert.All(
            result.Exploration.Iterations,
            static iteration => Assert.Equal(777, iteration.Execution.Artifact.SimulationSeed));
        Assert.Empty(result.ArtifactPaths);
    }

    [Fact]
    public void ExplorationStopsAtFailureLimitAndRetainsOneReplayArtifactPerFailure()
    {
        ReplayTestCampaignResult result = CreateFixture("CampaignFailure").Explore(
            static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
            new ReplayTestCampaignOptions
            {
                MaxIterations = 10,
                MaxFailures = 2,
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(2, result.Exploration.Iterations.Count);
        Assert.Equal(2, result.Exploration.FailureCount);
        Assert.Equal(Orleans.Testing.Simulation.Runtime.Exploration.ExplorationTerminationReason.FailureLimit, result.Exploration.TerminationReason);
        string path = Assert.Single(result.RetainedFailureArtifactPaths).Value;
        Assert.True(File.Exists(path));
        Assert.Single(result.ArtifactPaths);
        ReplayTestCampaignFailureException exception =
            Assert.Throws<ReplayTestCampaignFailureException>(result.ThrowIfFailed);
        Assert.Same(result, exception.Result);
    }

    [Fact]
    public void ExplicitExplorationCountOverridesEnvironmentCount()
    {
        WithEnvironmentVariable(
            ReplayTestEnvironment.ExplorationIterations,
            "3",
            () =>
            {
                ReplayTestFixture fixture = CreateFixture("EnvironmentCount");
                ReplayTestCampaignResult fromEnvironment = fixture.Explore(
                    static scheduler => scheduler.Schedule("complete", static () => { }),
                    TestContext.Current.CancellationToken);
                ReplayTestCampaignResult explicitResult = fixture.Explore(
                    static scheduler => scheduler.Schedule("complete", static () => { }),
                    new ReplayTestCampaignOptions { MaxIterations = 2 },
                    TestContext.Current.CancellationToken);

                Assert.Equal(3, fromEnvironment.Exploration.Iterations.Count);
                Assert.Equal(2, explicitResult.Exploration.Iterations.Count);
            });
    }

    [Fact]
    public void ExplorationTimeLimitEnvironmentCanBeTheOnlyRunBound()
    {
        WithEnvironmentVariable(
            ReplayTestEnvironment.ExplorationTimeLimit,
            TimeSpan.FromTicks(1).ToString("c", CultureInfo.InvariantCulture),
            () =>
            {
                ReplayTestCampaignResult result = CreateFixture("EnvironmentTimeLimit").Explore(
                    static scheduler => scheduler.Schedule("complete", static () => { }),
                    TestContext.Current.CancellationToken);

                Assert.Equal(ExplorationTerminationReason.TimeLimit, result.Exploration.TerminationReason);
                Assert.Empty(result.Exploration.Iterations);
            });
    }

    [Fact]
    public void ExplorationMaxFailuresEnvironmentControlsStopping()
    {
        WithEnvironmentVariable(
            ReplayTestEnvironment.ExplorationMaxFailures,
            "3",
            () =>
            {
                ReplayTestCampaignResult result = CreateFixture("EnvironmentFailureLimit").Explore(
                    static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
                    new ReplayTestCampaignOptions { MaxIterations = 10 },
                    TestContext.Current.CancellationToken);

                Assert.Equal(3, result.Exploration.FailureCount);
                Assert.Equal(ExplorationTerminationReason.FailureLimit, result.Exploration.TerminationReason);
            });
    }

    [Fact]
    public void SimulationSeedEnvironmentOverridesTestIdentitySeed()
    {
        WithEnvironmentVariable(
            ReplayTestEnvironment.SimulationSeed,
            "12345",
            () =>
            {
                ReplayTestResult result = CreateFixture("EnvironmentSimulationSeed").Run(
                    static scheduler => scheduler.Schedule("complete", static () => { }),
                    TestContext.Current.CancellationToken);

                Assert.Equal(12345, result.SimulationSeed);
                Assert.Equal(12345, result.Execution.Artifact.SimulationSeed);
            });
    }

    [Fact]
    public void ExplorationArtifactReplayRunsExactlyOnceAndRejectsCampaignLimits()
    {
        ReplayTestFixture fixture = CreateFixture("CampaignReplay");
        ReplayTestResult recorded = fixture.Run(
            static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
            TestContext.Current.CancellationToken);

        WithEnvironmentVariable(
            ReplayTestEnvironment.Artifact,
            recorded.ArtifactPath,
            () =>
            {
                ReplayTestCampaignResult replayed = fixture.Explore(
                    static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
                    TestContext.Current.CancellationToken);

                ScheduleExplorationIteration iteration = Assert.Single(replayed.Exploration.Iterations);
                Assert.True(iteration.Execution.Reproduced);
                Assert.Single(replayed.ArtifactPaths);
                Assert.Throws<InvalidOperationException>(
                    () => fixture.Explore(
                        static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
                        new ReplayTestCampaignOptions { MaxIterations = 2 },
                        TestContext.Current.CancellationToken));
                WithEnvironmentVariable(
                    ReplayTestEnvironment.ExplorationIterations,
                    "2",
                    () => Assert.Throws<InvalidOperationException>(
                        () => fixture.Explore(
                            static scheduler => scheduler.Schedule("fault", static () => throw new KnownTestFailure()),
                            TestContext.Current.CancellationToken)));
            });
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ReplayTestFixture CreateFixture(string method) => new(new ReplayTestConfiguration
    {
        TestClassName = nameof(ReplayTestFixtureTests),
        TestMethodName = method,
        ArtifactDirectory = _root,
    });

    private static void WithEnvironmentVariable(string name, string? value, Action action)
    {
        string? previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    private sealed class KnownTestFailure : Exception;
}

[CollectionDefinition("Replay test environment", DisableParallelization = true)]
public sealed class ReplayTestEnvironmentGroup;
