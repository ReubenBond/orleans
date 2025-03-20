// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

public static class ClientInstruments
{
    internal static ObservableGauge<int> ConnectedGatewayCount;
    internal static void RegisterConnectedGatewayCountObserve(Func<int> observeValue)
    {
        ConnectedGatewayCount = Instruments.Meter.CreateObservableGauge(InstrumentNames.CLIENT_CONNECTED_GATEWAY_COUNT, observeValue);
    }
}
