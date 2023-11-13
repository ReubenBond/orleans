using UnitTests.Interfaces;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IGeneratorTestDerivedFromCSharpInterfaceInExternalAssemblyGrain")]
    public interface IGeneratorTestDerivedFromCSharpInterfaceInExternalAssemblyGrain : IGrainWithGuidKey, ICSharpBaseInterface
    {
    }
}
