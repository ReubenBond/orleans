// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Storage;

public interface ILocalDataStore
{
    string Etag { get; }
    string WriteRow(IList<Tuple<string, string>> keys, IDictionary<string, object> data, string eTag);
    IDictionary<string, object> ReadRow(IList<Tuple<string, string>> keys);
    IList<IDictionary<string, object>> ReadMultiRow(IList<Tuple<string, string>> keys);
    bool DeleteRow(IList<Tuple<string, string>> keys, string eTag);
    void Clear();
}
