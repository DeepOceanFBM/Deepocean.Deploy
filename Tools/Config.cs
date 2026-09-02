using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeepOcean.Deploy.Tools
{
    public class Config
    {
        public List<ProjectConfig> Configs { set; get; }
        public string DatabasaeSQlitePath { set; get; }
    }
}
