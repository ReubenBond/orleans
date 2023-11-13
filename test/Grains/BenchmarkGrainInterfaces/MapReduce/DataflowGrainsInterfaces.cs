namespace BenchmarkGrainInterfaces.MapReduce
{
    [Alias("BenchmarkGrainInterfaces.MapReduce.IDataflowGrain")]
    public interface IDataflowGrain : IGrain
    {
        [Alias("Complete")]
        Task Complete();

        [Alias("Fault")]
        Task Fault();

        [Alias("Completion")]
        Task Completion();
    }

    [Alias("BenchmarkGrainInterfaces.MapReduce.ITargetGrain`1")]
    public interface ITargetGrain<in TInput> : IDataflowGrain, IGrainWithGuidKey
    {
        [Alias("OfferMessage")]
        Task<GrainDataflowMessageStatus> OfferMessage(TInput messageValue, bool consumeToAccept);

        [Alias("SendAsync")]
        Task SendAsync(TInput t);

        [Alias("SendAsync1")]
        Task SendAsync(TInput t, GrainCancellationToken gct);
    }

    [Alias("BenchmarkGrainInterfaces.MapReduce.ISourceGrain`1")]
    public interface ISourceGrain<TOutput> : IDataflowGrain, IGrainWithGuidKey
    {
        [Alias("LinkTo")]
        Task LinkTo(ITargetGrain<TOutput> t);

        [Alias("ConsumeMessage")]
        Task<TOutput> ConsumeMessage();
    }

    public interface IProcessor<in TProcessor>
    {
        Task Initialize(TProcessor processor);
    }

    public interface ITargetProcessor<in TInput>
    {
        void Process(TInput t);
    }

    public interface ITransformProcessor<in TInput, out TOutput>
    {
        TOutput Process(TInput input);
    }

    [Alias("BenchmarkGrainInterfaces.MapReduce.IPropagatorGrain`2")]
    public interface IPropagatorGrain<in TInput, TOutput> : ITargetGrain<TInput>, ISourceGrain<TOutput>
    {
        [Alias("ReceiveAll")]
        Task<List<TOutput>> ReceiveAll();
    }

    [Alias("BenchmarkGrainInterfaces.MapReduce.ITransformGrain`2")]
    public interface ITransformGrain<TInput, TOutput> : IPropagatorGrain<TInput, TOutput>, IProcessor<ITransformProcessor<TInput, TOutput>>
    {
    }

    [Alias("BenchmarkGrainInterfaces.MapReduce.IBufferGrain`1")]
    public interface IBufferGrain<T> : IPropagatorGrain<T, T>
    {
    }
}
