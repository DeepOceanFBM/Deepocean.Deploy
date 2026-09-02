using DeepOcean.Deploy.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace DeepOcean.Deploy.Services
{
    /// <summary>
    /// Compiles and executes custom C# tool scripts using Roslyn.
    /// Downloads required NuGet packages at runtime before compilation.
    /// </summary>
    public class RoslynCompilerService
    {
        private static readonly string CustomScriptsRoot = Path.Combine(AppContext.BaseDirectory, "CustomScripts");
        private static readonly string ToolsDir = Path.Combine(CustomScriptsRoot, "Tools");
        private static readonly string WorkFlowsDir = Path.Combine(CustomScriptsRoot, "WorkFlows");
        private static readonly string PackagesDir = Path.Combine(CustomScriptsRoot, "Packages");
        private static readonly string NugetCacheDir = Path.Combine(CustomScriptsRoot, ".nuget-cache");

        // Cache compiled assemblies by tool name to avoid re-compilation
        private static readonly Dictionary<string, Assembly> _assemblyCache = new();

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(ToolsDir);
            Directory.CreateDirectory(WorkFlowsDir);
            Directory.CreateDirectory(PackagesDir);
            Directory.CreateDirectory(NugetCacheDir);
        }

        // ─── File I/O ─────────────────────────────────────────────────────────

        public static CustomToolFiles LoadTool(string name)
        {
            EnsureDirectories();
            return new CustomToolFiles
            {
                Name = name,
                ModelCode = SafeRead(Path.Combine(ToolsDir, $"{name}.cs")),
                WorkFlowCode = SafeRead(Path.Combine(WorkFlowsDir, $"{name}_WorkFlow.cs")),
                Packages = LoadPackageList(name),
            };
        }

        public static List<string> ListTools()
        {
            EnsureDirectories();
            return Directory.GetFiles(ToolsDir, "*.cs")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n)
                .ToList();
        }

        public static void SaveTool(CustomToolFiles tool)
        {
            EnsureDirectories();
            File.WriteAllText(Path.Combine(ToolsDir, $"{tool.Name}.cs"), tool.ModelCode ?? "");
            File.WriteAllText(Path.Combine(WorkFlowsDir, $"{tool.Name}_WorkFlow.cs"), tool.WorkFlowCode ?? "");
            SavePackageList(tool.Name, tool.Packages ?? new());
            // Invalidate cache when source changes
            _assemblyCache.Remove(tool.Name);
        }

        public static void DeleteTool(string name)
        {
            TryDelete(Path.Combine(ToolsDir, $"{name}.cs"));
            TryDelete(Path.Combine(WorkFlowsDir, $"{name}_WorkFlow.cs"));
            TryDelete(Path.Combine(PackagesDir, $"{name}.json"));
            _assemblyCache.Remove(name);
        }

        // ─── NuGet ────────────────────────────────────────────────────────────

        public static async Task<List<string>> RestorePackagesAsync(string toolName, Action<string> log)
        {
            var packages = LoadPackageList(toolName);
            var dlls = new List<string>();
            if (!packages.Any()) return dlls;

            log($"[NuGet] Restoring {packages.Count} package(s) for '{toolName}'...");

            var providers = Repository.Provider.GetCoreV3();
            var sourceRepo = new SourceRepository(new PackageSource("https://api.nuget.org/v3/index.json"), providers);
            var cache = new SourceCacheContext { NoCache = false };

            foreach (var pkg in packages)
            {
                var parts = pkg.Split('/');
                var pkgId = parts[0].Trim();
                var pkgVer = parts.Length > 1 ? parts[1].Trim() : null;

                try
                {
                    var metaRes = await sourceRepo.GetResourceAsync<MetadataResource>();
                    NuGetVersion version;
                    if (pkgVer != null && NuGetVersion.TryParse(pkgVer, out var parsed))
                    {
                        version = parsed;
                    }
                    else
                    {
                        var versions = await metaRes.GetVersions(pkgId, true, false, cache, NullLogger.Instance, CancellationToken.None);
                        version = versions.OrderByDescending(v => v).FirstOrDefault();
                        if (version == null)
                        {
                            log($"[NuGet] ❌ Package not found: {pkgId}");
                            continue;
                        }
                    }

                    var pkgFolder = Path.Combine(NugetCacheDir, pkgId, version.ToString());
                    if (!Directory.Exists(pkgFolder))
                    {
                        log($"[NuGet] Downloading {pkgId} v{version}...");
                        Directory.CreateDirectory(pkgFolder);
                        var dlRes = await sourceRepo.GetResourceAsync<FindPackageByIdResource>();
                        using var ms = new MemoryStream();
                        await dlRes.CopyNupkgToStreamAsync(pkgId, version, ms, cache, NullLogger.Instance, CancellationToken.None);
                        ms.Seek(0, SeekOrigin.Begin);
                        using var reader = new PackageArchiveReader(ms);
                        var allLibs = reader.GetLibItems().ToList();
                        var libs = allLibs
                            .Where(g => g.TargetFramework.Framework.Contains("NETCore", StringComparison.OrdinalIgnoreCase) ||
                                        g.TargetFramework.Framework.Contains("NETStandard", StringComparison.OrdinalIgnoreCase) ||
                                        g.TargetFramework.Framework.Contains("net", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(g => g.TargetFramework.Version)
                            .FirstOrDefault() ?? allLibs.FirstOrDefault();

                        if (libs != null)
                        {
                            foreach (var item in libs.Items.Where(i => i.EndsWith(".dll")))
                            {
                                var dllName = Path.GetFileName(item);
                                var entry = reader.GetEntry(item);
                                var dllPath = Path.Combine(pkgFolder, dllName);
                                using var stream = entry.Open();
                                using var fs = File.Create(dllPath);
                                await stream.CopyToAsync(fs);
                            }
                        }
                    }

                    var foundDlls = Directory.GetFiles(pkgFolder, "*.dll");
                    dlls.AddRange(foundDlls);
                    
                    // Load DLLs into current context for runtime execution
                    foreach (var dllPath in foundDlls)
                    {
                        try
                        {
                            var asmName = System.Reflection.AssemblyName.GetAssemblyName(dllPath);
                            if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == asmName.Name))
                            {
                                System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
                            }
                        }
                        catch { }
                    }

                    log($"[NuGet] ✅ {pkgId} v{version} ready.");
                }
                catch (Exception ex)
                {
                    log($"[NuGet] ❌ Failed to restore {pkgId}: {ex.Message}");
                }
            }

            return dlls;
        }

        // ─── Roslyn Compilation ───────────────────────────────────────────────

        public static async Task<(Assembly? assembly, string error)> CompileToolAsync(string name, IEnumerable<string> extraDllPaths, Action<string> log)
        {
            if (_assemblyCache.TryGetValue(name, out var cached))
                return (cached, "");

            log($"[Roslyn] Compiling '{name}'...");

            var modelCode = SafeRead(Path.Combine(ToolsDir, $"{name}.cs"));
            var workflowCode = SafeRead(Path.Combine(WorkFlowsDir, $"{name}_WorkFlow.cs"));

            if (string.IsNullOrWhiteSpace(modelCode) || string.IsNullOrWhiteSpace(workflowCode))
                return (null, $"Missing source files for custom tool '{name}'.");

            // Collect all references: current app's assemblies + extra NuGet DLLs
            var references = new List<MetadataReference>();

            var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            if (!string.IsNullOrEmpty(trustedAssemblies))
            {
                foreach (var refPath in trustedAssemblies.Split(Path.PathSeparator))
                {
                    references.Add(MetadataReference.CreateFromFile(refPath));
                }
            }
            else
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
                            references.Add(MetadataReference.CreateFromFile(asm.Location));
                    }
                    catch { }
                }
            }

            foreach (var dll in extraDllPaths)
            {
                if (File.Exists(dll))
                    references.Add(MetadataReference.CreateFromFile(dll));
            }

            var syntaxTrees = new[]
            {
                CSharpSyntaxTree.ParseText(modelCode),
                CSharpSyntaxTree.ParseText(workflowCode),
            };

            var compilation = CSharpCompilation.Create(
                $"DynamicTool_{name}_{Guid.NewGuid():N}",
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);

            if (!result.Success)
            {
                var errors = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString());
                var errorMsg = string.Join("\n", errors);
                log($"[Roslyn] ❌ Compilation failed:\n{errorMsg}");
                return (null, errorMsg);
            }

            ms.Seek(0, SeekOrigin.Begin);
            var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
            _assemblyCache[name] = assembly;

            log($"[Roslyn] ✅ '{name}' compiled successfully.");
            return (assembly, "");
        }

        public static async Task<object> GetToolSchemaAsync(string toolName)
        {
            var dlls = await RestorePackagesAsync(toolName, _ => { });
            var (assembly, _) = await CompileToolAsync(toolName, dlls, _ => { });
            if (assembly == null) return new List<object>();

            var modelType = assembly.GetTypes().FirstOrDefault(t => t.IsSubclassOf(typeof(EventTools)));
            if (modelType == null) return new List<object>();

            return DeployController.GetTypeSchema(modelType) ?? new List<object>();
        }

        public static async Task<object?> RunCustomToolAsync(string name, object processData, Action<string> log)
        {
            var nugetDlls = await RestorePackagesAsync(name, log);
            var (assembly, error) = await CompileToolAsync(name, nugetDlls, log);
            if (assembly == null) throw new Exception($"Compile error: {error}");

            var workflowType = assembly.GetTypes()
                .FirstOrDefault(t => t.GetMethod("Start", BindingFlags.Public | BindingFlags.Static) != null);

            if (workflowType == null)
                throw new Exception($"WorkFlow class containing 'public static object Start(...)' method not found in compiled assembly.");

            var modelType = assembly.GetTypes()
                .FirstOrDefault(t => t.IsSubclassOf(typeof(EventTools)));

            if (modelType == null)
                throw new Exception($"Tool Model class inheriting from 'EventTools' not found in compiled assembly.");

            var startMethod = workflowType.GetMethod("Start");
            if (startMethod == null)
                throw new Exception($"Method 'Start' not found in '{workflowType.Name}'.");

            // Deserialize processData into the dynamic model type
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(processData);
            var typedConfig = Newtonsoft.Json.JsonConvert.DeserializeObject(json, modelType);

            try
            {
                var resultTask = startMethod.Invoke(null, new[] { typedConfig });
                if (resultTask is Task task)
                {
                    await task;
                    var resultProp = task.GetType().GetProperty("Result");
                    return resultProp?.GetValue(task);
                }
                return resultTask;
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw new Exception(ex.InnerException?.Message ?? ex.Message);
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string SafeRead(string path) =>
            File.Exists(path) ? File.ReadAllText(path) : "";

        private static void TryDelete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static List<string> LoadPackageList(string name)
        {
            var path = Path.Combine(PackagesDir, $"{name}.json");
            if (!File.Exists(path)) return new();
            return Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path)) ?? new();
        }

        private static void SavePackageList(string name, List<string> packages)
        {
            var path = Path.Combine(PackagesDir, $"{name}.json");
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(packages, Newtonsoft.Json.Formatting.Indented));
        }
    }

    public class CustomToolFiles
    {
        public string Name { get; set; } = "";
        public string ModelCode { get; set; } = "";
        public string WorkFlowCode { get; set; } = "";
        public List<string> Packages { get; set; } = new();
    }
}
