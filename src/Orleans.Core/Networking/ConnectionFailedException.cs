using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Indicates that a connection failed.
    /// </summary>
    public class ConnectionFailedException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionFailedException"/> class.
        /// </summary>
        public ConnectionFailedException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionFailedException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public ConnectionFailedException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionFailedException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public ConnectionFailedException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionFailedException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.")]
        protected ConnectionFailedException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
