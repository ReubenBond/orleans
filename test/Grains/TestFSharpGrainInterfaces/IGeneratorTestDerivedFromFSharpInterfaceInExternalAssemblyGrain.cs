using UnitTests.FSharpInterfaces;

namespace UnitTests.GrainInterfaces
{
    // uncomment the following interface definition to reproduce #1349

    [Alias("UnitTests.GrainInterfaces.IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain")]
    public interface IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain : IGrainWithGuidKey, IFSharpBaseInterface
    {
    }
}
