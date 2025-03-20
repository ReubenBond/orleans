// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime;

internal interface IMessageCenter
{
    void SendMessage(Message msg);

    void DispatchLocalMessage(Message message);
}
