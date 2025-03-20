// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Providers
{
    /// <summary>
    /// Constant values used by providers.
    /// </summary>
    public static class ProviderConstants
    {
        /// <summary>
        /// The default storage provider name.
        /// </summary>
        public const string DEFAULT_STORAGE_PROVIDER_NAME = "Default";

        /// <summary>
        /// The default log consistency provider name.
        /// </summary>
        public const string DEFAULT_LOG_CONSISTENCY_PROVIDER_NAME = "Default";

        public const string DEFAULT_PUBSUB_PROVIDER_NAME = "PubSubStore";
    }
}
