// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[GenerateSerializer]
abstract class Base<T>;

class Derived : Base<int>;

[Trait("Category", "BVT")]
public class GenericBaseClassTest
{
    private readonly ServiceProvider _services;
    private readonly Serializer _serializer;
    public GenericBaseClassTest()
    {
        _services = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        _serializer = _services.GetRequiredService<Serializer>();
    }

    [Fact]
    public void DerivedNoGeneric()
    {
        Assert.False(_serializer.CanSerialize(typeof(Derived)));
    }
}
