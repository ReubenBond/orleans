// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

internal interface ISerializerPresenceTest : IGrainWithGuidKey
{
    Task<bool> SerializerExistsForType(System.Type param);

    Task TakeSerializedData(object data);
}
