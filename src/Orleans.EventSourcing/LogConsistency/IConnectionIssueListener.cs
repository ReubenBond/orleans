// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.EventSourcing;

/// <summary>
/// An interface that is implemented by log-consistent grains using virtual protected methods
/// that can be overridden by users, in order to monitor the connection issues.
/// </summary>
public interface IConnectionIssueListener
{
    /// <summary>
    /// Called when running into some sort of connection trouble.
    /// The called code can modify the retry delay if desired, to change the default.
    /// </summary>
    void OnConnectionIssue(ConnectionIssue connectionIssue);

    /// <summary>
    /// Called when a previously reported connection issue has been resolved.
    /// </summary>
    void OnConnectionIssueResolved(ConnectionIssue connectionIssue);
}
