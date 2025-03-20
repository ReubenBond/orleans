// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MessagePack;
using ProtoBuf;

namespace Benchmarks.Serialization.Models;

[MessagePackObject]
[ProtoContract]
[GenerateSerializer]
public struct MyVector3
{
    [Key(0)]
    [ProtoMember(1)]
    [Id(0)]
    public float X;

    [Key(1)]
    [ProtoMember(2)]
    [Id(1)]
    public float Y;

    [Key(2)]
    [ProtoMember(3)]
    [Id(2)]
    public float Z;
}

[Immutable]
[GenerateSerializer]
public struct ImmutableVector3
{
    [Id(0)]
    public float X;

    [Id(1)]
    public float Y;

    [Id(2)]
    public float Z;
}
