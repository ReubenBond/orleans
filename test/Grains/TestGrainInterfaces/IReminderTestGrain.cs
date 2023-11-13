namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IReminderTestGrain")]
    public interface IReminderTestGrain : IGrainWithIntegerKey
    {
        [Alias("IsReminderExists")]
        Task<bool> IsReminderExists(string reminderName);
        [Alias("AddReminder")]
        Task AddReminder(string reminderName);
        [Alias("RemoveReminder")]
        Task RemoveReminder(string reminderName);
    }
}