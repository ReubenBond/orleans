namespace TestGrainInterfaces
{
    public enum GenderType
    {
        Male,
        Female
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("TestGrainInterfaces.PersonAttributes")]
    public class PersonAttributes
    {
        [Orleans.Id(0)]
        public string FirstName { get; set; }
        [Orleans.Id(1)]
        public string LastName { get; set; }
        [Orleans.Id(2)]
        public GenderType Gender { get; set; }
    }

    /// <summary>
    /// Orleans grain communication interface IPerson
    /// </summary>
    [Alias("TestGrainInterfaces.IPersonGrain")]
    public interface IPersonGrain : Orleans.IGrainWithGuidKey
    {
        [Alias("RegisterBirth")]
        Task RegisterBirth(PersonAttributes person);
        [Alias("Marry")]
        Task Marry(IPersonGrain spouse);

        [Alias("GetTentativePersonalAttributes")]
        Task<PersonAttributes> GetTentativePersonalAttributes();

        // Tests

        [Alias("RunTentativeConfirmedStateTest")]
        Task RunTentativeConfirmedStateTest();
    }
}
