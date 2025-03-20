// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime.Scheduler
{
    internal interface IWorkItem
    {
        string Name { get; }
        IGrainContext GrainContext { get; }
        void Execute();

        internal static readonly Action<object> ExecuteWorkItem = state => ((IWorkItem)state).Execute();
    }
}
