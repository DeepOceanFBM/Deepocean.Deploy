using DeepOcean.Deploy.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DeepOcean.Deploy.Extensions
{
    public static class RefMethod
    {
        public static async Task<object?> InvokeStaticMethodAsync(
  string className,
  string methodName,
  params object?[] parameters)
        {
            try
            {
                // Get Class Type
                Type? classType = FindType(className);

                if (classType == null)
                    throw new TypeLoadException($"Class not found: {className}");

                // Get Method
                MethodInfo? method = classType.GetMethod(
                    methodName,
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Static
                );

                if (method == null)
                    throw new MissingMethodException(
                        className,
                        methodName
                    );

                // تأكيد إنها Static
                if (!method.IsStatic)
                    throw new InvalidOperationException(
                        $"Method '{methodName}' is not static."
                    );

                // Invoke
                object? result = method.Invoke(
                    null,
                    parameters
                );

                // Task
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);

                    // Task<T>
                    var resultProperty = task
                        .GetType()
                        .GetProperty("Result");

                    return resultProperty?.GetValue(task);
                }

                return result;
            }
            catch (TargetInvocationException ex)
            {
                // Reflection بيحط Exception الأصلية هنا
                throw ex.InnerException ?? ex;
            }
        }


        public static Type? FindType(string className)
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(x => x.GetType(className))
                .FirstOrDefault(x => x != null);
        }


        public static object ChangeDynamicProperty(
       this object obj,
       string propertyName,
       object value)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException("Property name cannot be empty.", nameof(propertyName));

            var parts = propertyName.Split('.');

            object currentObj = obj;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];

                if (currentObj == null)
                    throw new NullReferenceException(
                        $"Cannot access property '{part}' because the parent object is null.");

                var propertyInfo = currentObj.GetType().GetProperty(
                    part,
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.IgnoreCase);

                if (propertyInfo == null)
                {
                    throw new ArgumentException(
                        $"Property '{part}' not found on type '{currentObj.GetType().FullName}'.");
                }

                // Last property -> set value
                if (i == parts.Length - 1)
                {
                    object convertedValue = ConvertValue(
                        value,
                        propertyInfo.PropertyType);

                    propertyInfo.SetValue(currentObj, convertedValue);

                    return obj;
                }

                // Move to nested object
                currentObj = propertyInfo.GetValue(currentObj);
            }

            return obj;


            object ConvertValue(object value, Type targetType)
            {
                if (value == null)
                {
                    if (!targetType.IsValueType ||
                        Nullable.GetUnderlyingType(targetType) != null)
                    {
                        return null;
                    }

                    throw new InvalidCastException(
                        $"Cannot assign null to '{targetType.FullName}'.");
                }

                var nullableType = Nullable.GetUnderlyingType(targetType);

                if (nullableType != null)
                    targetType = nullableType;

                if (targetType.IsInstanceOfType(value))
                    return value;

                if (targetType.IsEnum)
                    return Enum.Parse(targetType, value.ToString(), true);

                return Convert.ChangeType(value, targetType);
            }

        }


    }
}


