using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DeepOcean.Deploy.Services
{
    /// <summary>
    /// Manages a single debug session:
    ///  Browser (DAP JSON via WebSocket) ↔ DebugSessionManager ↔ netcoredbg (DAP JSON via stdin/stdout)
    /// </summary>
    public static class DebugSessionManager
    {
        private static string NetCoreDbgPath
        {
            get
            {
                var basePath = AppDomain.CurrentDomain.BaseDirectory;
                // Try bin directory first
                var path1 = Path.Combine(basePath, "netcoredbg", "netcoredbg", "netcoredbg.exe");
                if (File.Exists(path1)) return path1;
                // Try project root directory
                var path2 = Path.Combine(basePath, "..", "..", "..", "netcoredbg", "netcoredbg", "netcoredbg.exe");
                return Path.GetFullPath(path2);
            }
        }

        // ToolRunner exe path
        private static string ToolRunnerPath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", 
            "DeepOcean.Deploy.ToolRunner", "bin", "Debug", "net9.0", "DeepOcean.Deploy.ToolRunner.exe");

        private static Process _dbgProcess;
        private static CancellationTokenSource _cts;
        private static Func<string, Task> _sendToClientAsync;
        private static readonly object _lock = new object();

        public static bool IsRunning => _dbgProcess != null && !_dbgProcess.HasExited;

        /// <summary>
        /// Called when the browser connects to /api/debug-ws
        /// Starts netcoredbg and proxies messages between browser and debugger.
        /// </summary>
        public static async Task StartSessionAsync(string toolName, string jsonConfig, Func<string, Task> sendToClientAsync)
        {
            lock (_lock)
            {
                // Kill any existing session
                if (_dbgProcess != null && !_dbgProcess.HasExited)
                {
                    _dbgProcess.Kill();
                    _dbgProcess = null;
                }
                _sendToClientAsync = sendToClientAsync;
                _cts = new CancellationTokenSource();
            }

            try
            {
                // 1. Find the compiled DLL
                string dbgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".dbg", toolName);
                string dllPath = Path.Combine(dbgDir, $"{toolName}.dll");

                if (!File.Exists(dllPath))
                {
                    await _sendToClientAsync(JsonConvert.SerializeObject(new
                    {
                        type = "error",
                        message = $"Debug build not found for '{toolName}'. Please compile first."
                    }));
                    return;
                }

                // 2. Find the ToolRunner exe
                string toolRunnerExe = ToolRunnerPath;
                if (!File.Exists(toolRunnerExe))
                {
                    // Try current directory sibling
                    toolRunnerExe = Path.GetFullPath(Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "..", "DeepOcean.Deploy.ToolRunner", "bin", "Debug", "net9.0", "DeepOcean.Deploy.ToolRunner.exe"));
                }

                if (!File.Exists(toolRunnerExe))
                {
                    await _sendToClientAsync(JsonConvert.SerializeObject(new
                    {
                        type = "error",
                        message = $"ToolRunner not found. Expected at: {toolRunnerExe}"
                    }));
                    return;
                }

                if (!File.Exists(NetCoreDbgPath))
                {
                    await _sendToClientAsync(JsonConvert.SerializeObject(new
                    {
                        type = "error",
                        message = $"netcoredbg not found at: {NetCoreDbgPath}"
                    }));
                    return;
                }

                // Escape config for command line
                string escapedConfig = jsonConfig.Replace("\"", "\\\"");
                string toolRunnerArgs = $"\"{dllPath}\" \"{toolName}_WorkFlow\" \"{escapedConfig}\"";

                // 3. Start netcoredbg with ToolRunner as the debugee
                var psi = new ProcessStartInfo
                {
                    FileName = NetCoreDbgPath,
                    Arguments = $"--interpreter=vscode -- \"{toolRunnerExe}\" {toolRunnerArgs}",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _dbgProcess = new Process { StartInfo = psi };
                _dbgProcess.Start();

                await _sendToClientAsync(JsonConvert.SerializeObject(new
                {
                    type = "debugger_started",
                    message = "netcoredbg started. Waiting for DAP messages..."
                }));

                // 4. Start reading from netcoredbg and forwarding to browser
                _ = ReadFromDebuggerLoopAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                try
                {
                    await _sendToClientAsync(JsonConvert.SerializeObject(new
                    {
                        type = "error",
                        message = $"Debug session error: {ex.Message}"
                    }));
                }
                catch { }
                StopSession();
            }
        }

        /// <summary>
        /// Forwards a DAP message from the browser to netcoredbg stdin
        /// </summary>
        public static async Task SendToDebuggerAsync(string message)
        {
            if (_dbgProcess != null && !_dbgProcess.HasExited)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                var header = $"Content-Length: {bytes.Length}\r\n\r\n";
                var headerBytes = Encoding.UTF8.GetBytes(header);

                var stdin = _dbgProcess.StandardInput.BaseStream;
                await stdin.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stdin.WriteAsync(bytes, 0, bytes.Length);
                await stdin.FlushAsync();
            }
        }

        /// <summary>
        /// Reads DAP messages from netcoredbg stdout and forwards them to the browser WebSocket.
        /// DAP messages use HTTP-like headers: "Content-Length: N\r\n\r\n{json}"
        /// </summary>
        private static async Task ReadFromDebuggerLoopAsync(CancellationToken ct)
        {
            try
            {
                var stream = _dbgProcess.StandardOutput.BaseStream;
                while (!ct.IsCancellationRequested && !_dbgProcess.HasExited)
                {
                    // Read header
                    var header = await ReadLineFromStreamAsync(stream, ct);
                    if (header == null) break;

                    if (!header.StartsWith("Content-Length:")) continue;

                    int length = int.Parse(header.Substring("Content-Length:".Length).Trim());

                    // Read blank line
                    await ReadLineFromStreamAsync(stream, ct);

                    // Read body
                    var bodyBytes = new byte[length];
                    int totalRead = 0;
                    while (totalRead < length)
                    {
                        int read = await stream.ReadAsync(bodyBytes, totalRead, length - totalRead, ct);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    var json = Encoding.UTF8.GetString(bodyBytes, 0, totalRead);
                    await _sendToClientAsync(json);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[DebugSessionManager] ReadFromDebugger error: {ex.Message}");
            }
            finally
            {
                StopSession();
            }
        }

        private static async Task<string> ReadLineFromStreamAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buf, 0, 1, ct);
                if (read == 0) return null;
                char c = (char)buf[0];
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append(c);
            }
            return null;
        }

        public static void StopSession()
        {
            lock (_lock)
            {
                _cts?.Cancel();
                if (_dbgProcess != null && !_dbgProcess.HasExited)
                {
                    try { _dbgProcess.Kill(); } catch { }
                }
                _dbgProcess = null;
            }
        }
    }
}
