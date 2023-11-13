using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Consistency
{
    [Alias("Orleans.Transactions.TestKit.Consistency.IConsistencyTestGrain")]
    public interface IConsistencyTestGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("Run")]
        Task<Observation[]> Run(ConsistencyTestOptions options, int depth, string stack, int max, DateTime stopAfter);
    }


    [Serializable]
    [GenerateSerializer]
    [Alias("Orleans.Transactions.TestKit.Consistency.UserAbort")]
    public class UserAbort : Exception
    {
        public UserAbort() : base("User aborted transaction") { }

        protected UserAbort(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

}
