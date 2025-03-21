// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Reflection.Runtime.Assemblies;
using System.Reflection.Runtime.MethodInfos;
using System.Reflection.Runtime.TypeInfos;
using System.Runtime.CompilerServices;
using System.Text;

using Internal.LowLevelLinq;
using Internal.Reflection.Augments;
using Internal.Reflection.Core.Execution;
using Internal.Reflection.Extensions.NonPortable;
using Internal.Runtime.Augments;

namespace System.Reflection.Runtime.General
{
    internal static partial class Helpers
    {
        // This helper helps reduce the temptation to write "h == default(RuntimeTypeHandle)" which causes boxing.
        public static bool IsNull(this RuntimeTypeHandle h)
        {
            return h.Equals(default(RuntimeTypeHandle));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Type[] GetGenericTypeParameters(this Type type)
        {
            Debug.Assert(type.IsGenericTypeDefinition);
            return type.GetGenericArguments();
        }

        public static Type[] ToTypeArray(this RuntimeTypeInfo[] typeInfos)
        {
            int count = typeInfos.Length;
            if (count == 0)
                return Array.Empty<Type>();

            Type[] types = new Type[count];
            for (int i = 0; i < count; i++)
            {
                types[i] = typeInfos[i].ToType();
            }
            return types;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RuntimeTypeInfo ToRuntimeTypeInfo(this Type type)
        {
            if (type is RuntimeType runtimeType)
            {
                return runtimeType.GetRuntimeTypeInfo();
            }
            Debug.Assert(false);
            return null;
        }

        public static ReadOnlyCollection<T> ToReadOnlyCollection<T>(this IEnumerable<T> enumeration)
        {
            return Array.AsReadOnly(enumeration.ToArray());
        }

        public static MethodInfo FilterAccessor(this MethodInfo accessor, bool nonPublic)
        {
            if (nonPublic)
                return accessor;
            if (accessor.IsPublic)
                return accessor;
            return null;
        }

        public static TypeLoadException CreateTypeLoadException(string typeName, string assemblyName)
        {
            string message = SR.Format(SR.TypeLoad_ResolveTypeFromAssembly, typeName, assemblyName);
            return new TypeLoadException(message, typeName);
        }

        // Escape identifiers as described in "Specifying Fully Qualified Type Names" on msdn.
        // Current link is http://msdn.microsoft.com/en-us/library/yfsftwz6(v=vs.110).aspx
        public static string EscapeTypeNameIdentifier(this string identifier)
        {
            // Some characters in a type name need to be escaped

            // We're avoiding calling into MemoryExtensions here as it has paths that lead to reflection,
            // and that would lead to an infinite loop given that this is the implementation of reflection.
#pragma warning disable CA1870 // Use a cached 'SearchValues' instance
            if (identifier != null && identifier.IndexOfAny(s_charsToEscape) != -1)
#pragma warning restore CA1870
            {
                StringBuilder sbEscapedName = new StringBuilder(identifier.Length);
                foreach (char c in identifier)
                {
                    if (c.NeedsEscapingInTypeName())
                        sbEscapedName.Append('\\');

                    sbEscapedName.Append(c);
                }
                identifier = sbEscapedName.ToString();
            }
            return identifier;
        }

        public static bool NeedsEscapingInTypeName(this char c)
        {
            return Array.IndexOf(s_charsToEscape, c) >= 0;
        }

        private static readonly char[] s_charsToEscape = new char[] { '\\', '[', ']', '+', '*', '&', ',' };

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2075:UnrecognizedReflectionPattern",
            Justification = "Delegates always generate metadata for the Invoke method")]
        public static RuntimeMethodInfo GetInvokeMethod(this RuntimeTypeInfo delegateType)
        {
            Debug.Assert(delegateType.IsDelegate);

            MethodInfo? invokeMethod = delegateType.ToType().GetMethod("Invoke", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (invokeMethod == null)
            {
                // No Invoke method found. Since delegate types are compiler constructed, the most likely cause is missing metadata rather than
                // a missing Invoke method.
                throw ReflectionCoreExecution.ExecutionEnvironment.CreateMissingMetadataException(delegateType.ToType());
            }
            return (RuntimeMethodInfo)invokeMethod;
        }

        public static BinderBundle ToBinderBundle(this Binder binder, BindingFlags invokeAttr, CultureInfo cultureInfo)
        {
            if (binder == null || binder is DefaultBinder || ((invokeAttr & BindingFlags.ExactBinding) != 0))
                return null;
            return new BinderBundle(binder, cultureInfo);
        }

        // Helper for ICustomAttributeProvider.GetCustomAttributes(). The result of this helper is returned directly to apps
        // so it must always return a newly allocated array. Unlike most of the newer custom attribute apis, the attribute type
        // need not derive from System.Attribute. (In particular, it can be an interface or System.Object.)
        [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
            Justification = "Array.CreateInstance is only used with reference types here and is therefore safe.")]
        public static object[] InstantiateAsArray(this IEnumerable<CustomAttributeData> cads, Type actualElementType)
        {
            ArrayBuilder<object> attributes = default;
            foreach (CustomAttributeData cad in cads)
            {
                object instantiatedAttribute = cad.Instantiate();
                attributes.Add(instantiatedAttribute);
            }

