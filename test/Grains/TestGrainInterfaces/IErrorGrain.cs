namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IErrorGrain")]
    public interface IErrorGrain : ISimpleGrain
    {
        [Alias("LogMessage")]
        Task LogMessage(string msg);
        [Alias("SetAError")]
        Task SetAError(int a);
        [Alias("SetBError")]
        Task SetBError(int a);
        [Alias("GetAxBError")]
        Task<int> GetAxBError();
        [Alias("GetAxBError1")]
        Task<int> GetAxBError(int a, int b);
        [Alias("LongMethod")]
        Task LongMethod(int waitTime);
        [Alias("LongMethodWithError")]
        Task LongMethodWithError(int waitTime);
        [Alias("DelayMethod")]
        Task DelayMethod(int milliseconds);
        [Alias("Dispose")]
        Task Dispose();
        [Alias("UnobservedErrorImmediate")]
        Task<int> UnobservedErrorImmediate();
        [Alias("UnobservedErrorDelayed")]
        Task<int> UnobservedErrorDelayed();
        [Alias("UnobservedErrorContinuation2")]
        Task<int> UnobservedErrorContinuation2();
        [Alias("UnobservedErrorContinuation3")]
        Task<int> UnobservedErrorContinuation3();
        [Alias("UnobservedIgnoredError")]
        Task<int> UnobservedIgnoredError();
        [Alias("AddChildren")]
        Task AddChildren(List<IErrorGrain> children);
        [Alias("ExecuteDelayed")]
        Task<bool> ExecuteDelayed(TimeSpan delay);
    }
}
