namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.TestTypeA")]
    public class TestTypeA
    {
        [Orleans.Id(0)]
        public ICollection<TestTypeA> Collection { get; set; }
    }
}
