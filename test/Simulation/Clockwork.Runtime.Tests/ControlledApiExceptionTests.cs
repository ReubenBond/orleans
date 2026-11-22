namespace Orleans.Testing.Simulation.Runtime.Tests;

public sealed class ControlledApiExceptionTests
{
    [Fact]
    public void PreservesStructuredFailureDetails()
    {
        var exception = new SimulationApiException(
            SimulationApiCategory.ThreadPool,
            "System.Threading.ThreadPool.UnsafeQueueNativeOverlapped",
            "native I/O completion is not deterministic.");

        Assert.Equal(SimulationApiCategory.ThreadPool, exception.Category);
        Assert.Equal("System.Threading.ThreadPool.UnsafeQueueNativeOverlapped", exception.ApiName);
        Assert.Equal("native I/O completion is not deterministic.", exception.Reason);
        Assert.Contains("thread-pool", exception.Message, StringComparison.Ordinal);
    }
}
