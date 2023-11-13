using TestGrainInterfaces;

namespace TestGrains
{
    // We list all the events supported by the JournaledPersonGrain 

    // we chose to have all these events implement the following marker interface
    // (this is optional, but gives us a bit more typechecking)
    public interface IPersonEvent { } 

    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("TestGrains.PersonRegistered")]
    public class PersonRegistered : IPersonEvent
    {
        [Orleans.Id(0)]
        public string FirstName { get; set; }
        [Orleans.Id(1)]
        public string LastName { get; set; }
        [Orleans.Id(2)]
        public GenderType Gender { get; set; }

        public PersonRegistered(string firstName, string lastName, GenderType gender)
        {
            FirstName = firstName;
            LastName = lastName;
            Gender = gender;
        }
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("TestGrains.PersonMarried")]
    public class PersonMarried : IPersonEvent
    {
        [Orleans.Id(0)]
        public Guid SpouseId { get; set; }
        [Orleans.Id(1)]
        public string SpouseFirstName { get; set; }
        [Orleans.Id(2)]
        public string SpouseLastName { get; set; }
        
        public PersonMarried(Guid spouseId, string spouseFirstName, string spouseLastName)
        {
            SpouseId = spouseId;
            SpouseFirstName = spouseFirstName;
            SpouseLastName = spouseLastName;
        }
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("TestGrains.PersonLastNameChanged")]
    public class PersonLastNameChanged : IPersonEvent
    {
        [Orleans.Id(0)]
        public string LastName { get; set; }

        public PersonLastNameChanged(string lastName)
        {
            LastName = lastName;
        }
    }
}
