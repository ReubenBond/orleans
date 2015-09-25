namespace Orleans.CodeGeneration
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Runtime.Serialization;
    using System.Text.RegularExpressions;

    using Orleans.Concurrency;
    using Orleans.Runtime;
    using Orleans.Serialization;

    public static class SerializationCodeGenerator
    {
        /// <summary>
        /// The serializers.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, SerializationManager.SerializerMethods> Serializers =
            new ConcurrentDictionary<Type, SerializationManager.SerializerMethods>();

        /// <summary>
        /// The logger.
        /// </summary>
        private static readonly TraceLogger Logger = TraceLogger.GetLogger("CodeGenerator");

        /// <summary>
        /// The namespaces which should not have serializers generated.
        /// </summary>
        private static readonly List<string> BlacklistedNamespaces = new List<string>
        {
            "System",
            "Microsoft.Win32",
            "Microsoft.CodeAnalysis"
        };

        /// <summary>
        /// The delegate used to set fields in value types.
        /// </summary>
        /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
        /// <typeparam name="TField">The field type.</typeparam>
        /// <param name="instance">The instance having its field set.</param>
        /// <param name="value">The value being set.</param>
        private delegate void ValueTypeSetter<TDeclaring, in TField>(ref TDeclaring instance, TField value);

        /// <summary>
        /// Generates and registers serializers for the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        public static void GenerateAndRegisterSerializer(Type type)
        {
            if (!ShouldGenerateSerializer(type))
            {
                return;
            }

            if (type.IsGenericTypeDefinition)
            {
                Logger.Info("Generating generic serializer for type " + type.GetParseableName());
                RegisterSpecializingSerializer(type);
                return;
            }

            Logger.Info("Generating serializer for type " + type.GetParseableName());
            GenerateAndRegisterSerializerInner(type);
        }

        /// <summary>
        /// Returns true if a serializer should be generated for the provided type, false otherwise.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>
        /// true if a serializer should be generated for the provided type, false otherwise.
        /// </returns>
        private static bool ShouldGenerateSerializer(Type type)
        {
            return
                !(type.IsArray || type.IsByRef || type.IsAbstract || type.IsPointer || type.IsCOMObject
                  || type.IsInterface || type.IsGenericParameter || type.IsPrimitive
                  || TypeUtils.IsInNamespace(type, BlacklistedNamespaces));
        }

        /// <summary>
        /// Registers a serializer for the provided generic type definition which will specialize when constructed versions of that type are serialized.
        /// </summary>
        /// <param name="type">The generic type definition.</param>
        private static void RegisterSpecializingSerializer(Type type)
        {
            SerializationManager.Register(
                type,
                original =>
                {
                    if (original == null)
                    {
                        return null;
                    }

                    var expected = original.GetType();
                    var methods = GenerateAndRegisterSerializerInner(expected);
                    return methods.DeepCopy(original);
                },
                (value, stream, expected) =>
                {
                    var methods = GenerateAndRegisterSerializerInner(expected);
                    methods.Serialize(value, stream, expected);
                },
                (expected, stream) =>
                {
                    var methods = GenerateAndRegisterSerializerInner(expected);
                    return methods.Deserialize(expected, stream);
                });
        }

        /// <summary>
        /// Generates and registers serializers for the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        private static SerializationManager.SerializerMethods GenerateAndRegisterSerializerInner(Type type)
        {
            SerializationManager.SerializerMethods result;
            if (Serializers.TryGetValue(type, out result))
            {
                return result;
            }

            // Generate a serializer for this type.
            var fields = GetFields(type);
            var copier = GenerateDeepCopier(type, fields);
            var serializer = GenerateAndRegisterSerializer(type, fields);
            var deserializer = GenerateDeserializer(type, fields);
            result = new SerializationManager.SerializerMethods(copier, serializer, deserializer);

            // Save and register the serializer.
            Serializers.TryAdd(type, result);
            SerializationManager.Register(type, result.DeepCopy, result.Serialize, result.Deserialize, true);

            return result;
        }

        /// <summary>
        /// Generates a <see cref="SerializationManager.DeepCopier"/>.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fields">The fields.</param>
        /// <returns>The newly generated <see cref="SerializationManager.DeepCopier"/>.</returns>
        private static SerializationManager.DeepCopier GenerateDeepCopier(
            Type type,
            IEnumerable<FieldInfoMember> fields)
        {
            // Parameters
            var originalParameter = Expression.Parameter(typeof(object), "original");

            // Optimization for types marked as immutable.
            if (type.GetCustomAttribute<ImmutableAttribute>() != null)
            {
                // Immutable types do not require copying.
                return
                    Expression.Lambda<SerializationManager.DeepCopier>(originalParameter, originalParameter).Compile();
            }

            // Variables
            var inputVariable = Expression.Variable(type, "input");
            var resultVariable = Expression.Variable(type, "result");
            var resultAsObject = Expression.Convert(resultVariable, typeof(object));

            // Perform some initialization.
            Expression<Action<object, object>> recordObject =
                (original, copy) => SerializationContext.Current.RecordObject(original, copy);
            var body = new List<Expression>
            {
                Expression.Assign(inputVariable, Expression.Convert(originalParameter, type)),
                Expression.Assign(resultVariable, GetObjectCreationExpression(type)),
                Expression.Invoke(recordObject, originalParameter, resultAsObject)
            };

            // Copy each field.
            body.AddRange(fields.Select(field => field.GetSetExpression(resultVariable, field.GetGetExpression(inputVariable))));

            // Return the result.
            if (type.IsValueType)
            {
                body.Add(resultAsObject);
            }
            else
            {
                body.Add(resultVariable);
            }

            return Expression.Lambda<SerializationManager.DeepCopier>(
                Expression.Block(new[] { inputVariable, resultVariable }, body),
                originalParameter).Compile();
        }

        /// <summary>
        /// Generates a <see cref="SerializationManager.Serializer"/>.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fields">The fields.</param>
        /// <returns>The newly generated <see cref="SerializationManager.Serializer"/>.</returns>
        private static SerializationManager.Serializer GenerateAndRegisterSerializer(
            Type type,
            IEnumerable<FieldInfoMember> fields)
        {
            // Parameters
            var untypedInputParameter = Expression.Parameter(typeof(object), "untypedInput");
            var streamParameter = Expression.Parameter(typeof(BinaryTokenStreamWriter), "stream");
            var expectedParameter = Expression.Parameter(typeof(Type), "expected");

            // Variables
            var inputVariable = Expression.Variable(type, "input");

            // Perform some initialization.
            Expression<Action<object, BinaryTokenStreamWriter, Type>> serializeInner =
                (input, stream, expected) => SerializationManager.SerializeInner(input, stream, expected);
            var body = new List<Expression>
            {
                Expression.Assign(inputVariable, Expression.Convert(untypedInputParameter, type))
            };

            // Serialize each field.
            foreach (var field in fields)
            {
                body.Add(
                    Expression.Invoke(
                        serializeInner,
                        Expression.Convert(field.GetGetExpression(inputVariable), typeof(object)),
                        streamParameter,
                        Expression.Constant(field.FieldInfo.FieldType)));
            }

            return Expression.Lambda<SerializationManager.Serializer>(
                Expression.Block(new[] { inputVariable }, body),
                untypedInputParameter,
                streamParameter,
                expectedParameter).Compile();
        }

        /// <summary>
        /// Generates a <see cref="SerializationManager.Deserializer"/>.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fields">The fields.</param>
        /// <returns>The newly generated <see cref="SerializationManager.Deserializer"/>.</returns>
        private static SerializationManager.Deserializer GenerateDeserializer(
            Type type,
            IEnumerable<FieldInfoMember> fields)
        {
            // Parameters
            var expectedTypeParameter = Expression.Parameter(typeof(Type), "expected");
            var streamParameter = Expression.Parameter(typeof(BinaryTokenStreamReader), "stream");

            // Variables
            var resultVariable = Expression.Variable(type, "result");

            // Perform some initialization.
            Expression<Func<Type, BinaryTokenStreamReader, object>> deserializeInner =
                (expected, stream) => SerializationManager.DeserializeInner(expected, stream);
            var body = new List<Expression> { Expression.Assign(resultVariable, GetObjectCreationExpression(type)) };

            // Deserialize each field.
            foreach (var field in fields)
            {
                var fieldType = field.FieldInfo.FieldType;
                var value = Expression.Invoke(deserializeInner, Expression.Constant(fieldType), streamParameter);
                body.Add(field.GetSetExpression(resultVariable, Expression.Convert(value, fieldType)));
            }

            // Return the result.
            if (type.IsValueType)
            {
                body.Add(Expression.Convert(resultVariable, typeof(object)));
            }
            else
            {
                body.Add(resultVariable);
            }

            return Expression.Lambda<SerializationManager.Deserializer>(
                Expression.Block(new[] { resultVariable }, body),
                expectedTypeParameter,
                streamParameter).Compile();
        }

        /// <summary>
        /// Creates an expression to get a new instance of the provided <paramref name="type"/>.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>An expression to get a new instance of the provided <paramref name="type"/>.</returns>
        private static Expression GetObjectCreationExpression(Type type)
        {
            Expression result;
            var constructor = type.GetConstructor(Type.EmptyTypes);
            if (type.IsValueType)
            {
                // Use the default value.
                result = Expression.Default(type);
            }
            else if (constructor != null)
            {
                // Use the default constructor.
                result = Expression.New(constructor);
            }
            else
            {
                // Create an unformatted object.
                Expression<Func<Type, object>> getUninitializedObject = _ => FormatterServices.GetUninitializedObject(_);
                result = Expression.Convert(Expression.Invoke(getUninitializedObject, Expression.Constant(type)), type);
            }

            return result;
        }

        /// <summary>
        /// Returns the fields for the specified <paramref name="type"/>.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The fields for the specified <paramref name="type"/>.</returns>
        private static List<FieldInfoMember> GetFields(Type type)
        {
            var result =
                type.GetAllFields()
                    .Where(field => field.GetCustomAttribute<NonSerializedAttribute>() == null)
                    .Select(info => new FieldInfoMember(info))
                    .ToList();
            result.Sort(FieldInfoMember.Comparer.Instance);
            return result;
        }

        /// <summary>
        /// Represents a field of a type.
        /// </summary>
        private class FieldInfoMember
        {
            public FieldInfoMember(FieldInfo fieldInfo)
            {
                this.FieldInfo = fieldInfo;
                this.PropertyInfo = this.GetPropertyInfo();
            }

            /// <summary>
            /// Gets the <see cref="FieldInfo"/> for this field.
            /// </summary>
            public FieldInfo FieldInfo { get; private set; }

            /// <summary>
            /// Gets or sets the <see cref="PropertyInfo"/> for this field, if it is the backing field of an auto-property.
            /// </summary>
            private PropertyInfo PropertyInfo { get; set; }

            /// <summary>
            /// Returns an expression to rerieve the value of this field for an instance.
            /// </summary>
            /// <param name="instance">An expression representing the instance.</param>
            /// <param name="forceAvoidCopy">Whether or not to force shallow copying.</param>
            /// <returns>An expression to rerieve the value of this field for an instance.</returns>
            public Expression GetGetExpression(Expression instance, bool forceAvoidCopy = false)
            {
                Expression getter;

                // If the field is the backing field for an auto-property, try to use the property directly.
                if (this.PropertyInfo != null && this.PropertyInfo.GetGetMethod() != null)
                {
                    // Access the field via its auto-property.
                    getter = Expression.Property(instance, this.PropertyInfo);
                }
                else
                {
                    // Access the field
                    getter = Expression.Field(instance, this.FieldInfo);
                }

                var fieldType = this.FieldInfo.FieldType;
                if (forceAvoidCopy || fieldType.IsOrleansShallowCopyable())
                {
                    // Shallow-copy the field.
                    return getter;
                }

                // Deep-copy the field.
                Expression<Func<object, object>> deepCopyInner = input => SerializationManager.DeepCopyInner(input);
                return Expression.Convert(
                    Expression.Invoke(deepCopyInner, Expression.Convert(getter, typeof(object))),
                    fieldType);
            }

            /// <summary>
            /// Returns an expression to set the value of this field for an instance.
            /// </summary>
            /// <param name="instance">An expression representing the instance.</param>
            /// <param name="value">The expression representing the new value.</param>
            /// <returns>An expression to set the value of this field for an instance.</returns>
            public Expression GetSetExpression(Expression instance, Expression value)
            {
                // If the field is the backing field for an auto-property, try to use the property directly.
                if (this.PropertyInfo != null && this.PropertyInfo.GetSetMethod() != null)
                {
                    return Expression.Assign(Expression.Property(instance, this.PropertyInfo), value);
                }

                // Readonly fields cannot be set from an expression directly.
                if (this.FieldInfo.IsInitOnly)
                {
                    return this.GetSetExpressionForReadonlyField(instance, value);
                }

                // Return an expression which performs a direct assignment.
                return Expression.Assign(Expression.Field(instance, this.FieldInfo), value);
            }

            /// <summary>
            /// Returns an expression to set the value of this <see langword="readonly"/> field for an instance.
            /// </summary>
            /// <param name="instance">An expression representing the instance.</param>
            /// <param name="value">The expression representing the new value.</param>
            /// <returns>An expression to set the value of this field for an instance.</returns>
            private Expression GetSetExpressionForReadonlyField(Expression instance, Expression value)
            {
                var declaringType = this.FieldInfo.DeclaringType;
                if (declaringType == null)
                {
                    throw new InvalidOperationException("Field " + this.FieldInfo.Name + " does not have a declaring type.");
                }

                // Create a delegate to set the field on this type.
                var fieldType = this.FieldInfo.FieldType;
                Type delegateType;
                Type[] parameterTypes;
                if (declaringType.IsValueType)
                {
                    // Value types need to be passed by-ref.
                    delegateType = typeof(ValueTypeSetter<,>).MakeGenericType(declaringType, fieldType);
                    parameterTypes = new[] { declaringType.MakeByRefType(), this.FieldInfo.FieldType };
                }
                else
                {
                    // Reference types can be passed directly.
                    delegateType = typeof(Action<,>).MakeGenericType(declaringType, fieldType);
                    parameterTypes = new[] { declaringType, this.FieldInfo.FieldType };
                }

                // Return an expression which invokes the newly created delegate to set the field's value.
                var setter = this.GetSetDelegate(delegateType, parameterTypes);
                return Expression.Invoke(Expression.Constant(setter, delegateType), instance, value);
            }

            /// <summary>
            /// Returns a delegate to set the set the value of a specified field.
            /// </summary>
            /// <param name="delegateType">The delegate type.</param>
            /// <param name="parameterTypes">The parameter types.</param>
            /// <returns>A delegate to set the set the value of a specified field.</returns>
            private Delegate GetSetDelegate(Type delegateType, Type[] parameterTypes)
            {
                var declaringType = this.FieldInfo.DeclaringType;
                if (declaringType == null)
                {
                    throw new InvalidOperationException(
                        "Field " + this.FieldInfo.Name + " does not have a declaring type.");
                }

                // Create a method to hold the generated IL.
                var method = new DynamicMethod(
                    this.FieldInfo.Name + "Set",
                    null,
                    parameterTypes,
                    declaringType.Module,
                    true);

                // Emit IL to return the value of the Transaction property.
                var emitter = method.GetILGenerator();
                emitter.Emit(OpCodes.Ldarg_0);
                emitter.Emit(OpCodes.Ldarg_1);
                emitter.Emit(OpCodes.Stfld, this.FieldInfo);
                emitter.Emit(OpCodes.Ret);

                return method.CreateDelegate(delegateType);
            }

            /// <summary>
            /// Returns the corresponding <see cref="PropertyInfo"/> for this instance's <see cref="FieldInfo"/> or null if not found.
            /// </summary>
            /// <returns>
            /// The corresponding <see cref="PropertyInfo"/> for this instance's <see cref="FieldInfo"/> or null if not found.
            /// </returns>
            private PropertyInfo GetPropertyInfo()
            {
                var propertyName = Regex.Match(this.FieldInfo.Name, "^<([^>]+)>.*$");
                if (!propertyName.Success || this.FieldInfo.DeclaringType == null)
                {
                    return null;
                }

                var name = propertyName.Groups[1].Value;
                return this.FieldInfo.DeclaringType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            }

            /// <summary>
            /// A comparer for <see cref="FieldInfoMember"/> which compares by name.
            /// </summary>
            internal class Comparer : IComparer<FieldInfoMember>
            {
                /// <summary>
                /// The singleton instance.
                /// </summary>
                private static readonly Comparer Singleton = new Comparer();

                public int Compare(FieldInfoMember x, FieldInfoMember y)
                {
                    return string.Compare(x.FieldInfo.Name, y.FieldInfo.Name, StringComparison.Ordinal);
                }

                /// <summary>
                /// Gets the singleton instance of this class.
                /// </summary>
                public static Comparer Instance
                {
                    get
                    {
                        return Singleton;
                    }
                }
            }
        }
    }
}