using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeepOcean.Deploy.Extensions
{
    public class DeepOceanJsonObject
    {
        public static T? GetValue<T>(object obj, string propertyName)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            if (string.IsNullOrEmpty(propertyName)) throw new ArgumentNullException(nameof(propertyName));
            var property = obj.GetType().GetProperty(propertyName);
            if (property == null) throw new ArgumentException($"Property '{propertyName}' not found on type '{obj.GetType().FullName}'.");
            var value = property.GetValue(obj);
            if (value is T typedValue)
            {
                return typedValue;
            }
            else
            {
                throw new InvalidCastException($"Cannot cast value of property '{propertyName}' to type '{typeof(T).FullName}'.");
            }
        }




    }
}
