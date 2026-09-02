using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeepOcean.Deploy.Extensions
{
    public class ServiceResponseModel<T>
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public T? Data { set; get; }
        public int CodeStatus { get; set; } = 0;
    }
}
