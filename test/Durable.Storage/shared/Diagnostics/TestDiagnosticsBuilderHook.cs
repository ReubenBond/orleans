using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Services;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Registers Clog's test diagnostics with Microsoft.Testing.Platform and installs the debug assertion hook and the local storage failure recorder. The generated
/// entry point calls it because <c>test/Durable.Storage/Directory.Build.props</c> declares it as a <c>TestingPlatformBuilderHook</c>.
/// </summary>
internal static class TestDiagnosticsBuilderHook
{
    public static void AddExtensions(ITestApplicationBuilder builder, string[] arguments)
    {
        _ = arguments;
        DebugAssertions.Install();
        FatalErrors.Install();
        builder.TestHost.AddDataConsumer(static services => new FailureDumpWriter(services.GetConfiguration(), services.GetOutputDevice()));
    }
}
