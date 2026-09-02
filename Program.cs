using DeepOcean.Deploy.Services;
using DeepOcean.Deploy.Tools;
using EmbedIO;
using EmbedIO.Files;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace DeepOcean.Deploy
{
    class Program
    {
        static void Main(string[] args)
        {
            var url = "http://localhost:5000/";
            if (args.Length > 0)
                url = args[0];

            var baseDir = AppContext.BaseDirectory;
            var projectRoot = baseDir;

            // If running from bin/Debug, step back to the project root
            if (baseDir.Contains("bin") && baseDir.Contains("Debug"))
            {
                var dirInfo = new DirectoryInfo(baseDir);
                while (dirInfo != null && !dirInfo.Name.Equals("DeepOcean.Deploy", StringComparison.OrdinalIgnoreCase))
                {
                    dirInfo = dirInfo.Parent;
                }
                if (dirInfo != null)
                {
                    projectRoot = dirInfo.FullName;
                    Directory.SetCurrentDirectory(projectRoot);
                }
            }

            var wwwroot = Path.Combine(projectRoot, "wwwroot");

            using (var server = CreateWebServer(url, wwwroot))
            {
                server.RunAsync();
                Console.WriteLine($"Server is running on {url}");
                Console.WriteLine("Server is running indefinitely. Close the console window to stop it.");
                System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
            }
        }

        private static WebServer CreateWebServer(string url, string wwwroot)
        {
            var server = new WebServer(o => o
                    .WithUrlPrefix(url)
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithWebApi("/api", m => m.WithController<DeployController>())
                .WithStaticFolder("/", wwwroot, true, m => m.WithContentCaching(false));

            return server;
        }
    }

    public class DeployRequest
    {
        public List<string> Projects { get; set; }
    }

    public class DeployController : WebApiController
    {
        private static readonly string ConfigFile = "projects_config.json";
        public static ConcurrentQueue<string> LogsQueue = new ConcurrentQueue<string>();
        public static List<string> LogsList = new List<string>();

        public static void AddLog(string message)
        {
            Console.WriteLine(message);
            LogsQueue.Enqueue(message);
            LogsList.Add(message);
        }

        [Route(HttpVerbs.Get, "/projects")]
        public async Task GetProjects()
        {
            if (!File.Exists(ConfigFile))
            {
                File.WriteAllText(ConfigFile, "[]");
            }
            string json = File.ReadAllText(ConfigFile);
            HttpContext.Response.ContentType = "application/json";
            using (var writer = HttpContext.OpenResponseText())
            {
                await writer.WriteAsync(json);
            }
        }

        [Route(HttpVerbs.Post, "/projects")]
        public async Task SaveProjects()
        {
            string requestBody;
            using (var reader = HttpContext.OpenRequestText())
            {
                requestBody = await reader.ReadToEndAsync();
            }

            var json = JsonConvert.DeserializeObject<JArray>(requestBody);
            if (json == null)
            {
                HttpContext.Response.StatusCode = 400;
                return;
            }
            File.WriteAllText(ConfigFile, JsonConvert.SerializeObject(json, Formatting.Indented));
            HttpContext.Response.StatusCode = 200;
        }

        [Route(HttpVerbs.Get, "/processes")]
        public async Task GetProcesses()
        {
            var assembly = typeof(EventTools).Assembly;
            var eventToolTypes = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(EventTools)));

            var result = new List<object>();

            foreach (var type in eventToolTypes)
            {
                result.Add(new
                {
                    Name = type.Name,
                    Properties = GetTypeSchema(type)
                });
            }

            // Also include custom scripted tools from CustomScripts/Tools/
            var customTools = RoslynCompilerService.ListTools();
            foreach (var toolName in customTools)
            {
                var props = await RoslynCompilerService.GetToolSchemaAsync(toolName);
                result.Add(new { Name = toolName, Properties = props, IsCustom = true });
            }

            HttpContext.Response.ContentType = "application/json";
            using (var writer = HttpContext.OpenResponseText())
            {
                await writer.WriteAsync(JsonConvert.SerializeObject(result));
            }
        }

        [Route(HttpVerbs.Get, "/logs")]
        public async Task GetLogs()
        {
            var result = new { logs = LogsList };
            HttpContext.Response.ContentType = "application/json";
            using (var writer = HttpContext.OpenResponseText())
            {
                await writer.WriteAsync(JsonConvert.SerializeObject(result));
            }
        }

        [Route(HttpVerbs.Post, "/deploy")]
        public async Task PostDeploy()
        {
            var requestData = await HttpContext.GetRequestDataAsync<DeployRequest>();
            if (requestData == null || requestData.Projects == null || requestData.Projects.Count == 0)
            {
                HttpContext.Response.StatusCode = 400;
                return;
            }

            LogsList.Clear();
            AddLog($"Starting deployment for selected projects...");

            try
            {
                if (!File.Exists(ConfigFile))
                {
                    throw new Exception("Projects config file not found.");
                }

                var configJson = File.ReadAllText(ConfigFile);
                var allProjects = JsonConvert.DeserializeObject<List<ProjectConfig>>(configJson);
                var projectsToRun = allProjects.Where(p => requestData.Projects.Contains(p.ProjectName)).ToList();

                var workFlowObj = new WorkFlowLogic();
                var response = await workFlowObj.RunProjects(projectsToRun);
                
                if (response.Success)
                {
                    AddLog("✅ DEPLOYMENT COMPLETED SUCCESSFULLY!");
                    HttpContext.Response.StatusCode = 200;
                }
                else
                {
                    AddLog($"❌ DEPLOYMENT FAILED: {response.Message}");
                    HttpContext.Response.StatusCode = 500;
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ FATAL ERROR: {ex.Message}");
                HttpContext.Response.StatusCode = 500;
            }
        }

        // ─── Custom Tools API ────────────────────────────────────────────────

        [Route(HttpVerbs.Get, "/custom-tools")]
        public async Task GetCustomTools()
        {
            var tools = RoslynCompilerService.ListTools();
            HttpContext.Response.ContentType = "application/json";
            using var writer = HttpContext.OpenResponseText();
            await writer.WriteAsync(JsonConvert.SerializeObject(tools));
        }

        [Route(HttpVerbs.Get, "/custom-tools/{name}")]
        public async Task GetCustomTool(string name)
        {
            var tool = RoslynCompilerService.LoadTool(name);
            HttpContext.Response.ContentType = "application/json";
            using var writer = HttpContext.OpenResponseText();
            await writer.WriteAsync(JsonConvert.SerializeObject(tool));
        }

        [Route(HttpVerbs.Post, "/custom-tools")]
        public async Task SaveCustomTool()
        {
            string body;
            using (var reader = HttpContext.OpenRequestText())
                body = await reader.ReadToEndAsync();

            var tool = JsonConvert.DeserializeObject<CustomToolFiles>(body);
            if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
            {
                HttpContext.Response.StatusCode = 400;
                return;
            }
            RoslynCompilerService.SaveTool(tool);
            HttpContext.Response.StatusCode = 200;
        }

        [Route(HttpVerbs.Delete, "/custom-tools/{name}")]
        public async Task DeleteCustomTool(string name)
        {
            RoslynCompilerService.DeleteTool(name);
            HttpContext.Response.StatusCode = 200;
            await Task.CompletedTask;
        }

        [Route(HttpVerbs.Post, "/custom-tools/compile")]
        public async Task CompileCustomTool()
        {
            string body;
            using (var reader = HttpContext.OpenRequestText())
                body = await reader.ReadToEndAsync();

            var tool = JsonConvert.DeserializeObject<CustomToolFiles>(body);
            if (tool == null)
            {
                HttpContext.Response.StatusCode = 400;
                return;
            }

            // Save temp, compile, report result
            var tempName = tool.Name ?? "_temp_compile_";
            RoslynCompilerService.SaveTool(tool);

            var logs = new List<string>();
            var nugetDlls = await RoslynCompilerService.RestorePackagesAsync(tempName, m => logs.Add(m));
            var (asm, error) = await RoslynCompilerService.CompileToolAsync(tempName, nugetDlls, m => logs.Add(m));

            var response = new
            {
                Success = asm != null,
                Error = error,
                Logs = logs
            };

            HttpContext.Response.ContentType = "application/json";
            using (var writer = HttpContext.OpenResponseText())
                await writer.WriteAsync(JsonConvert.SerializeObject(response));
        }
        public static object GetTypeSchema(Type type, HashSet<Type> visited = null)
        {
            visited ??= new HashSet<Type>();
            if (visited.Contains(type)) return null;
            visited.Add(type);

            var props = new List<object>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                bool isPrimitive = p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType == typeof(decimal);
                if (isPrimitive)
                {
                    props.Add(new { Name = p.Name, Type = p.PropertyType.Name });
                }
                else
                {
                    props.Add(new { Name = p.Name, Type = "Object", Fields = GetTypeSchema(p.PropertyType, visited) });
                }
            }
            visited.Remove(type);
            return props;
        }
    }
}
