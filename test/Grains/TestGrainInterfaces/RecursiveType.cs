// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IReferenceRecursiveTypeGrain : IGrainWithGuidKey
    {
        Task<RecursiveType> Echo(RecursiveType arg);
    }

    /// <summary>
    /// These classes form a repro for https://github.com/dotnet/orleans/issues/5473, which resulted in a
    /// StackOverflowException during code generation.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class RecursiveType : SelfTyped<RecursiveType>
    {
    }

    [GenerateSerializer]
    public abstract class SelfTyped<T> where T : SelfTyped<T>
    {
    }
}
