// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans;

public interface ITransactionClient
{
    /// <summary>
    /// Run transaction delegate
    /// </summary>
    /// <param name="transactionOption"></param>
    /// <param name="transactionDelegate"></param>
    /// <returns><see cref="Task"/></returns>
    /// <remarks>Transaction always commit, unless an exception is thrown from the delegate and depending on <paramref name="transactionOption"/></remarks>
    Task RunTransaction(TransactionOption transactionOption, Func<Task> transactionDelegate);

    /// <summary>
    /// Run transaction delegate
    /// </summary>
    /// <param name="transactionOption"></param>
    /// <param name="transactionDelegate"></param>
    /// <returns>True if the transaction should commit</returns>
    Task RunTransaction(TransactionOption transactionOption, Func<Task<bool>> transactionDelegate);
}
