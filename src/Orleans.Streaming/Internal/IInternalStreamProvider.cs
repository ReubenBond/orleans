// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Streams;

internal interface IInternalStreamProvider
{
    IInternalAsyncBatchObserver<T> GetProducerInterface<T>(IAsyncStream<T> streamId);
    IInternalAsyncObservable<T> GetConsumerInterface<T>(IAsyncStream<T> streamId);
}
