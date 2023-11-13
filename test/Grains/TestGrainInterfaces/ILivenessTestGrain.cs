namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ILivenessTestGrain")]
    public interface ILivenessTestGrain : IGrainWithIntegerKey
    {
        // separate label that can be set
        [Alias("GetLabel")]
        Task<string> GetLabel();

        [Alias("SetLabel")]
        Task SetLabel(string label);

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();

        [Alias("GetUniqueId")]
        Task<string> GetUniqueId();

        [Alias("GetGrainReference")]
        Task<ILivenessTestGrain> GetGrainReference();

        [Alias("StartTimer")]
        Task StartTimer();

    }
}
