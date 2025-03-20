// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AWSUtils.Tests.StorageTests
{
    public class DynamoDBStorageTestsFixture
    {
        internal UnitTestDynamoDBStorage DataManager { get; set; }

        public DynamoDBStorageTestsFixture()
        {
            if (AWSTestConstants.IsDynamoDbAvailable)
            {
                DataManager = new UnitTestDynamoDBStorage();
            }
        }
    }
}
