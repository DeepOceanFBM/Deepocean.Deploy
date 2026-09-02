using DeepOcean.Deploy.Extensions;
using DeepOcean.Deploy.Services;
using DeepOcean.Deploy.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DeepOcean.Deploy
{
    public class WorkFlowLogic
    {
        public async Task<ServiceResponseModel<object>> RunProjects(List<ProjectConfig> Projects)
        {
            foreach (ProjectConfig Project in Projects)
            {
                //bool GoNextProject = true;

                await SentLogs("Init Project : " + Project.ProjectName);


                foreach (var Processe in Project.Processes)
                {
                    JObject procJObj = Processe as JObject ?? JObject.FromObject(Processe);
                    string typeName = procJObj["Type"]?.ToString();

                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var classType = GetClassType(typeName);

                        if (!classType.Success)
                        {
                            return new ServiceResponseModel<object>()
                            {
                                Success = false,
                                CodeStatus = 404,
                            };
                        }

                        ServiceResponseModel<object> Res = await CallMethodProcesse(Processe, classType.Data);

                        if (Res == null)
                        {
                            // Error Frmate Object
                            return new ServiceResponseModel<object>()
                            {
                                Success = false,
                                Message = $"Method Call is Fil | Project Name : {Project.ProjectName} -> Processe : {classType.Data}",
                                CodeStatus = 500,
                            };
                        }

                        if ((Res != null && !Res.Success))
                        {
                            string Message = $"Method Call is Fil | Project Name : {Project.ProjectName} -> Processe : {classType.Data} | Message : {Res.Message}";
                            await SentLogs(Message);
                            //Not Fount or Not Call Method System
                            return new ServiceResponseModel<object>()
                            {
                                Success = false,
                                Message = Message,
                                CodeStatus = 500,
                            };
                        }

                        //Add Set Logs 
                        await SentLogs($"Done Processe : {classType.Data} In Project : {Project.ProjectName}");
                    }
                    else
                    {
                        string Message = $"Not Match Processe Config | Project Name : {Project.ProjectName}";

                        await SentLogs(Message);

                        return new ServiceResponseModel<object>
                        {
                            Success = false,
                            CodeStatus = 404,
                            Message = Message,
                        };
                    }
                }
            }

            return new ServiceResponseModel<object>() { Message = "", Success = true, CodeStatus = 200 };
        }

        private static async Task<ServiceResponseModel<object>> CallMethodProcesse(object Processe, Type? classType)
        {
            JObject procJObj = Processe as JObject ?? JObject.FromObject(Processe);
            string typeName = procJObj["Type"]?.ToString() ?? "";

            // --- Custom Tool (Roslyn) path ---
            if (classType == null)
            {
                try
                {
                    var result = await RoslynCompilerService.RunCustomToolAsync(
                        typeName,
                        Processe,
                        msg => DeployController.AddLog(msg));

                    return new ServiceResponseModel<object>()
                    {
                        Success = true,
                        Data = result,
                        Message = $"Done Run Custom Processe {typeName}",
                        CodeStatus = 200,
                    };
                }
                catch (Exception ex)
                {
                    return new ServiceResponseModel<object>()
                    {
                        Success = false,
                        Message = $"Custom tool error: {ex.Message}",
                        CodeStatus = 500,
                    };
                }
            }

            // --- Built-in tool path ---
            var OrgProcesse = JsonConvert.DeserializeObject(
                JsonConvert.SerializeObject(Processe),
                classType);


            if (OrgProcesse == null)
            {
                return new ServiceResponseModel<object>()
                {
                    Success = false,
                    Message = $"Not Match Processe Config | Class Type: {classType}",
                    CodeStatus = 500,
                };
            }

            if (OrgProcesse is EventTools Tools)
            {
                var ClassName = $"DeepOcean.Deploy.WorkFlow.{Tools.Type}_WorkFlow";
                var res = await DeepOcean.Deploy.Extensions.RefMethod.InvokeStaticMethodAsync(ClassName, "Start", OrgProcesse);
                return new ServiceResponseModel<object>()
                {
                    Success = res != null,
                    Data = res ?? default,
                    Message = (res == null ? $"Fil Run Processe " : $"Done Run Processe ") + classType,
                    CodeStatus = res == null ? 500 : 200,
                };
            }
            else
            {
                // Not Found Main Tools 
                return new ServiceResponseModel<object>()
                {
                    CodeStatus = 404,
                    Success = false,
                    Message = $"Not Found EventsTools"
                };
            }
        }

        private ServiceResponseModel<Type?> GetClassType(string typeName)
        {
            // First: look in built-in assembly
            string fullTypeName = $"DeepOcean.Deploy.Tools.{typeName}";
            var assembly = typeof(EventTools).Assembly;
            Type? classType = assembly.GetType(fullTypeName);

            // If not found built-in, check if a custom script file exists
            if (classType == null)
            {
                var customToolsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "CustomScripts", "Tools");
                var customFile = System.IO.Path.Combine(customToolsDir, $"{typeName}.cs");
                // Return null type to signal Roslyn path
                bool hasCustomScript = System.IO.File.Exists(customFile);
                return new ServiceResponseModel<Type?>()
                {
                    Data = null,
                    Success = hasCustomScript,
                    Message = hasCustomScript ? "" : $"Tool '{typeName}' not found in built-in assembly or CustomScripts.",
                };
            }

            return new ServiceResponseModel<Type?>()
            {
                Data = classType,
                Success = true,
                Message = "",
            };
        }

        private async Task<object> SentLogs(string Message)
        {
            DeployController.AddLog(Message);
            return new object();
        }


    }
}
