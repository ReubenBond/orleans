// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public class DynamoDBReminderTableOptions
    {
        /// <summary>
        /// Gets or sets the connection string.
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }
    }
}