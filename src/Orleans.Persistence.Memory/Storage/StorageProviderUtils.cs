using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Storage
{
    internal class StorageProviderUtils
    {
        /// <summary>
        /// ETag of value "*" to match any etag for conditional table operations (update, nerge, delete).
        /// </summary>
        public const string ANY_ETAG = "*";

        public static int PositiveHash(GrainReference grainReference, int hashRange)
        {
            int hash = unchecked((int)grainReference.GetUniformHashCode());
            int positiveHash = ((hash % hashRange) + hashRange) % hashRange;
            return positiveHash;
        }
        public static int PositiveHash(int hash, int hashRange)
        {
            int positiveHash = ((hash % hashRange) + hashRange) % hashRange;
            return positiveHash;
        }

        public static string PrintKeys(IEnumerable<Tuple<string, string>> keys)
        {
            return Utils.EnumerableToString(keys,
                keyTuple => string.Format("Key:{0}={1}", keyTuple.Item1, keyTuple.Item2 ?? "null"));
        }

        public static string PrintData(object data)
        {
            if (data == null)
            {
                return "[ ]";
            }

            return data.ToString();
        }

        public static string PrintOneWrite(
            string key,
            object data,
            string eTag)
        {
            var sb = new StringBuilder();
            sb.Append("Key=").Append(key);
            sb.Append(" Data=").Append(PrintData(data));
            sb.Append(" Etag=").Append(eTag ?? "null");
            return sb.ToString();
        }
    }
}
