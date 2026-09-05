using DeepOcean.Deploy.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepOcean.Deploy.Services
{
    public class RoslynCompilerService
    {
        private static readonly string CustomScriptsRoot = Path.Combine(AppContext.BaseDirectory, "CustomScripts");
        private static readonly string ToolsDir = Path.Combine(CustomScriptsRoot, "Tools");
        private static readonly string WorkFlowsDir = Path.Combine(CustomScriptsRoot, "WorkFlows");
        private static readonly string PackagesDir = Path.Combine(CustomScriptsRoot, "Packages");
        private static readonly string NugetCacheDir = Path.Combine(CustomScriptsRoot, ".nuget-cache");
        private static readonly string RuntimeDir = Path.Combine(CustomScriptsRoot, ".runtime");

        private static readonly ConcurrentDictionary<string, Assembly> _assemblyCache = new();
        private static readonly ConcurrentDictionary<string, ToolLoadContext> _loadContexts = new();

        private static readonly NuGetFramework HostFramework = DetectHostFramework();
        private static readonly string HostRid = RuntimeInformation.RuntimeIdentifier;
        private static readonly HashSet<string> HostTpaNames = BuildHostTpaNames();

        /// <summary>NuGet RID fallback chain, e.g. win-x64 → win → any</summary>
        private static readonly string[] RidFallbacks = BuildRidFallbacks();

        private static NuGetFramework DetectHostFramework()
        {
            try { var v = Environment.Version; return NuGetFramework.ParseFolder($"net{v.Major}.{v.Minor}"); }
            catch { return NuGetFramework.ParseFolder("net9.0"); }
        }

        private static string[] BuildRidFallbacks()
        {
            var rid = RuntimeInformation.RuntimeIdentifier; // e.g. win10-x64, linux-musl-x64
            var fallbacks = new List<string> { rid };

            var parts = rid.Split('-');
            string os = parts.Length > 0 ? parts[0] : "";
            string arch = parts.Length > 1 ? parts[1] : "";
            string qualifier = parts.Length > 2 ? parts[2] : "";

            // Strip version numbers for base OS
            string baseOs = os;
            if (os.StartsWith("win") && os.Length > 3) baseOs = "win";
            else if (os.StartsWith("osx") && os.Length > 3) baseOs = "osx";
            else if (os.StartsWith("alpine") && os.Length > 6) baseOs = "alpine";
            else if (os.StartsWith("rhel") && os.Length > 4) baseOs = "rhel";
            else if (os.StartsWith("ubuntu") && os.Length > 6) baseOs = "ubuntu";
            else if (os.StartsWith("debian") && os.Length > 6) baseOs = "debian";

            // Add permutations
            if (!string.IsNullOrEmpty(arch) && !string.IsNullOrEmpty(qualifier))
                fallbacks.Add($"{baseOs}-{arch}-{qualifier}");

            if (!string.IsNullOrEmpty(arch))
            {
                fallbacks.Add($"{os}-{arch}");
                fallbacks.Add($"{baseOs}-{arch}");
            }

            if (!string.IsNullOrEmpty(qualifier))
            {
                fallbacks.Add($"{os}-{qualifier}");
                fallbacks.Add($"{baseOs}-{qualifier}");
            }

            fallbacks.Add(os);
            fallbacks.Add(baseOs);

            // Cross-OS family fallbacks
            if (baseOs != "win" && baseOs != "osx")
            {
                if (!string.IsNullOrEmpty(arch)) fallbacks.Add($"linux-{arch}");
                fallbacks.Add("linux");
            }
            if (baseOs != "win")
            {
                if (!string.IsNullOrEmpty(arch)) fallbacks.Add($"unix-{arch}");
                fallbacks.Add("unix");
            }

            fallbacks.Add("any");
            
            return fallbacks.Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
        }

        private static HashSet<string> BuildHostTpaNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrWhiteSpace(tpa))
                foreach (var p in tpa.Split(Path.PathSeparator))
                { var n = Path.GetFileNameWithoutExtension(p); if (!string.IsNullOrWhiteSpace(n)) set.Add(n); }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (!a.IsDynamic && a.GetName().Name is string nm) set.Add(nm);
            return set;
        }

        static RoslynCompilerService() { EnsureDirectories(); }

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(CustomScriptsRoot);
            Directory.CreateDirectory(ToolsDir);
            Directory.CreateDirectory(WorkFlowsDir);
            Directory.CreateDirectory(PackagesDir);
            Directory.CreateDirectory(NugetCacheDir);
            Directory.CreateDirectory(RuntimeDir);
        }

        private static string GetToolRuntimeDirectory(string toolName) => Path.Combine(RuntimeDir, toolName);

        public static CustomToolFiles LoadTool(string name)
        {
            EnsureDirectories();
            return new CustomToolFiles
            {
                Name = name,
                ModelCode = SafeRead(Path.Combine(ToolsDir, $"{name}.cs")),
                WorkFlowCode = SafeRead(Path.Combine(WorkFlowsDir, $"{name}_WorkFlow.cs")),
                Packages = LoadPackageList(name)
            };
        }

        public static List<string> ListTools()
        {
            EnsureDirectories();
            return Directory.GetFiles(ToolsDir, "*.cs")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x).ToList()!;
        }

        public static void SaveTool(CustomToolFiles tool)
        {
            EnsureDirectories();
            File.WriteAllText(Path.Combine(ToolsDir, $"{tool.Name}.cs"), tool.ModelCode ?? "");
            File.WriteAllText(Path.Combine(WorkFlowsDir, $"{tool.Name}_WorkFlow.cs"), tool.WorkFlowCode ?? "");
            SavePackageList(tool.Name, tool.Packages ?? new List<string>());
            ClearToolCache(tool.Name);
        }

        public static void DeleteTool(string name)
        {
            TryDelete(Path.Combine(ToolsDir, $"{name}.cs"));
            TryDelete(Path.Combine(WorkFlowsDir, $"{name}_WorkFlow.cs"));
            TryDelete(Path.Combine(PackagesDir, $"{name}.json"));
            ClearToolCache(name);
            try { var d = GetToolRuntimeDirectory(name); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        private static void ClearToolCache(string name)
        {
            _assemblyCache.TryRemove(name, out _);
            if (_loadContexts.TryRemove(name, out var ctx)) try { ctx.Unload(); } catch { }
        }

        // -- NUGET RESTORE -----------------------------------------------

        public static async Task<List<string>> RestorePackagesAsync(string toolName, Action<string> log)
        {
            EnsureDirectories();
            var packageRefs = LoadPackageList(toolName);
            if (packageRefs.Count == 0) return new List<string>();

            log($"[NuGet] Restoring {packageRefs.Count} package(s) for '{toolName}'...");

            var toolRuntimeDir = GetToolRuntimeDirectory(toolName);
            Directory.CreateDirectory(toolRuntimeDir);

            var repository = new SourceRepository(
                new NuGet.Configuration.PackageSource("https://api.nuget.org/v3/index.json"),
                Repository.Provider.GetCoreV3());
            using var cache = new SourceCacheContext { NoCache = false };

            var rootIds = new List<PackageIdentity>();
            foreach (var pkgRef in packageRefs)
            {
                var parts = pkgRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var pkgId = parts[0].Trim();
                NuGetVersion? version = null;
                if (parts.Length > 1) NuGetVersion.TryParse(parts[1].Trim(), out version);
                version ??= await GetLatestVersionAsync(repository, pkgId, cache);
                if (version == null) { log($"[NuGet] ? Could not resolve '{pkgId}'"); continue; }
                rootIds.Add(new PackageIdentity(pkgId, version));
                log($"[NuGet] ?? Root: {pkgId} {version}");
            }

            if (rootIds.Count == 0) return new List<string>();

            var allPackages = await CollectFullGraphAsync(repository, rootIds, cache, log);

            // compileDlls = ref → lib paths for Roslyn references
            // runtimeDlls = actual DLLs needed at runtime (lib + runtimes/RID/lib)
            var compileDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runtimeDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pkg in allPackages)
            {
                var pkgDir = Path.Combine(NugetCacheDir, pkg.Id, pkg.Version.ToString());
                Directory.CreateDirectory(pkgDir);
                var nupkgPath = Path.Combine(pkgDir, $"{pkg.Id}.{pkg.Version}.nupkg");
                if (!File.Exists(nupkgPath))
                    await DownloadPackageAsync(repository, pkg, cache, nupkgPath, log);

                using var reader = new PackageArchiveReader(nupkgPath);

                // 1. Extract compile-time refs (ref/ → lib/) into toolRuntimeDir
                var refs = ExtractCompileAssets(reader, toolRuntimeDir);
                foreach (var d in refs) compileDlls.Add(d);

                // 2. Extract runtime managed DLLs (runtimes/RID/lib/TFM → lib/)
                var rtManaged = ExtractRuntimeManagedDlls(reader, toolRuntimeDir);
                foreach (var d in rtManaged) runtimeDlls.Add(d);
                if (rtManaged.Count == 0)
                    foreach (var d in refs) runtimeDlls.Add(d); // fall back to lib

                // 3. Extract native assets (runtimes/RID/native)
                ExtractNativeAssets(reader, toolRuntimeDir);
            }

            // Combine: compile set drives Roslyn; runtime set drives the ALC
            var allDlls = new HashSet<string>(runtimeDlls, StringComparer.OrdinalIgnoreCase);
            foreach (var d in compileDlls) allDlls.Add(d);

            var context = _loadContexts.GetOrAdd(toolName, _ => new ToolLoadContext(toolRuntimeDir, HostTpaNames));
            foreach (var dll in allDlls) context.RegisterAssembly(dll);

            log($"[NuGet] 📦 {compileDlls.Count} compile ref(s), {runtimeDlls.Count} runtime DLL(s) in .runtime/{toolName}/");

            // Return compile DLLs (Roslyn uses these)
            return compileDlls.ToList();
        }

        private static async Task<List<PackageIdentity>> CollectFullGraphAsync(
            SourceRepository repository, IEnumerable<PackageIdentity> roots,
            SourceCacheContext cache, Action<string> log)
        {
            var visitedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(roots.Select(r => r.Id));
            var allAvailable = new List<SourcePackageDependencyInfo>();
            var depResource = await repository.GetResourceAsync<DependencyInfoResource>();

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                if (!visitedIds.Add(currentId)) continue;

                IEnumerable<SourcePackageDependencyInfo>? packages = null;
                try
                {
                    packages = await depResource.ResolvePackages(
                        currentId, HostFramework, cache, NullLogger.Instance, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log($"[NuGet] ⚠ Could not resolve packages for {currentId}: {ex.Message}");
                    continue;
                }

                if (packages == null || !packages.Any())
                {
                    log($"[NuGet] ⚠ No versions found for {currentId}");
                    continue;
                }

                foreach (var pkg in packages)
                {
                    allAvailable.Add(pkg);
                    foreach (var dep in pkg.Dependencies)
                    {
                        if (!visitedIds.Contains(dep.Id))
                        {
                            queue.Enqueue(dep.Id);
                        }
                    }
                }
            }

            if (allAvailable.Count == 0)
                return new List<PackageIdentity>();

            var targets = roots.Select(r => new PackageDependency(r.Id, new VersionRange(r.Version))).ToList();
            var targetIdentities = roots.ToList();
            try
            {
                var resolver = new PackageResolver();
                var resolved = resolver.Resolve(new PackageResolverContext(
                    DependencyBehavior.Lowest,
                    targets.Select(t => t.Id),
                    Enumerable.Empty<string>(),
                    Enumerable.Empty<PackageReference>(),
                    targetIdentities,
                    allAvailable,
                    new[] { new NuGet.Configuration.PackageSource("https://api.nuget.org/v3/index.json") },
                    NullLogger.Instance), CancellationToken.None).ToList();

                log($"[NuGet] ✅ Resolver selected {resolved.Count} package(s) in graph.");
                return resolved;
            }
            catch (Exception ex)
            {
                log($"[NuGet] ⚠ PackageResolver failed ({ex.Message}); falling back to collected packages.");
                // If resolver fails, at least return the roots so we don't crash everything
                return targetIdentities;
            }
        }

        private static async Task<NuGetVersion?> GetLatestVersionAsync(
            SourceRepository repo, string id, SourceCacheContext cache)
        {
            var meta = await repo.GetResourceAsync<MetadataResource>();
            var versions = await meta.GetVersions(id, false, false, cache, NullLogger.Instance, CancellationToken.None);
            return versions.OrderByDescending(v => v).FirstOrDefault();
        }



        private static async Task DownloadPackageAsync(
            SourceRepository repo, PackageIdentity pkg,
            SourceCacheContext cache, string dest, Action<string> log)
        {
            log($"[NuGet] ? Downloading {pkg.Id} {pkg.Version}...");
            var resource = await repo.GetResourceAsync<FindPackageByIdResource>();
            using var ms = new MemoryStream();
            if (!await resource.CopyNupkgToStreamAsync(pkg.Id, pkg.Version, ms, cache, NullLogger.Instance, CancellationToken.None))
                throw new Exception($"Failed to download {pkg.Id} {pkg.Version}");
            ms.Position = 0;
            using var fs = File.Create(dest);
            await ms.CopyToAsync(fs);
        }

        /// <summary>
        /// Compile-time references: prefers ref/ over lib/ (best compatible TFM).
        /// </summary>
        private static List<string> ExtractCompileAssets(PackageArchiveReader reader, string runtimeDirectory)
        {
            var provider = DefaultCompatibilityProvider.Instance;

            // Try ref/ first (compile-time contract)
            var refItems = reader.GetItems("ref").ToList();
            var best = PickBestFrameworkGroup(refItems, provider);

            // Fall back to lib/ if no ref/ group matched
            if (best == null)
            {
                var libItems = reader.GetLibItems().ToList();
                best = PickBestFrameworkGroup(libItems, provider);
            }

            if (best == null) return new List<string>();

            return ExtractDllsFromGroup(reader, best, runtimeDirectory);
        }

        /// <summary>
        /// Runtime managed DLLs: runtimes/{RID}/lib/{TFM} (RID fallback chain).
        /// Returns empty if no RID-specific managed DLLs exist (caller falls back to lib/).
        /// </summary>
        private static List<string> ExtractRuntimeManagedDlls(PackageArchiveReader reader, string runtimeDirectory)
        {
            var provider = DefaultCompatibilityProvider.Instance;
            var allFiles = reader.GetFiles().ToList();

            foreach (var rid in RidFallbacks)
            {
                var prefix = $"runtimes/{rid}/lib/";
                var ridFiles = allFiles
                    .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (ridFiles.Count == 0) continue;

                // Group by TFM folder
                var grouped = ridFiles
                    .GroupBy(f =>
                    {
                        var rest = f.Substring(prefix.Length);
                        var slash = rest.IndexOf('/');
                        return slash > 0 ? rest.Substring(0, slash) : rest;
                    })
                    .Select(g => new FrameworkSpecificGroup(
                        NuGetFramework.ParseFolder(g.Key),
                        g.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                var best = PickBestFrameworkGroup(grouped, provider);
                if (best == null) continue;

                var result = ExtractDllsFromGroup(reader, best, runtimeDirectory);
                if (result.Count > 0) return result;
            }

            return new List<string>();
        }

        /// <summary>
        /// Native assets: runtimes/{RID}/native (RID fallback chain).
        /// Extracts .dll / .exe / .so / .dylib.
        /// </summary>
        private static void ExtractNativeAssets(PackageArchiveReader reader, string runtimeDirectory)
        {
            var allFiles = reader.GetFiles().ToList();

            foreach (var rid in RidFallbacks)
            {
                var prefix = $"runtimes/{rid}/native/";
                var candidates = allFiles
                    .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var file in candidates)
                {
                    var fileName = Path.GetFileName(file);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    var ext = Path.GetExtension(fileName).ToLowerInvariant();
                    if (ext != ".dll" && ext != ".exe" && ext != ".so" && ext != ".dylib") continue;

                    var dest = Path.Combine(runtimeDirectory, fileName);
                    try
                    {
                        using var input = reader.GetEntry(file).Open();
                        using var output = File.Create(dest);
                        input.CopyTo(output);
                    }
                    catch (Exception ex)
                    {
                        // Non-fatal: log but don't stop restore
                        Console.Error.WriteLine($"[NuGet] ⚠ Could not extract native asset '{fileName}': {ex.Message}");
                    }
                }

                // Stop at first RID that had native files (most specific wins)
                if (candidates.Count > 0) break;
            }
        }

        private static FrameworkSpecificGroup? PickBestFrameworkGroup(
            IEnumerable<FrameworkSpecificGroup> groups,
            IFrameworkCompatibilityProvider provider)
        {
            return groups
                .Where(g =>
                    g.TargetFramework == NuGetFramework.AnyFramework ||
                    provider.IsCompatible(HostFramework, g.TargetFramework))
                .OrderByDescending(g => g.TargetFramework == HostFramework)
                .ThenByDescending(g => g.TargetFramework == NuGetFramework.AnyFramework)
                .ThenByDescending(g => g.TargetFramework.Version)
                .FirstOrDefault();
        }

        private static List<string> ExtractDllsFromGroup(
            PackageArchiveReader reader,
            FrameworkSpecificGroup group,
            string runtimeDirectory)
        {
            var result = new List<string>();
            foreach (var item in group.Items)
            {
                if (!item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(runtimeDirectory, Path.GetFileName(item));
                try
                {
                    using var stream = reader.GetEntry(item).Open();
                    using var file = File.Create(dest);
                    stream.CopyTo(file);
                    result.Add(dest);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to extract '{Path.GetFileName(item)}': {ex.Message}", ex);
                }
            }
            return result;
        }

        // -- COMPILATION -------------------------------------------------

        private static List<MetadataReference> BuildReferences(IEnumerable<string> extraDllPaths)
        {
            var refs = new List<MetadataReference>();
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrWhiteSpace(tpa))
                foreach (var path in tpa.Split(Path.PathSeparator))
                { try { if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path)); } catch { } }
            else
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                { try { if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location)) refs.Add(MetadataReference.CreateFromFile(asm.Location)); } catch { } }

            var hostAsm = typeof(EventTools).Assembly;
            if (!string.IsNullOrEmpty(hostAsm.Location))
                refs.Add(MetadataReference.CreateFromFile(hostAsm.Location));

            foreach (var dll in extraDllPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            { try { if (File.Exists(dll)) refs.Add(MetadataReference.CreateFromFile(dll)); } catch { } }

            return refs;
        }

        public static async Task<(Assembly? assembly, string error)> CompileToolAsync(
            string name, IEnumerable<string> extraDllPaths, Action<string> log)
        {
            if (_assemblyCache.TryGetValue(name, out var cached)) return (cached, "");
            log($"[Roslyn] Compiling '{name}'...");

            var modelCode = SafeRead(Path.Combine(ToolsDir, $"{name}.cs"));
            var workflowCode = SafeRead(Path.Combine(WorkFlowsDir, $"{name}_WorkFlow.cs"));
            if (string.IsNullOrWhiteSpace(modelCode) || string.IsNullOrWhiteSpace(workflowCode))
                return (null, $"Missing source files for '{name}'.");

            var references = BuildReferences(extraDllPaths);
            var compilation = CSharpCompilation.Create(
                $"DynamicTool_{name}_{Guid.NewGuid():N}",
                new[] { CSharpSyntaxTree.ParseText(modelCode), CSharpSyntaxTree.ParseText(workflowCode) },
                references.Distinct(new MetadataReferenceComparer()),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var runtimeDir = GetToolRuntimeDirectory(name);
            Directory.CreateDirectory(runtimeDir);
            var toolDll = Path.Combine(runtimeDir, $"{name}.dll");

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (!emitResult.Success)
            {
                var err = string.Join(Environment.NewLine, emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                log($"[Roslyn] ? Compilation failed:\n{err}");
                return (null, err);
            }

            ms.Position = 0;
            using (var fs = File.Create(toolDll)) ms.CopyTo(fs);

            var context = _loadContexts.GetOrAdd(name, _ => new ToolLoadContext(runtimeDir, HostTpaNames));
            foreach (var dll in extraDllPaths) if (File.Exists(dll)) context.RegisterAssembly(dll);

            var assembly = context.LoadFromAssemblyPath(toolDll);
            _assemblyCache[name] = assembly;
            log($"[Roslyn] ? '{name}' compiled successfully.");
            return (assembly, "");
        }

        public static async Task<(string dllPath, string error)> CompileForDebugAsync(
            string name, IEnumerable<string> extraDllPaths, Action<string> log)
        {
            log($"[Roslyn] Compiling '{name}' for DEBUG...");

            var modelCode = SafeRead(Path.Combine(ToolsDir, $"{name}.cs"));
            var workflowCode = SafeRead(Path.Combine(WorkFlowsDir, $"{name}_WorkFlow.cs"));
            if (string.IsNullOrWhiteSpace(modelCode) || string.IsNullOrWhiteSpace(workflowCode))
                return (null!, $"Missing source files for '{name}'.");

            var dbgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".dbg", name);
            Directory.CreateDirectory(dbgDir);

            var modelFilePath = Path.Combine(dbgDir, $"{name}.cs");
            var workflowFilePath = Path.Combine(dbgDir, $"{name}_WorkFlow.cs");
            File.WriteAllText(modelFilePath, modelCode);
            File.WriteAllText(workflowFilePath, workflowCode);

            var extraList = extraDllPaths.ToList();
            var references = BuildReferences(extraList);

            foreach (var dll in extraList)
            { if (!File.Exists(dll)) continue; var dest = Path.Combine(dbgDir, Path.GetFileName(dll)); if (!File.Exists(dest)) File.Copy(dll, dest, true); }

            var runtimeDir = GetToolRuntimeDirectory(name);
            if (Directory.Exists(runtimeDir))
                foreach (var f in Directory.GetFiles(runtimeDir))
                { try { File.Copy(f, Path.Combine(dbgDir, Path.GetFileName(f)), true); } catch { } }

            var hostDll = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(hostDll))
                try { File.Copy(hostDll, Path.Combine(dbgDir, Path.GetFileName(hostDll)), true); } catch { }

            var modelText = Microsoft.CodeAnalysis.Text.SourceText.From(modelCode, System.Text.Encoding.UTF8);
            var workflowText = Microsoft.CodeAnalysis.Text.SourceText.From(workflowCode, System.Text.Encoding.UTF8);

            var compilation = CSharpCompilation.Create(
                $"DynamicTool_{name}_{Guid.NewGuid():N}",
                new[] { CSharpSyntaxTree.ParseText(modelText, path: modelFilePath), CSharpSyntaxTree.ParseText(workflowText, path: workflowFilePath) },
                references.Distinct(new MetadataReferenceComparer()),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithOptimizationLevel(OptimizationLevel.Debug));

            var dllDest = Path.Combine(dbgDir, $"{name}.dll");
            var pdbDest = Path.Combine(dbgDir, $"{name}.pdb");
            var emitOptions = new Microsoft.CodeAnalysis.Emit.EmitOptions(
                debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb);

            using var peStream = File.Create(dllDest);
            using var pdbStream = File.Create(pdbDest);
            var result = compilation.Emit(peStream, pdbStream, options: emitOptions);

            if (!result.Success)
            {
                var err = string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                log($"[Roslyn] ? Debug Compilation failed:\n{err}");
                return (null!, err);
            }

            log($"[Roslyn] ? '{name}' compiled for DEBUG successfully.");
            return (dllDest, "");
        }

        // -- SCHEMA / RUN -------------------------------------------------

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
                .FirstOrDefault(t => t.GetMethod("Start", BindingFlags.Public | BindingFlags.Static) != null)
                ?? throw new Exception("WorkFlow class with public static Start() not found.");

            var modelType = assembly.GetTypes()
                .FirstOrDefault(t => t.IsSubclassOf(typeof(EventTools)))
                ?? throw new Exception("Tool model class inheriting EventTools not found.");

            var startMethod = workflowType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!;
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(processData);
            var typedConfig = Newtonsoft.Json.JsonConvert.DeserializeObject(json, modelType);

            try
            {
                var resultObj = startMethod.Invoke(null, new[] { typedConfig });
                if (resultObj is Task task)
                {
                    await task;
                    return task.GetType().GetProperty("Result")?.GetValue(task);
                }
                return resultObj;
            }
            catch (TargetInvocationException ex) { throw new Exception(ex.InnerException?.Message ?? ex.Message); }
        }

        // -- HELPERS ------------------------------------------------------

        private static string SafeRead(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
        private static void TryDelete(string path) { if (File.Exists(path)) File.Delete(path); }

        private static List<string> LoadPackageList(string name)
        {
            var path = Path.Combine(PackagesDir, $"{name}.json");
            if (!File.Exists(path)) return new List<string>();
            return Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path)) ?? new List<string>();
        }

        private static void SavePackageList(string name, List<string> packages)
        {
            File.WriteAllText(Path.Combine(PackagesDir, $"{name}.json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(packages, Newtonsoft.Json.Formatting.Indented));
        }
    }

    // -- TOOL LOAD CONTEXT ------------------------------------------------

    internal sealed class ToolLoadContext : AssemblyLoadContext
    {
        private readonly string _runtimeDirectory;
        private readonly HashSet<string> _hostTpaNames;
        private readonly Dictionary<string, string> _registered = new(StringComparer.OrdinalIgnoreCase);

        public ToolLoadContext(string runtimeDirectory, HashSet<string> hostTpaNames)
            : base($"DeepOceanTool_{Path.GetFileName(runtimeDirectory)}", isCollectible: true)
        {
            _runtimeDirectory = runtimeDirectory;
            _hostTpaNames = hostTpaNames;
            Directory.CreateDirectory(_runtimeDirectory);
        }

        public void RegisterAssembly(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var n = AssemblyName.GetAssemblyName(path).Name;
                if (!string.IsNullOrWhiteSpace(n)) _registered[n] = path;
            }
            catch { /* Non-managed DLL or native binary – skip silently */ }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Delegate host/TPA assemblies to default context (preserves type identity)
            if (_hostTpaNames.Contains(name)) return null;

            if (_registered.TryGetValue(name, out var p))
            {
                try { return LoadFromAssemblyPath(p); }
                catch (Exception ex) { throw new Exception($"ToolLoadContext: failed to load registered assembly '{name}' from '{p}': {ex.Message}", ex); }
            }

            var local = Path.Combine(_runtimeDirectory, $"{name}.dll");
            if (File.Exists(local))
            {
                try { return LoadFromAssemblyPath(local); }
                catch (Exception ex) { throw new Exception($"ToolLoadContext: failed to load '{name}' from runtime dir: {ex.Message}", ex); }
            }

            return null;
        }
    }

    // -- METADATA REFERENCE COMPARER --------------------------------------

    internal sealed class MetadataReferenceComparer : IEqualityComparer<MetadataReference>
    {
        public bool Equals(MetadataReference? x, MetadataReference? y)
            => string.Equals(x?.Display, y?.Display, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(MetadataReference obj)
            => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Display ?? "");
    }

    // -- CUSTOM TOOL FILES ------------------------------------------------

    public class CustomToolFiles
    {
        public string Name { get; set; } = "";
        public string ModelCode { get; set; } = "";
        public string WorkFlowCode { get; set; } = "";
        public List<string> Packages { get; set; } = new();
    }
}
