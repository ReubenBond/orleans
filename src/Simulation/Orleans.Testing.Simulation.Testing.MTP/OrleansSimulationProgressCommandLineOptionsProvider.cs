using System.Globalization;
using System.Text;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Microsoft.Testing.Platform.Extensions.OutputDevice;
using Microsoft.Testing.Platform.Extensions.TestHost;
using Microsoft.Testing.Platform.Extensions.TestHostControllers;
using Microsoft.Testing.Platform.OutputDevice;
using Microsoft.Testing.Platform.Services;

namespace Orleans.Testing.Simulation.Testing;

/// <summary>Registers Orleans simulation Microsoft Testing Platform command-line options.</summary>
public static class TestingPlatformBuilderHook
{
    /// <summary>Adds Orleans simulation test-runner extensions to the application builder.</summary>
    /// <param name="testApplicationBuilder">The test application builder.</param>
    /// <param name="_">The test application command-line arguments.</param>
    public static void AddExtensions(ITestApplicationBuilder testApplicationBuilder, string[] _)
    {
        ArgumentNullException.ThrowIfNull(testApplicationBuilder);
        testApplicationBuilder.CommandLine.AddProvider(static () => new OrleansSimulationProgressCommandLineOptionsProvider());
        testApplicationBuilder.TestHost.AddTestHostApplicationLifetime(
            static serviceProvider => new OrleansSimulationProgressOutputLifetime(serviceProvider.GetOutputDevice()));
        testApplicationBuilder.TestHostControllers.AddEnvironmentVariableProvider(
            static serviceProvider => new OrleansSimulationProgressEnvironmentVariableProvider(
                serviceProvider.GetCommandLineOptions()));
    }
}

internal sealed class OrleansSimulationProgressCommandLineOptionsProvider : ICommandLineOptionsProvider
{
    internal const string OptionName = "orleans-simulation-progress";

    private static readonly CommandLineOption[] s_options =
    [
        new(
            OptionName,
            "Report live simulation iterations, executed steps, time advances, simulated time, and pending work at this wall-clock interval (for example, 5s).",
            ArgumentArity.ExactlyOne,
            isHidden: false),
    ];

    public string Uid => "OrleansSimulationProgressCommandLineOptionsProvider";

    public string Version => "0.1.0";

    public string DisplayName => "Orleans simulation progress";

    public string Description => "Enables periodic progress output from active Orleans simulation drive loops.";

    public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() => s_options;

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments)
    {
        if (commandOption.Name != OptionName)
        {
            return ValidationResult.ValidTask;
        }

        return arguments is [var value] && SimulationProgressEnvironment.TryParseInterval(value, out _)
            ? ValidationResult.ValidTask
            : ValidationResult.InvalidTask(
                $"--{OptionName} must be followed by a positive duration such as '5s', '500ms', '2m', or '00:00:05'.");
    }

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions)
    {
        if (!commandLineOptions.TryGetOptionArgumentList(OptionName, out string[]? arguments))
        {
            return ValidationResult.ValidTask;
        }

        if (arguments is not [var value] ||
            !SimulationProgressEnvironment.TryParseInterval(value, out TimeSpan interval))
        {
            return ValidationResult.InvalidTask(
                $"--{OptionName} must be followed by a positive duration such as '5s', '500ms', '2m', or '00:00:05'.");
        }

        Environment.SetEnvironmentVariable(
            SimulationProgressEnvironment.Interval,
            interval.ToString("c", CultureInfo.InvariantCulture));
        return ValidationResult.ValidTask;
    }
}

internal sealed class OrleansSimulationProgressEnvironmentVariableProvider : ITestHostEnvironmentVariableProvider
{
    private readonly string? _value;
    private readonly string? _validationError;

    public OrleansSimulationProgressEnvironmentVariableProvider(ICommandLineOptions commandLineOptions)
    {
        ArgumentNullException.ThrowIfNull(commandLineOptions);

        string? value = commandLineOptions.TryGetOptionArgumentList(
            OrleansSimulationProgressCommandLineOptionsProvider.OptionName,
            out string[]? arguments)
            ? arguments is [var argument] ? argument : null
            : Environment.GetEnvironmentVariable(SimulationProgressEnvironment.Interval);

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _value = value;
        if (!SimulationProgressEnvironment.TryParseInterval(value, out _))
        {
            _validationError =
                $"{SimulationProgressEnvironment.Interval} must be a positive duration such as " +
                $"'5s', '500ms', '2m', or '00:00:05', not '{value}'.";
        }
    }

    public string Uid => "OrleansSimulationProgressEnvironmentVariableProvider";

    public string Version => "0.1.0";

    public string DisplayName => "Orleans simulation progress environment";

    public string Description => "Forwards Orleans simulation progress configuration to orchestrated test hosts.";

    public Task<bool> IsEnabledAsync() => Task.FromResult(_value is not null);

    public Task UpdateAsync(IEnvironmentVariables environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        if (_value is not null)
        {
            environmentVariables.SetVariable(new EnvironmentVariable(
                SimulationProgressEnvironment.Interval,
                _value,
                isSecret: false,
                isLocked: true));
        }

        return Task.CompletedTask;
    }

    public Task<ValidationResult> ValidateTestHostEnvironmentVariablesAsync(
        IReadOnlyEnvironmentVariables environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        if (_validationError is not null)
        {
            return ValidationResult.InvalidTask(_validationError);
        }

        if (_value is null)
        {
            return ValidationResult.ValidTask;
        }

        return environmentVariables.TryGetVariable(
                SimulationProgressEnvironment.Interval,
                out OwnedEnvironmentVariable? configured) &&
            configured.Value == _value
            ? ValidationResult.ValidTask
            : ValidationResult.InvalidTask(
                $"Unable to pass {SimulationProgressEnvironment.Interval} to the test host.");
    }
}

internal sealed class OrleansSimulationProgressOutputLifetime :
    ITestHostApplicationLifetime,
    IOutputDeviceDataProducer,
    IDisposable
{
    private readonly TextWriter _writer;

    public OrleansSimulationProgressOutputLifetime(IOutputDevice outputDevice)
    {
        ArgumentNullException.ThrowIfNull(outputDevice);
        _writer = new OutputDeviceTextWriter(outputDevice, this);
    }

    public string Uid => "OrleansSimulationProgressOutputLifetime";

    public string Version => "0.1.0";

    public string DisplayName => "Orleans simulation progress output";

    public string Description => "Routes Orleans simulation progress through the Microsoft Testing Platform output device.";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public Task BeforeRunAsync(CancellationToken cancellationToken)
    {
        SimulationProgressOutput.SetWriter(_writer);
        return Task.CompletedTask;
    }

    public Task AfterRunAsync(int exitCode, CancellationToken cancellationToken)
    {
        SimulationProgressOutput.SetWriter(null);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        SimulationProgressOutput.SetWriter(null);
        _writer.Dispose();
    }

    private sealed class OutputDeviceTextWriter(
        IOutputDevice outputDevice,
        IOutputDeviceDataProducer producer) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) =>
            outputDevice.DisplayAsync(
                producer,
                new TextOutputDeviceData(value ?? string.Empty),
                CancellationToken.None).GetAwaiter().GetResult();
    }
}
