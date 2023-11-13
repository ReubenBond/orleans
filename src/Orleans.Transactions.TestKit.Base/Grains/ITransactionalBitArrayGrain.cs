
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Correctnesss
{
    [Alias("Orleans.Transactions.TestKit.Correctnesss.ITransactionalBitArrayGrain")]
    public interface ITransactionalBitArrayGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Ping 
        /// </summary>
        /// <returns></returns>
        [Alias("Ping")]
        Task Ping();
        /// <summary>
        /// apply set operation to every transaction state
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("SetBit")]
        Task SetBit(int newValue);

        /// <summary>
        /// Performs a read transaction on each state, returning the results in order.
        /// </summary>
        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("Get")]
        Task<List<BitArrayState>> Get();
    }
}
