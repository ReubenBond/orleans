// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Streams.Core;

/// <summary>
/// Functionality for retrieving a <see cref="IStreamSubscriptionManager"/> instance.
/// </summary>
public interface IStreamSubscriptionManagerAdmin
{
    /// <summary>
    /// Gets the stream subscription manager.
    /// </summary>
    /// <param name="managerType">Type of the manager.</param>
    /// <returns>The <see cref="IStreamSubscriptionManager"/>.</returns>
    IStreamSubscriptionManager GetStreamSubscriptionManager(string managerType);
}

/// <summary>
/// Constants for <see cref="IStreamSubscriptionManagerAdmin.GetStreamSubscriptionManager(string)"/>.
/// </summary>
public static class StreamSubscriptionManagerType
{
    /// <summary>
    /// The explicit subscription manager.
    /// </summary>
    public const string ExplicitSubscribeOnly = "ExplicitSubscribeOnly";
}
