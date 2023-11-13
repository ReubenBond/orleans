namespace UnitTests.GrainInterfaces
{
    public enum ReturnCode
    {
        OK = 0,
        Fail = 1
    }

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.MemberVariables")]
    public struct MemberVariables
    {
        [Id(0)]
        public byte[] byteArray;
        [Id(1)]
        public string stringVar;
        [Id(2)]
        public ReturnCode code;

        public MemberVariables(byte[] bytes, string str, ReturnCode codeInput)
        {
            byteArray = bytes;
            stringVar = str;
            code = codeInput;
        }
    }

    [Alias("UnitTests.GrainInterfaces.IGeneratorTestGrain")]
    public interface IGeneratorTestGrain : IGrainWithIntegerKey
    {
        [Alias("ByteSet")]
        Task<byte[]> ByteSet(byte[] data);
        [Alias("StringSet")]
        Task StringSet(string str);
        [Alias("StringIsNullOrEmpty")]
        Task<bool> StringIsNullOrEmpty();
        [Alias("GetMemberVariables")]
        Task<MemberVariables> GetMemberVariables();
        [Alias("SetMemberVariables")]
        Task SetMemberVariables(MemberVariables x);

    }
}
