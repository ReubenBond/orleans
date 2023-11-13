using System.Threading.Tasks;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// Grain interface for grains that participate in multi-cluster log-consistency protocols.
    /// </summary>
    [Alias("Orleans.EventSourcing.ILogConsistencyProtocolParticipant")]
    public interface ILogConsistencyProtocolParticipant  : IGrain  
    {
        /// <summary>
        /// Called immediately before the user-level OnActivateAsync, on same scheduler.
        /// </summary>
        /// <returns></returns>
        [Alias("PreActivateProtocolParticipant")]
        Task PreActivateProtocolParticipant();

        /// <summary>
        /// Called immediately after the user-level OnActivateAsync, on same scheduler.
        /// </summary>
        /// <returns></returns>
        [Alias("PostActivateProtocolParticipant")]
        Task PostActivateProtocolParticipant();

        /// <summary>
        /// Called immediately after the user-level OnDeactivateAsync, on same scheduler.
        /// </summary>
        /// <returns></returns>
        [Alias("DeactivateProtocolParticipant")]
        Task DeactivateProtocolParticipant();
    }

    /// <summary>
    /// interface to mark classes that represent protocol messages.
    /// All such classes must be serializable.
    /// </summary>
    public interface ILogConsistencyProtocolMessage
    {
    }
}
