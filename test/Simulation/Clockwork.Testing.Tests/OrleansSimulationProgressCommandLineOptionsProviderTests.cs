using System.Diagnostics.CodeAnalysis;
using Orleans.Testing.Simulation.Testing;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Microsoft.Testing.Platform.Extensions.TestHostControllers;

namespace Orleans.Testing.Simulation.Testing.Tests;

[Collection("Orleans simulation progress environment")]
public sealed class OrleansSimulationProgressCommandLineOptionsProviderTests
{
    [Fact]
    public void ExposesOrleansSimulationProgressOption()
    {
        var provider = new OrleansSimulationProgressCommandLineOptionsProvider();

        CommandLineOption option = Assert.Single(provider.GetCommandLineOptions());

        Assert.Equal("orleans-simulation-progress", option.Name);
        Assert.Equal(ArgumentArity.ExactlyOne, option.Arity);
        Assert.False(option.IsHidden);
        Assert.Equal(
            "ORLEANS_SIMULATION_PROGRESS_INTERVAL",
            SimulationProgressEnvironment.Interval);
    }

    [Theory]
    [InlineData("5s", true)]
    [InlineData("500ms", true)]
    [InlineData("00:00:05", true)]
    [InlineData("0s", false)]
    [InlineData("invalid", false)]
    public async Task ValidatesProgressInterval(string value, bool expectedValid)
    {
        var provider = new OrleansSimulationProgressCommandLineOptionsProvider();
        CommandLineOption option = Assert.Single(provider.GetCommandLineOptions());

        var result = await provider.ValidateOptionArgumentsAsync(option, [value]);

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public async Task CommandLineIntervalOverridesEnvironmentForTheTestProcess()
    {
        string? previous = Environment.GetEnvironmentVariable(SimulationProgressEnvironment.Interval);
        try
        {
            Environment.SetEnvironmentVariable(SimulationProgressEnvironment.Interval, "1s");
            var provider = new OrleansSimulationProgressCommandLineOptionsProvider();
            var options = new StubCommandLineOptions("orleans-simulation-progress", "5s");

            var result = await provider.ValidateCommandLineOptionsAsync(options);

            Assert.True(result.IsValid);
            Assert.Equal(
                "00:00:05",
                Environment.GetEnvironmentVariable(SimulationProgressEnvironment.Interval));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SimulationProgressEnvironment.Interval, previous);
        }
    }

    [Fact]
    public async Task ForwardsCommandLineIntervalToAnOrchestratedTestHost()
    {
        var provider = new OrleansSimulationProgressEnvironmentVariableProvider(
            new StubCommandLineOptions("orleans-simulation-progress", "5s"));
        var environment = new StubEnvironmentVariables(provider);

        await provider.UpdateAsync(environment);
        ValidationResult result = await provider.ValidateTestHostEnvironmentVariablesAsync(environment);

        Assert.True(await provider.IsEnabledAsync());
        Assert.True(result.IsValid);
        Assert.Equal("5s", environment.Value);
    }

    private sealed class StubCommandLineOptions(string name, string value) : ICommandLineOptions
    {
        public bool IsOptionSet(string optionName) => optionName == name;

        public bool TryGetOptionArgumentList(string optionName, [NotNullWhen(true)] out string[]? arguments)
        {
            arguments = optionName == name ? [value] : null;
            return arguments is not null;
        }
    }

    private sealed class StubEnvironmentVariables(IExtension owner) : IEnvironmentVariables
    {
        public string? Value { get; private set; }

        public void SetVariable(EnvironmentVariable environmentVariable) => Value = environmentVariable.Value;

        public void RemoveVariable(string variable) => Value = null;

        public bool TryGetVariable(
            string variable,
            [NotNullWhen(true)] out OwnedEnvironmentVariable? environmentVariable)
        {
            environmentVariable = Value is null
                ? null
                : new OwnedEnvironmentVariable(owner, variable, Value, isSecret: false, isLocked: true);
            return environmentVariable is not null;
        }
    }
}

[CollectionDefinition("Orleans simulation progress environment", DisableParallelization = true)]
public sealed class OrleansSimulationProgressEnvironmentGroup;
