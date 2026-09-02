<div align="center">
  <h1>🌊 DeepOcean Deploy</h1>
  <p><strong>A powerful, one-click deployment and automation orchestrator.</strong></p>
  
  <p>
    <a href="https://github.com/Ahmed-Ashraf/DeepOcean.Deploy/issues"><img src="https://img.shields.io/github/issues/Ahmed-Ashraf/DeepOcean.Deploy" alt="Issues"></a>
    <a href="https://github.com/Ahmed-Ashraf/DeepOcean.Deploy/stargazers"><img src="https://img.shields.io/github/stars/Ahmed-Ashraf/DeepOcean.Deploy" alt="Stars"></a>
    <a href="https://github.com/Ahmed-Ashraf/DeepOcean.Deploy/network/members"><img src="https://img.shields.io/github/forks/Ahmed-Ashraf/DeepOcean.Deploy" alt="Forks"></a>
    <img src="https://img.shields.io/badge/Platform-.NET%209-blue" alt=".NET 9">
  </p>
</div>

## 📌 Overview

**DeepOcean Deploy** is an advanced local deployment and automation engine designed to eliminate the headache of managing large, multi-tier projects. 

Originally built to handle the complex, 10+ sub-project deployments of the massive *DeepOcean* ecosystem, this tool proved so effective that it evolved into a standalone, open-source orchestrator. Whether you are deploying a monolithic architecture, managing microservices, or simply looking for a robust general-purpose automation tool, DeepOcean Deploy turns hours of manual configuration into a literal **One-Click** operation.

---

## ✨ Features

- **🚀 One-Click Deployments:** Group multiple processes, tasks, and scripts into a single "Project" and run them sequentially with one click.
- **🧠 Dynamic Custom Tools (Roslyn):** Write your automation steps in pure C#. The engine dynamically compiles your code at runtime using Roslyn—no need to rebuild the main application!
- **🎨 Auto-Generated UI:** When you define properties in your custom C# tools, the dashboard automatically generates a nested visual form for them. Zero HTML/JS required.
- **📦 Runtime NuGet Restore:** Need a third-party library for a custom tool? Just reference it, and the engine will fetch the NuGet package on the fly before execution.
- **🔌 Plugin System (.CTP):** Easily share your automation scripts with others by Exporting/Importing Custom Tools as `.CTP` (Custom Tools Plugin) files.
- **📝 Real-Time Logging:** Monitor your deployment processes with a live, color-coded logging console right in the browser.

---

## 🛠️ How It Works

DeepOcean Deploy runs as a lightweight local server (powered by `EmbedIO`). It provides a beautiful, responsive web interface where you can orchestrate your deployment workflows.

### 1. The Tool Model (Configuration)
Create a C# class to define what inputs your tool needs. The UI will automatically read this class and generate a configuration form.

```csharp
namespace DeepOcean.Deploy.Tools
{
    public class DatabaseBackupTool : EventTools
    {
        public string ConnectionString { get; set; } = string.Empty;
        public bool BackupToCloud { get; set; }
    }
}
```

### 2. The WorkFlow (Execution)
Write the actual execution logic. The configuration filled out by the user in the UI is passed directly to your `Start` method!

```csharp
using DeepOcean.Deploy.Tools;

namespace DeepOcean.Deploy.WorkFlow
{
    internal class DatabaseBackupTool_WorkFlow
    {
        public static object Start(DatabaseBackupTool Config)
        {
            DeepOcean.Deploy.DeployController.AddLog($"Connecting to: {Config.ConnectionString}");
            
            if (Config.BackupToCloud) {
                // Automation logic here
            }
            
            return true;
        }
    }
}
```

---

## 🚀 Getting Started

### Prerequisites
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Running the Orchestrator
1. Clone the repository:
   ```bash
   git clone https://github.com/YourUsername/DeepOcean.Deploy.git
   ```
2. Navigate to the project directory:
   ```bash
   cd DeepOcean.Deploy
   ```
3. Run the application:
   ```bash
   dotnet run
   ```
4. Open your browser and navigate to: `http://localhost:5000`

---

## 🤝 Contributing

We welcome contributions! If you have ideas for new built-in tools, UI improvements, or core engine enhancements, feel free to open an issue or submit a Pull Request.

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

---

## 📜 License

Distributed under the MIT License. See `LICENSE` for more information.

---
<div align="center">
  <i>Made with ❤️ for developers who value their time.</i>
</div>
