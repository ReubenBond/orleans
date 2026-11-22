using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Testing.Platform.Configurations;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.OutputDevice;
using Microsoft.Testing.Platform.OutputDevice;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Writes a process dump of the test host when a test fails, so the state behind the failure can be inspected
/// (docs/spec/verification.md). <c>CLOG_TEST_FAILURE_DUMPS</c> bounds the dumps per process (default 1; 0 disables them) and
/// <c>CLOG_TEST_FAILURE_DUMP_TYPE</c> selects the dump type (default <c>WithHeap</c>).
/// </summary>
/// <remarks>
/// xUnit decides a test's outcome after its last extension point has run, so the platform's result message is the
/// earliest place which knows a test failed. Dumping never changes a result: a failure to dump is reported and the
/// run continues.
/// </remarks>
internal sealed class FailureDumpWriter : IDataConsumer, IOutputDeviceDataProducer
{
    internal const string LimitVariable = "CLOG_TEST_FAILURE_DUMPS";
    internal const string TypeVariable = "CLOG_TEST_FAILURE_DUMP_TYPE";

    private readonly IOutputDevice _output;
    private readonly int _limit;
    private readonly DumpType _type;
    private int _written;

    public FailureDumpWriter(IConfiguration configuration, IOutputDevice output)
    {
        TestDiagnostics.ResultsDirectory = configuration.GetTestResultDirectory();
        _output = output;
        _limit = Environment.GetEnvironmentVariable(LimitVariable) is { Length: > 0 } limit
            ? int.Parse(limit, NumberStyles.None, CultureInfo.InvariantCulture)
            : 1;
        _type = Environment.GetEnvironmentVariable(TypeVariable) is { Length: > 0 } type
            ? Enum.Parse<DumpType>(type, ignoreCase: true)
            : DumpType.WithHeap;
    }

    public Type[] DataTypesConsumed { get; } = [typeof(TestNodeUpdateMessage)];

    public string Uid => nameof(FailureDumpWriter);

    public string Version => "1.0.0";

    public string DisplayName => "Clog failure dumps";

    public string Description => "Writes a process dump of the test host when a test fails.";

    public Task<bool> IsEnabledAsync() => Task.FromResult(_limit > 0);

    public async Task ConsumeAsync(IDataProducer dataProducer, IData value, CancellationToken cancellationToken)
    {
        // The platform delivers each consumer's messages one at a time, so the count needs no synchronization.
        if (value is not TestNodeUpdateMessage { TestNode: var test } || _written == _limit || !Failed(test.Properties))
        {
            return;
        }

        _written++;
        var path = TestDiagnostics.PathFor(test.DisplayName, string.Create(CultureInfo.InvariantCulture, $".{Environment.ProcessId}.failure.dmp"));
        string message;
        try
        {
            await new DiagnosticsClient(Environment.ProcessId).WriteDumpAsync(_type, path, logDumpGeneration: false, cancellationToken);
            message = $"Failure dump ({_type}) for '{test.DisplayName}': {path}";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            message = $"Failed to write a failure dump for '{test.DisplayName}' to {path}: {error.Message}";
        }

        if (_written == _limit)
        {
            message += string.Create(CultureInfo.InvariantCulture, $" (limit of {_limit} reached; set {LimitVariable} to write more)");
        }

        await _output.DisplayAsync(this, new WarningMessageOutputDeviceData(message), cancellationToken);
    }

    private static bool Failed(PropertyBag properties) =>
        properties.Any<FailedTestNodeStateProperty>()
        || properties.Any<ErrorTestNodeStateProperty>()
        || properties.Any<TimeoutTestNodeStateProperty>();
}
