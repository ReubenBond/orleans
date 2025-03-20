// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Serialization;

namespace Orleans.Clustering.Redis
{
    /// <summary>
    /// Represents an exception which occurred in the Redis clustering.
    /// </summary>
    [Serializable]
    public class RedisClusteringException : Exception
    {
        /// <inheritdoc/>
        public RedisClusteringException() : base() { }

        /// <inheritdoc/>
        public RedisClusteringException(string message) : base(message) { }

        /// <inheritdoc/>
        public RedisClusteringException(string message, Exception innerException) : base(message, innerException) { }

        /// <inheritdoc/>
        [Obsolete]
        protected RedisClusteringException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}