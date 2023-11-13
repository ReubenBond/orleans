using System.Collections.Specialized;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.EnumClass")]
    public class EnumClass
    {
        [Id(0)]
        public IEnumerable<DateTimeKind> EnumsList { get; set; }
    }

    [Alias("UnitTests.GrainInterfaces.IExternalTypeGrain")]
    public interface IExternalTypeGrain : IGrainWithIntegerKey
    {
        [Alias("GetAbstractModel")]
        Task GetAbstractModel(IEnumerable<NameObjectCollectionBase> list);

        [Alias("GetEnumModel")]
        Task<EnumClass> GetEnumModel();
    }
}
