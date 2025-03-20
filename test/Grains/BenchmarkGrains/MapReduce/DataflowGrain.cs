// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkGrainInterfaces.MapReduce;

namespace BenchmarkGrains.MapReduce
{
    public abstract class DataflowGrain : Grain, IDataflowGrain
    {
        public Task Complete()
        {
            throw new NotImplementedException();
        }

        public Task Fault()
        {
            throw new NotImplementedException();
        }

        public Task Completion()
        {
            throw new NotImplementedException();
        }
    }
}