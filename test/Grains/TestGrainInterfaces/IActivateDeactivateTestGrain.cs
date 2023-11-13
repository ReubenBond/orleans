namespace UnitTests.GrainInterfaces
{
    // Note: Self-managed can only implement one grain interface, so have to use copy-paste rather than subclassing 

    [Alias("UnitTests.GrainInterfaces.ISimpleActivateDeactivateTestGrain")]
    public interface ISimpleActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        [Alias("DoSomething")]
        Task<string> DoSomething();
        [Alias("DoDeactivate")]
        Task DoDeactivate();
    }

    [Alias("UnitTests.GrainInterfaces.ITailCallActivateDeactivateTestGrain")]
    public interface ITailCallActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        [Alias("DoSomething")]
        Task<string> DoSomething();
        [Alias("DoDeactivate")]
        Task DoDeactivate();
    }

    [Alias("UnitTests.GrainInterfaces.ILongRunningActivateDeactivateTestGrain")]
    public interface ILongRunningActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        [Alias("DoSomething")]
        Task<string> DoSomething();
        [Alias("DoDeactivate")]
        Task DoDeactivate();
    }

    [Alias("UnitTests.GrainInterfaces.IBadActivateDeactivateTestGrain")]
    public interface IBadActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        [Alias("ThrowSomething")]
        Task ThrowSomething();
        [Alias("GetKey")]
        Task<long> GetKey();
    }

    [Alias("UnitTests.GrainInterfaces.IBadConstructorTestGrain")]
    public interface IBadConstructorTestGrain : IGrainWithIntegerKey
    {
        [Alias("DoSomething")]
        Task<string> DoSomething();
    }

    [Alias("UnitTests.GrainInterfaces.ITaskActionActivateDeactivateTestGrain")]
    public interface ITaskActionActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        [Alias("DoSomething")]
        Task<string> DoSomething();
        [Alias("DoDeactivate")]
        Task DoDeactivate();
    }

    [Alias("UnitTests.GrainInterfaces.ICreateGrainReferenceTestGrain")]
    public interface ICreateGrainReferenceTestGrain : IGrainWithIntegerKey
    {
        [Alias("DoSomething")]
        Task<string> DoSomething();

        [Alias("ForwardCall")]
        Task ForwardCall(IBadActivateDeactivateTestGrain otherGrain);
    }

    [Alias("UnitTests.GrainInterfaces.IDeactivatingWhileActivatingTestGrain")]
    public interface IDeactivatingWhileActivatingTestGrain : IGrainWithIntegerKey
    {
        [Alias("DoSomething")]
        Task<string> DoSomething();
    }
    
}
