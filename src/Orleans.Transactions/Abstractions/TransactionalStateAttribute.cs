// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Orleans.Transactions.Abstractions
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class TransactionalStateAttribute : Attribute, IFacetMetadata, ITransactionalStateConfiguration
    {
        public string StateName { get; }
        public string StorageName { get; }

        public TransactionalStateAttribute(string stateName, string storageName = null)
        {
            this.StateName = stateName;
            this.StorageName = storageName;
        }
    }
}
