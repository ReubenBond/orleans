// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime
{
    internal interface IActivationCollector
    {
        /// <summary>
        /// Schedule collection.
        /// </summary>
        /// <param name="item">The activation to be scheduled.</param>
        /// <returns></returns>
        void ScheduleCollection(ActivationData item);

        /// <summary>
        /// Attempt to reschedule collection.
        /// </summary>
        /// <param name="item">The activation to be rescheduled.</param>
        /// <returns></returns>
        bool TryRescheduleCollection(ActivationData item);
    }
}
