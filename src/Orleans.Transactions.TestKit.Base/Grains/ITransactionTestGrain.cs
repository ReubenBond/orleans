using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    [Alias("Orleans.Transactions.TestKit.ITransactionTestGrain")]
    public interface ITransactionTestGrain : IGrainWithGuidKey
    {

        /// <summary>
        /// apply set operation to every transaction state
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("Set")]
        Task Set(int newValue);

        /// <summary>
        /// apply add operation to every transaction state
        /// </summary>
        /// <param name="numberToAdd"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("Add")]
        Task<int[]> Add(int numberToAdd);

        /// <summary>
        /// apply get operation to every transaction state
        /// </summary>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("Get")]
        Task<int[]> Get();

        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("AddAndThrow")]
        Task AddAndThrow(int numberToAdd);

        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("SetAndThrow")]
        Task SetAndThrow(int numberToSet);

        [Alias("Deactivate")]
        Task Deactivate();
    }
}
