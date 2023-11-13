namespace UnitTests.DtosRefOrleans
{
    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.DtosRefOrleans.ClassReferencingOrleansTypeDto")]
    public class ClassReferencingOrleansTypeDto
    {
        static ClassReferencingOrleansTypeDto()
        {
            _ = typeof(IGrain).ToString();
        }

        [Id(0)]
        public string MyProperty { get; set; }
    }
}