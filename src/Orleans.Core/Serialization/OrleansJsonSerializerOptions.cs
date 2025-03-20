// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Orleans.Serialization;

public class OrleansJsonSerializerOptions
{
    public JsonSerializerSettings JsonSerializerSettings { get; set; }

    public OrleansJsonSerializerOptions()
    {
        JsonSerializerSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings();
    }
}

public class ConfigureOrleansJsonSerializerOptions : IPostConfigureOptions<OrleansJsonSerializerOptions>
{
    private readonly IServiceProvider _serviceProvider;

    public ConfigureOrleansJsonSerializerOptions(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void PostConfigure(string name, OrleansJsonSerializerOptions options)
    {
        OrleansJsonSerializerSettings.Configure(_serviceProvider, options.JsonSerializerSettings);
    }
}
