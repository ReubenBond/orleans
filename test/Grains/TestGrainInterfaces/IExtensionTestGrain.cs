namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IExtensionTestGrain")]
    public interface IExtensionTestGrain : IGrainWithIntegerKey
    {
        [Alias("InstallExtension")]
        Task InstallExtension(string name);
    }

    [Alias("UnitTests.GrainInterfaces.IGenericExtensionTestGrain`1")]
    public interface IGenericExtensionTestGrain<in T> : IGrainWithIntegerKey
    {
        [Alias("InstallExtension")]
        Task InstallExtension(T name);
    }

    [Alias("UnitTests.GrainInterfaces.IGenericGrainWithNonGenericExtension`1")]
    public interface IGenericGrainWithNonGenericExtension<in T> : IGrainWithIntegerKey
    {
        [Alias("DoSomething")]
        Task DoSomething();
    }

    [Alias("UnitTests.GrainInterfaces.INoOpTestGrain")]
    public interface INoOpTestGrain : IGrainWithIntegerKey
    {
    }
}