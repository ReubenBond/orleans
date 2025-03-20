// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Internal
{
    /// <summary>
    /// The Utils class contains a variety of utility methods for use in application and grain code.
    /// </summary>
    internal static class StandardExtensions
    {
        public static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;

        public static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
    }
}
