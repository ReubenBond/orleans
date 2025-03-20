// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    public class RedStreamNamespacePredicate : IStreamNamespacePredicate
    {
        public string PredicatePattern => ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(RedStreamNamespacePredicate), constructorArgument: null);

        public bool IsMatch(string streamNamespace)
        {
            return streamNamespace.StartsWith("red");
        }
    }
}