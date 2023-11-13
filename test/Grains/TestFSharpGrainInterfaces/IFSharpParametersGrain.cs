using UnitTests.FSharpGrains;
using UnitTests.FSharpInterfaces;

[assembly: GenerateCodeForDeclaringAssembly(typeof(Generic1ArgumentGrain<>))]

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IFSharpParametersGrain`2")]
    public interface IFSharpParametersGrain<T,U> : IGrainWithGuidKey, IFSharpParameters<T>
    {
    }
}
