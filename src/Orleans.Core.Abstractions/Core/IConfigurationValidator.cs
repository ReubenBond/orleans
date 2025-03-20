// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans
{
    /// <summary>
    /// Describes a configuration validator which is called during client and silo initialization.
    /// </summary>
    public interface IConfigurationValidator
    {
        /// <summary>
        /// Validates system configuration and throws an exception if configuration is not valid.
        /// </summary>
        void ValidateConfiguration();
    }
}