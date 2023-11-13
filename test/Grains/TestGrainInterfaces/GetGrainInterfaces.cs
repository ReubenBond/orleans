namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IBase")]
    public interface IBase : IGrainWithIntegerKey
    {
        [Alias("Foo")]
        Task<bool> Foo();
    }

    [Alias("UnitTests.GrainInterfaces.IDerivedFromBase")]
    public interface IDerivedFromBase : IBase
    {
        [Alias("Bar")]
        Task<bool> Bar();
    }

    [Alias("UnitTests.GrainInterfaces.IBase1")]
    public interface IBase1 : IGrainWithIntegerKey
    {
        [Alias("Foo")]
        Task<bool> Foo();
    }

    [Alias("UnitTests.GrainInterfaces.IBase2")]
    public interface IBase2 : IGrainWithIntegerKey
    {
        [Alias("Bar")]
        Task<bool> Bar();
    }

    [Alias("UnitTests.GrainInterfaces.IBase3")]
    public interface IBase3 : IGrainWithIntegerKey
    {
        [Alias("Foo")]
        Task<bool> Foo();
    }

    [Alias("UnitTests.GrainInterfaces.IBase4")]
    public interface IBase4 : IGrainWithIntegerKey
    {
        [Alias("Foo")]
        Task<bool> Foo();
    }

    [Alias("UnitTests.GrainInterfaces.IStringGrain")]
    public interface IStringGrain : IGrainWithStringKey
    {
        [Alias("Foo")]
        Task<bool> Foo();
    }

    [Alias("UnitTests.GrainInterfaces.IGuidGrain")]
    public interface IGuidGrain : IGrainWithGuidKey
    {
        [Alias("Foo")]
        Task<bool> Foo();
    }
}
