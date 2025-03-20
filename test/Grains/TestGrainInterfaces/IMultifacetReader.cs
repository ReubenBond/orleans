// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces

{
    public interface IMultifacetReader : IGrainWithIntegerKey
    {
        Task<int> GetValue();
        //event ValueUpdateEventHandler ValueUpdateEvent;
        //event ValueUpdateEventHandler CommonEvent;
    }
}
