namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ReplaceArguments")]
    public class ReplaceArguments
    {
        [Orleans.Id(0)]
        public string OldString { get; private set; }
        [Orleans.Id(1)]
        public string NewString { get; private set; }

        public ReplaceArguments(string oldStr, string newStr)
        {
            OldString = oldStr;
            NewString = newStr;
        }
    }

    [Alias("UnitTests.GrainInterfaces.IGeneratorTestDerivedDerivedGrain")]
    public interface IGeneratorTestDerivedDerivedGrain : IGeneratorTestDerivedGrain2
    {
        [Alias("StringNConcat")]
        Task<string> StringNConcat(string[] strArray);
        [Alias("StringReplace")]
        Task<string> StringReplace(ReplaceArguments strs);
    }
}