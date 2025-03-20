// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime.Hosting.ProviderConfiguration;
using Orleans.Storage;

[assembly: RegisterProvider("Memory", "GrainStorage", "Silo", typeof(MemoryGrainStorageProviderBuilder))]

namespace Orleans.Runtime.Hosting.ProviderConfiguration;

internal sealed class MemoryGrainStorageProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddMemoryGrainStorage(name, (OptionsBuilder<MemoryGrainStorageOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            if (int.TryParse(configurationSection[nameof(options.NumStorageGrains)], out var nsg))
            {
                options.NumStorageGrains = nsg;
            }

            var serializerKey = configurationSection["SerializerKey"];
            if (!string.IsNullOrEmpty(serializerKey))
            {
                options.GrainStorageSerializer = services.GetRequiredKeyedService<IGrainStorageSerializer>(serializerKey);
            }
        }));
    }
}
