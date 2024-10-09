using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Consistency
{
    public interface IConsistencyTestGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<Observation[]> Run(ConsistencyTestOptions options, int depth, string stack, int max, DateTime stopAfter);
    }


    [Serializable]
    [GenerateSerializer]
    public class UserAbortException : Exception
    {
        public UserAbortException() : base("User aborted transaction") { }

        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.")]
        protected UserAbortException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

}
