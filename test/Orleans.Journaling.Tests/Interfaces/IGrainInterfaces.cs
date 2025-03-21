namespace Orleans.Journaling.Tests;

public interface ITestDurableGrain : IGrainWithGuidKey
{
    Task SetTestValues(string name, int counter);
    Task<string> GetName();
    Task<int> GetCounter();
}

public interface ITestDurableGrainWithComplexState : IGrainWithGuidKey
{
    Task SetTestValues(TestPerson person, List<string> items);
    Task<TestPerson> GetPerson();
    Task<IReadOnlyList<string>> GetItems();
}
