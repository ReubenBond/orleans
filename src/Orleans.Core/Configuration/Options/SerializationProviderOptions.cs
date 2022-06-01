using System.Collections.Generic;
using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies serialization provider and fallback serializer options.
    /// </summary>
    public class SerializationProviderOptions
    {
        /// <summary>
        /// Externally registered serializers
        /// </summary>
        public List<Type> SerializationProviders { get; set; } = new List<Type>();

        /// <summary>
        /// Serializer used if no serializer is found for a type.
        /// </summary>
        public Type FallbackSerializationProvider { get; set; }

        /// <summary>
        /// The maximum retained size for serialization and deserialization contexts.
        /// </summary>
        /// <remarks>
        /// This should reflect the expected object graph size for messages.
        /// </remarks>
        public int MaxSustainedSerializationContextCapacity { get; set; } = 64;

        /// <summary>
        /// The <see cref="LogLevel"/> to use when logging types serialized by the <see cref="FallbackSerializationProvider"/>.
        /// </summary>
        /// <remarks>
        /// The default value is <see cref="LogLevel.Information"/>.
        /// </remarks>
        public LogLevel FallbackSerializationLogLevel { get; set; } = LogLevel.Information;

        /// <summary>
        /// The <see cref="LogLevel"/> to use when logging <see cref="Exception"/> types serialized by the <see cref="FallbackSerializationProvider"/>.
        /// </summary>
        /// <remarks>
        /// The default value is <see cref="LogLevel.Debug"/>.
        /// </remarks>
        public LogLevel ExceptionFallbackSerializationLogLevel { get; set; } = LogLevel.Debug;
    }
}
