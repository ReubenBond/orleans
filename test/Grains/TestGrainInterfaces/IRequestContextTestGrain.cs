namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IRequestContextTestGrain")]
    public interface IRequestContextTestGrain : IGrainWithIntegerKey
    {
        [Alias("TraceIdEcho")]
        Task<string> TraceIdEcho();

        [Alias("TraceIdDoubleEcho")]
        Task<string> TraceIdDoubleEcho();

        [Alias("TraceIdDelayedEcho1")]
        Task<string> TraceIdDelayedEcho1();

        [Alias("TraceIdDelayedEcho2")]
        Task<string> TraceIdDelayedEcho2();

        [Alias("E2EActivityId")]
        Task<Guid> E2EActivityId();
    }

    [Alias("UnitTests.GrainInterfaces.IRequestContextTaskGrain")]
    public interface IRequestContextTaskGrain : IGrainWithIntegerKey
    {
        [Alias("TraceIdEcho")]
        Task<string> TraceIdEcho();

        [Alias("TraceIdDoubleEcho")]
        Task<string> TraceIdDoubleEcho();

        [Alias("TraceIdDelayedEcho1")]
        Task<string> TraceIdDelayedEcho1();

        [Alias("TraceIdDelayedEcho2")]
        Task<string> TraceIdDelayedEcho2();

        [Alias("TraceIdDelayedEchoAwait")]
        Task<string> TraceIdDelayedEchoAwait();

        [Alias("TraceIdDelayedEchoTaskRun")]
        Task<string> TraceIdDelayedEchoTaskRun();

        [Alias("E2EActivityId")]
        Task<Guid> E2EActivityId();

        [Alias("TestRequestContext")]
        Task<Tuple<string, string>> TestRequestContext();
    }

    [Alias("UnitTests.GrainInterfaces.IRequestContextProxyGrain")]
    public interface IRequestContextProxyGrain : IGrainWithIntegerKey
    {
        [Alias("E2EActivityId")]
        Task<Guid> E2EActivityId();
    }
}
