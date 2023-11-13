using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ReminderState")]
    public record class ReminderState([property: Id(0)] IGrainReminder Reminder)
    {
        [Id(1)] public DateTime? Registered { get; init; } = null;
        [Id(2)] public DateTime? Unregistered { get; init; } = null;
        [Id(3)] public List<DateTime> Fired { get; init; } = new();
        [Id(4)] public List<(DateTime, string)> Log { get; init; } = new();
    }

    [Alias("UnitTests.GrainInterfaces.IReminderTestGrain2")]
    public interface IReminderTestGrain2 : IGrainWithGuidKey
    {
        [Alias("StartReminder")]
        Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);

        [Alias("StopReminder")]
        Task StopReminder(string reminderName);
        [Alias("StopReminder1")]
        Task StopReminder(IGrainReminder reminder);

        [Alias("GetReminderPeriod")]
        Task<TimeSpan> GetReminderPeriod(string reminderName);
        [Alias("GetReminderDueTimeAndPeriod")]
        Task<(TimeSpan DueTime, TimeSpan Period)> GetReminderDueTimeAndPeriod(string reminderName);
        [Alias("GetCounter")]
        Task<long> GetCounter(string name);
        [Alias("GetReminderObject")]
        Task<IGrainReminder> GetReminderObject(string reminderName);
        [Alias("GetRemindersList")]
        Task<List<IGrainReminder>> GetRemindersList();

        [Alias("EraseReminderTable")]
        Task EraseReminderTable();

        [Alias("GetReminderStates")]
        Task<Dictionary<string, ReminderState>> GetReminderStates();
    }

    // to test reminders for different grain types
    [Alias("UnitTests.GrainInterfaces.IReminderTestCopyGrain")]
    public interface IReminderTestCopyGrain : IGrainWithGuidKey
    {
        [Alias("StartReminder")]
        Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);
        [Alias("StopReminder")]
        Task StopReminder(string reminderName);

        [Alias("GetReminderPeriod")]
        Task<TimeSpan> GetReminderPeriod(string reminderName);
        [Alias("GetCounter")]
        Task<long> GetCounter(string name);
    }

    [Alias("UnitTests.GrainInterfaces.IReminderGrainWrong")]
    public interface IReminderGrainWrong : IGrainWithIntegerKey
    // since the grain doesnt implement IRemindable, we should get an error at run time
    // we need a way to let the user know at compile time if IRemindable isn't implemented and tries to register a reminder
    {
        [Alias("StartReminder")]
        Task<bool> StartReminder(string reminderName);
    }
}

