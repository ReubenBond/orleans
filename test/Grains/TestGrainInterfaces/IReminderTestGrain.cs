// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface IReminderTestGrain : IGrainWithIntegerKey
{
    Task<bool> IsReminderExists(string reminderName);
    Task AddReminder(string reminderName);
    Task RemoveReminder(string reminderName);
}