            if (actualElementType.ContainsGenericParameters || actualElementType.IsValueType)
            {
                // This is here for desktop compatibility. ICustomAttribute.GetCustomAttributes() normally returns an array of the
                // exact attribute type requested except in two cases: when the passed in type is an open type and when
                // it is a value type. In these two cases, it returns an array of type Object[].
                return attributes.ToArray();
            }
            else
            {
                object[] result = (object[])Array.CreateInstance(actualElementType, attributes.Count);
                attributes.CopyTo(result);
                return result;
            }
        }

        private static object? GetRawDefaultValue(IEnumerable<CustomAttributeData> customAttributes)
        {
            foreach (CustomAttributeData attributeData in customAttributes)
            {
                Type attributeType = attributeData.AttributeType;
                if (attributeType == typeof(DecimalConstantAttribute))
                {
                    return GetRawDecimalConstant(attributeData);
                }
                else if (attributeType.IsSubclassOf(typeof(CustomConstantAttribute)))
                {
                    if (attributeType == typeof(DateTimeConstantAttribute))
                    {
                        return GetRawDateTimeConstant(attributeData);
                    }
                    return GetRawConstant(attributeData);
                }
            }
            return DBNull.Value;
        }

        private static decimal GetRawDecimalConstant(CustomAttributeData attr)
        {
            System.Collections.Generic.IList<CustomAttributeTypedArgument> args = attr.ConstructorArguments;

            return new decimal(
                lo: GetConstructorArgument(args, 4),
                mid: GetConstructorArgument(args, 3),
                hi: GetConstructorArgument(args, 2),
                isNegative: ((byte)args[1].Value!) != 0,
                scale: (byte)args[0].Value!);

            static int GetConstructorArgument(IList<CustomAttributeTypedArgument> args, int index)
            {
                // The constructor is overloaded to accept both signed and unsigned arguments
                object obj = args[index].Value!;
                return (obj is int value) ? value : (int)(uint)obj;
            }
        }

        private static DateTime GetRawDateTimeConstant(CustomAttributeData attr)
        {
            return new DateTime((long)attr.ConstructorArguments[0].Value!);
        }

        // We are relying only on named arguments for historical reasons
        private static object? GetRawConstant(CustomAttributeData attr)
        {
            foreach (CustomAttributeNamedArgument namedArgument in attr.NamedArguments)
            {
                if (namedArgument.MemberInfo.Name.Equals("Value"))
                    return namedArgument.TypedValue.Value;
            }
            return DBNull.Value;
        }

        private static object? GetDefaultValue(IEnumerable<CustomAttributeData> customAttributes)
        {
            // we first look for a CustomConstantAttribute, but we will save the first occurrence of DecimalConstantAttribute
            // so we don't go through all custom attributes again
            CustomAttributeData? firstDecimalConstantAttributeData = null;
            foreach (CustomAttributeData attributeData in customAttributes)
            {
                Type attributeType = attributeData.AttributeType;
                if (firstDecimalConstantAttributeData == null && attributeType == typeof(DecimalConstantAttribute))
                {
                    firstDecimalConstantAttributeData = attributeData;
                }
                else if (attributeType.IsSubclassOf(typeof(CustomConstantAttribute)))
                {
                    CustomConstantAttribute customConstantAttribute = (CustomConstantAttribute)(attributeData.Instantiate());
                    return customConstantAttribute.Value;
                }
            }

            if (firstDecimalConstantAttributeData != null)
            {
                DecimalConstantAttribute decimalConstantAttribute = (DecimalConstantAttribute)(firstDecimalConstantAttributeData.Instantiate());
                return decimalConstantAttribute.Value;
            }
            else
            {
                return DBNull.Value;
            }
        }

        public static bool GetCustomAttributeDefaultValueIfAny(IEnumerable<CustomAttributeData> customAttributes, bool raw, out object? defaultValue)
        {
            // The resolution of default value is done by following these rules:
            // 1. For RawDefaultValue, we pick the first custom attribute holding the constant value
            //  in the following order: DecimalConstantAttribute, DateTimeConstantAttribute, CustomConstantAttribute
            // 2. For DefaultValue, we first look for CustomConstantAttribute and pick the first occurrence.
            //  If none is found, then we repeat the same process searching for DecimalConstantAttribute.
            // IMPORTANT: Please note that there is a subtle difference in order custom attributes are inspected for
            //  RawDefaultValue and DefaultValue.
            object? resolvedValue = raw ? GetRawDefaultValue(customAttributes) : GetDefaultValue(customAttributes);
            if (resolvedValue != DBNull.Value)
            {
                defaultValue = resolvedValue;
                return true;
            }
            else
            {
                defaultValue = null;
                return false;
            }
        }
    }
}
