// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestGrainInterfaces;

// The grain supports an operation to reserve a seat
public interface ISeatReservationGrain : IGrainWithIntegerKey
{
    // returns a boolean if reservation was successful
    Task<bool> Reserve(int seatnumber, string userid);
}
