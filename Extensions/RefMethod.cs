using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DeepOcean.Deploy.Extensions
{
    public class RefMethod
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

    }
}
