namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IGeneratorTestDerivedGrain2")]
    public interface IGeneratorTestDerivedGrain2 : IGeneratorTestGrain
    {
        [Alias("StringConcat")]
        Task<string> StringConcat(string str1, string str2, string str3);
    }
}