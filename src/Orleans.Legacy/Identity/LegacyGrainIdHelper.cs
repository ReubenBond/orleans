using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Legacy
{
    internal static class LegacyGrainIdHelper
    {
        public static IdSpan ConvertKeyToNewIdFormat(LegacyGrainId grainId)
        {
            var key = grainId.Key;
            var (n0, n1, keyExt) = (key.N0, key.N1, key.KeyExt);
            if (n1 != 0 && n0 == 0)
            {
                if (string.IsNullOrEmpty(keyExt))
                {
                    return IdSpan.Create($"{n1:X16}+{keyExt}");
                }
                else
                {
                    return IdSpan.Create($"{n1:X16}");
                }
            }
            else if (n1 != 0 && n0 != 0)
            {
                if (string.IsNullOrEmpty(keyExt))
                {
                    return IdSpan.Create($"{n0:X16}{n1:X16}+{keyExt}");
                }
                else
                {
                    return IdSpan.Create($"{n0:X16}{n1:X16}");
                }
            }
            else if (n1 == 0 && n0 == 0)
            {
                if (string.IsNullOrEmpty(keyExt))
                {
                    return IdSpan.Create($"{n0:X16}{n1:X16}+{keyExt}");
                }
                else
                {
                    return IdSpan.Create(keyExt);
                }
            }

            throw new InvalidOperationException($"Unable to convert GrainId");
        }

        public static string GetGrainReferenceHexKey(Type grainClass, IdSpan key)
        {
            // Convert the key into N0, N1, plus optional KeyExt
            var (n0, n1, keyExt) = ExtractKeyComponents(key);

            // Convert the grain class into TypeCodeData
            var typeCode = (ulong)(uint)GetTypeCode(grainClass);

            var hasKeyExt = string.IsNullOrEmpty(keyExt);
            var isSystemTarget = typeof(SystemTarget).IsAssignableFrom(grainClass);
            byte category = (isSystemTarget, hasKeyExt) switch
            {
                (false, false) => 0,
                (true, false) => 1,
                (false, true) => 6,
                (true, true) => 8
            };
            var typeCodeData = (ulong)category << 56 | typeCode;

            // Extract the generic arguments from the grain class
            var genericArguments = grainClass.IsGenericType ? TypeUtils.GenericTypeArgsString(grainClass.UnderlyingSystemType.FullName) : null;

            // Format the result
            var grainIdString = GetGrainIdHexString(n0, n1, typeCodeData, keyExt);
            /*
            if (IsObserverReference)
            {
                return string.Format("{0}={1} {2}={3}", "GrainReference", grainIdString, "ObserverId", observerId.ToParsableString());
            }

            if (IsSystemTarget)
            {
                return String.Format("{0}={1} {2}={3}", "GrainReference", grainIdString, "SystemTarget", "SystemTargetSilo");
            }
            */

            if (!string.IsNullOrEmpty(genericArguments))
            {
                return $"GrainReference={grainIdString} GenericArguments={genericArguments}";
            }

            return String.Format("{0}={1}", "GrainReference", grainIdString);
        }

        public static string GetGrainIdHexString(ulong n0, ulong n1, ulong typeCodeData, string keyExt) => keyExt switch
        {
            null => $"{n0:x16}{n1:x16}{typeCodeData:x16}",
            _ => $"{n0:x16}{n1:x16}{typeCodeData:x16}+{keyExt}"
        };

        public static (ulong n0, ulong n1, string keyExt) ExtractKeyComponents(IdSpan key)
        {
            string keyExt;
            var keySpan = key.Value.Span;
            ulong n0;
            ulong n1;
            var keyString = key.ToStringUtf8();

            // Try to extract N0 and N1
            if (keySpan.Length >= 32 && Guid.TryParseExact(keyString.AsSpan().Slice(0, 32), "N", out var guidKey) && (keyString.Length == 32 || keyString[33] == '+'))
            {
                // We have a GUID
                Span<byte> guidBytes = stackalloc byte[16];
                var wroteBytes = guidKey.TryWriteBytes(guidBytes);
                Debug.Assert(wroteBytes);
                n0 = BitConverter.ToUInt64(guidBytes[0..8]);
                n1 = BitConverter.ToUInt64(guidBytes[8..16]);

                // Decode the key extension, if present.
                if (keyString.Length > 32 && keyString[33] == '+')
                {
                    if (keyString.Length == 33)
                    {
                        // Key ends in a '+', so the key extension is the empty string.
                        keyExt = string.Empty;
                    }
                    else
                    {
                        keyExt = keyString[34..];
                    }
                }
                else
                {
                    keyExt = null;
                }
            }
            else if (keyString.Length >= 16 && ulong.TryParse(keyString.AsSpan().Slice(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var integerKey) && (keyString.Length == 16 || keyString[17] == '+'))
            {
                // N1 is set to the decoded hexadecimal value.
                n1 = integerKey;
                n0 = 0;

                // Decode the key extension, if present.
                if (keyString.Length > 16 && keyString[17] == '+')
                {
                    if (keyString.Length == 17)
                    {
                        // Key ends in a '+', so the key extension is the empty string.
                        keyExt = string.Empty;
                    }
                    else
                    {
                        keyExt = keyString[18..];
                    }
                }
                else
                {
                    keyExt = null;
                }
            }
            else
            {
                // The entire key is a string.
                n0 = 0;
                n1 = 0;
                keyExt = keyString;
            }

            return (n0, n1, keyExt);
        }

        public static int GetTypeCode(Type type)
        {
            if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();

            var attr = type.GetCustomAttributes<TypeCodeOverrideAttribute>(false).FirstOrDefault();
            if (attr != null) return attr.TypeCode;

            var fullName = TypeUtils.GetTemplatedName(
                TypeUtils.GetFullName(type), 
                type,
                type.GetGenericArguments(),
                t => false);
            return CalculateIdHash(fullName);
        }

        private static int CalculateIdHash(string text)
        {
            var input = BitConverter.IsLittleEndian ? MemoryMarshal.AsBytes(text.AsSpan()) : Encoding.Unicode.GetBytes(text);

            Span<int> result = stackalloc int[256 / 8 / sizeof(int)];
            var sha = SHA256.Create();
            sha.TryComputeHash(input, MemoryMarshal.AsBytes(result), out _);
            sha.Dispose();

            var hash = 0;
            for (var i = 0; i < result.Length; i++) hash ^= result[i];
            return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(hash) : hash;
        }

        /// <summary>
        /// A collection of utility functions for dealing with Type information.
        /// </summary>
        private static class TypeUtils
        {
            public static string GenericTypeArgsString(string className)
            {
                int startIndex = className.IndexOf('[');
                int endIndex = className.LastIndexOf(']');
                return className.Substring(startIndex + 1, endIndex - startIndex - 1);
            }

            public static string GetSimpleTypeName(Type type, Predicate<Type> fullName = null)
            {
                if (type.IsNestedPublic || type.IsNestedPrivate)
                {
                    if (type.DeclaringType.IsGenericType)
                    {
                        return GetTemplatedName(
                            GetUntemplatedTypeName(type.DeclaringType.Name),
                            type.DeclaringType,
                            GetGenericArgumentsSafe(type),
                            _ => true) + "." + GetUntemplatedTypeName(type.Name);
                    }

                    return GetTemplatedName(type.DeclaringType) + "." + GetUntemplatedTypeName(type.Name);
                }

                if (type.IsGenericType) return GetSimpleTypeName(fullName != null && fullName(type) ? GetFullName(type) : type.Name);

                return fullName != null && fullName(type) ? GetFullName(type) : type.Name;
            }

            public static string GetUntemplatedTypeName(string typeName)
            {
                int i = typeName.IndexOf('`');
                if (i > 0)
                {
                    typeName = typeName.Substring(0, i);
                }
                i = typeName.IndexOf('<');
                if (i > 0)
                {
                    typeName = typeName.Substring(0, i);
                }
                return typeName;
            }

            public static string GetSimpleTypeName(string typeName)
            {
                int i = typeName.IndexOf('`');
                if (i > 0)
                {
                    typeName = typeName.Substring(0, i);
                }
                i = typeName.IndexOf('[');
                if (i > 0)
                {
                    typeName = typeName.Substring(0, i);
                }
                i = typeName.IndexOf('<');
                if (i > 0)
                {
                    typeName = typeName.Substring(0, i);
                }
                return typeName;
            }

            public static string GetTemplatedName(Type t, Predicate<Type> fullName = null)
            {
                if (fullName == null)
                    fullName = _ => true; // default to full type names

                if (t.IsGenericType) return GetTemplatedName(GetSimpleTypeName(t, fullName), t, GetGenericArgumentsSafe(t), fullName);

                if (t.IsArray)
                {
                    return GetTemplatedName(t.GetElementType(), fullName)
                           + "["
                           + new string(',', t.GetArrayRank() - 1)
                           + "]";
                }

                return GetSimpleTypeName(t, fullName);
            }

            public static string GetTemplatedName(string baseName, Type t, Type[] genericArguments, Predicate<Type> fullName)
            {
                if (!t.IsGenericType || (t.DeclaringType != null && t.DeclaringType.IsGenericType)) return baseName;
                string s = baseName;
                s += "<";
                s += GetGenericTypeArgs(genericArguments, fullName);
                s += ">";
                return s;
            }

            public static Type[] GetGenericArgumentsSafe(Type type)
            {
                var result = type.GetGenericArguments();

                if (type.ContainsGenericParameters)
                {
                    // Get generic parameter from generic type definition to have consistent naming for inherited interfaces
                    // Example: interface IA<TName>, class A<TOtherName>: IA<OtherName>
                    // in this case generic parameter name of IA interface from class A is OtherName instead of TName.
                    // To avoid this situation use generic parameter from generic type definition.
                    // Matching by position in array, because GenericParameterPosition is number across generic parameters.
                    // For half open generic types (IA<int,T>) T will have position 0.
                    var originalGenericArguments = type.GetGenericTypeDefinition().GetGenericArguments();
                    if (result.Length != originalGenericArguments.Length) // this check may be redunant
                        return result;

                    for (int i = 0; i < result.Length; i++)
                    {
                        if (result[i].IsGenericParameter)
                            result[i] = originalGenericArguments[i];
                    }
                }
                return result;
            }

            public static string GetGenericTypeArgs(IEnumerable<Type> args, Predicate<Type> fullName)
            {
                string s = string.Empty;

                bool first = true;

                foreach (var genericParameter in args)
                {
                    if (!first)
                    {
                        s += ",";
                    }

                    if (!genericParameter.IsGenericType)
                    {
                        s += GetSimpleTypeName(genericParameter, fullName);
                    }
                    else
                    {
                        s += GetTemplatedName(genericParameter, fullName);
                    }
                    first = false;
                }

                return s;
            }

            public static string GetFullName(Type t)
            {
                if (t == null) throw new ArgumentNullException(nameof(t));

                if (t.IsNested && !t.IsGenericParameter)
                {
                    return t.Namespace + "." + t.DeclaringType.Name + "." + t.Name;
                }
                if (t.IsArray)
                {
                    return GetFullName(t.GetElementType())
                           + "["
                           + new string(',', t.GetArrayRank() - 1)
                           + "]";
                }

                // using of t.FullName breaks interop with core and full .net in one cluster, because
                // FullName of types from corelib is different.
                // .net core int: [System.Int32, System.Private.CoreLib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]
                // full .net int: [System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]
                return t.FullName ?? (t.IsGenericParameter ? t.Name : t.Namespace + "." + t.Name);
            }
        }
    }
}
