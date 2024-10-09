using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;
using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    [Serializable]
    [GenerateSerializer]
    public class GrainData
    {
        [Id(0)]
        public int Value { get; set; }
    }

    public class MaxStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public MaxStateTransactionalGrain(ITransactionalStateFactory stateFactory,
            ILoggerFactory loggerFactory)
            : base(Enumerable.Range(0, TransactionTestConstants.MaxCoordinatedTransactions)
                .Select(i => stateFactory.Create<GrainData>(new TransactionalStateConfiguration(new TransactionalStateAttribute($"data{i}", TransactionTestConstants.TransactionStore))))
                .ToArray(),
                  loggerFactory)
        {
        }
    }

    public class DoubleStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public DoubleStateTransactionalGrain(
            [TransactionalState("data1", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data1,
            [TransactionalState("data2", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data2,
            ILoggerFactory loggerFactory)
            : base(new ITransactionalState<GrainData>[2] { data1, data2 }, loggerFactory)
        {
        }
    }

    public class SingleStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public SingleStateTransactionalGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data,
            ILoggerFactory loggerFactory)
            : base(new ITransactionalState<GrainData>[1] { data }, loggerFactory)
        {
        }
    }

    public class NoStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public NoStateTransactionalGrain(
            ILoggerFactory loggerFactory)
            : base(Array.Empty<ITransactionalState<GrainData>>(), loggerFactory)
        {
        }
    }

    public class MultiStateTransactionalGrainBaseClass : Grain, ITransactionTestGrain
    {
        private readonly ILoggerFactory loggerFactory;

        protected ITransactionalState<GrainData>[] DataArray { get; set; }
        protected ILogger Logger { get; set; }

        public MultiStateTransactionalGrainBaseClass(
            ITransactionalState<GrainData>[] dataArray,
            ILoggerFactory loggerFactory)
        {
            this.DataArray = dataArray;
            this.loggerFactory = loggerFactory;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.Logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            return base.OnActivateAsync(cancellationToken);
        }

        public async Task Set(int newValue)
        {
            foreach(var data in this.DataArray)
            {
                await data.PerformUpdate(state =>
                {
                    this.Logger.LogInformation("Setting from {Value} to {NewValue}.", state.Value, newValue);
                    state.Value = newValue;
                    this.Logger.LogInformation("Set to {Value}.", state.Value);
                });
            }
        }

        public async Task<int[]> Add(int numberToAdd)
        {
            var result = new int[DataArray.Length];
            for(int i = 0; i < DataArray.Length; i++)
            {
                result[i] = await DataArray[i].PerformUpdate(state =>
                {
                    this.Logger.LogInformation("Adding {NumberToAdd} to value {Value}.", numberToAdd, state.Value);
                    state.Value += numberToAdd;
                    this.Logger.LogInformation("Value after Adding {NumberToAdd} is {Value}.", numberToAdd, state.Value);
                    return state.Value;
                });
            }
            return result;
        }

        public async Task<int[]> Get()
        {
            var result = new int[DataArray.Length];
            for (int i = 0; i < DataArray.Length; i++)
            {
                result[i] = await DataArray[i].PerformRead(state =>
                {
                    this.Logger.LogInformation("Get {Value}.", state.Value);
                    return state.Value;
                });
            }
            return result;
        }

        public async Task AddAndThrow(int numberToAdd)
        {
            await Add(numberToAdd);
            throw new AddAndThrowException($"{GetType().Name} test exception");
        }

        public async Task SetAndThrow(int numberToSet)
        {
            await Set(numberToSet);
            throw new AddAndThrowException($"{GetType().Name} test exception");
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class AddAndThrowException : Exception
    {
        public AddAndThrowException() : base("Unexpected error.") { }

        public AddAndThrowException(string message) : base(message) { }

        public AddAndThrowException(string message, Exception innerException) : base(message, innerException) { }

        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.")]
        protected AddAndThrowException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
