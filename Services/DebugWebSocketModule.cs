using EmbedIO.WebSockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace DeepOcean.Deploy.Services
{
    /// <summary>
    /// WebSocket module for the debug session.
    /// Handles initial handshake (tool name + config), then delegates to DebugSessionManager.
    /// </summary>
    public class DebugWebSocketModule : WebSocketModule
    {
        public DebugWebSocketModule(string urlPath) : base(urlPath, true) { }

        protected override async Task OnMessageReceivedAsync(
            IWebSocketContext context,
            byte[] rxBuffer,
            IWebSocketReceiveResult rxResult)
        {
            var message = System.Text.Encoding.UTF8.GetString(rxBuffer, 0, rxBuffer.Length);

            JObject msg;
            try { msg = JObject.Parse(message); }
            catch { return; }

            var msgType = msg["type"]?.ToString();

            if (msgType == "start_debug")
            {
                // Browser sends: { type: "start_debug", toolName: "MyTool", config: {...} }
                var toolName = msg["toolName"]?.ToString();
                var configJson = msg["config"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";

                if (string.IsNullOrWhiteSpace(toolName))
                {
                    await SendAsync(context, JsonConvert.SerializeObject(new
                    {
                        type = "error",
                        message = "toolName is required."
                    }));
                    return;
                }

                // Delegate to DebugSessionManager
                await DebugSessionManager.StartSessionAsync(toolName, configJson, async (msg) => {
                    await SendAsync(context, msg);
                });
            }
            else if (msgType == "stop_debug")
            {
                DebugSessionManager.StopSession();
            }
            else
            {
                // Forward DAP packets (like setBreakpoints, continue, etc)
                await DebugSessionManager.SendToDebuggerAsync(message);
            }
        }

        protected override Task OnClientConnectedAsync(IWebSocketContext context)
        {
            Console.WriteLine($"[DebugWS] Client connected: {context.Id}");
            return Task.CompletedTask;
        }

        protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
        {
            Console.WriteLine($"[DebugWS] Client disconnected: {context.Id}");
            DebugSessionManager.StopSession();
            return Task.CompletedTask;
        }
    }
}
