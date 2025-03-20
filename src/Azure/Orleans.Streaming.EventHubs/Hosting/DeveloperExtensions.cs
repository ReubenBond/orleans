// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Orleans.Hosting.Developer
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use event data generator streams.
        /// </summary>
        public static ISiloBuilder AddEventDataGeneratorStreams(
            this ISiloBuilder builder,
            string name,
            Action<IEventDataGeneratorStreamConfigurator> configure)
        {
            var configurator = new EventDataGeneratorStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}