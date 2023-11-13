namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IReferenceRecursiveTypeGrain")]
    public interface IReferenceRecursiveTypeGrain : IGrainWithGuidKey
    {
        [Alias("Echo")]
        Task<RecursiveType> Echo(RecursiveType arg);
    }

    /// <summary>
    /// These classes form a repro for https://github.com/dotnet/orleans/issues/5473, which resulted in a
    /// StackOverflowException during code generation.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.RecursiveType")]
    public class RecursiveType : SelfTyped<RecursiveType>
    {
    }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.SelfTyped`1")]
    public abstract class SelfTyped<T> where T : SelfTyped<T>
    {
    }
}
