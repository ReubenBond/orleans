// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class GatewayInstruments
{
    internal static readonly Counter<int> GatewaySent = Instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_SENT);
    internal static readonly Counter<int> GatewayReceived = Instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_RECEIVED);
    internal static readonly Counter<int> GatewayLoadShedding = Instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_LOAD_SHEDDING);
}
