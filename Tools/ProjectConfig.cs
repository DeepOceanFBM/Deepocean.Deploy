namespace DeepOcean.Deploy.Tools
{
    public class ProjectConfig
    {
        public string ProjectName { set; get; } = string.Empty;
        public string ProjectType { set; get; } = string.Empty;
        public int ProjectID { set; get; }
        public List<object> Processes { set; get; } = new List<object>();
    }




}
