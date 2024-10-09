using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    // uncomment the following class to verify correct code generation for #1349
    // (do so once code generation succeeds)
    // NOTE: also uncomment the corresponding test in Tester/GeneratorGrainTests.cs

    public class GeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain : Grain, IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain
    {
        public Task<int> Echo(int value)
        {
            return Task.FromResult(value);
        }

        public Task<Tuple<string, int>> MultipleParameterEcho(string value, int value)
        {
            return Task.FromResult(new Tuple<string,int>(value,value));
        }
    }
}
