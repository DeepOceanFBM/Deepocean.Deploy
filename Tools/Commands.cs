namespace DeepOcean.Deploy.Tools
{
    public class Commands : EventTools
    {
        public required string FileName { set; get; } //= fileName,
        public required string Arguments { set; get; }
        public required string WorkingDirectory { set; get; }

        public bool RedirectStandardOutput { set; get; } = true;
        public bool RedirectStandardError { set; get; } = true;
        public bool UseShellExecute { set; get; } = false;
        public bool CreateNoWindow { set; get; } = true;
    }




}
