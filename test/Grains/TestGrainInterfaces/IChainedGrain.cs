namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IChainedGrain")]
    public interface IChainedGrain : IGrainWithIntegerKey
    {
        [Alias("GetId")]
        Task<int> GetId();
        [Alias("GetX")]
        Task<int> GetX();
        [Alias("GetNext")]
        Task<IChainedGrain> GetNext();

        //[ReadOnly]
        [Alias("GetCalculatedValue")]
        Task<int> GetCalculatedValue();
        [Alias("SetNext")]
        Task SetNext(IChainedGrain next);
        [Alias("SetNextNested")]
        Task SetNextNested(ChainGrainHolder next);

        //[ReadOnly]
        [Alias("Validate")]
        Task Validate(bool nextIsSet);
        [Alias("PassThis")]
        Task PassThis(IChainedGrain next);
        [Alias("PassNull")]
        Task PassNull(IChainedGrain next);
        [Alias("PassThisNested")]
        Task PassThisNested(ChainGrainHolder next);
        [Alias("PassNullNested")]
        Task PassNullNested(ChainGrainHolder next);
    }
    
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ChainGrainHolder")]
    public class ChainGrainHolder
    {
        [Id(0)]
        public IChainedGrain Next { get; set; }
    }
}
