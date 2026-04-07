using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes; // 👈 必须加这一行！修复报错核心
using System.Text;
// using Internal;
using Newtonsoft.Json;

namespace LicenseServer.Caller
{
    // 👇 主程序直接复制这两个模型（无需引用子程序，解耦）
    public class PipeCommunication
    {
        public class VerifyRequest
        {
            public string VerifyType { get; set; }
            public string ApiUrl { get; set; }
            public string LicenseKey { get; set; }
            public string AppName { get; set; }
        }

        public class VerifyResponse
        {
            public bool Success { get; set; }
            public string Msg { get; set; }
            // public string MachineId { get; set; }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                // --------------------------
                // 1. 创建【匿名管道】（主进程端）
                // --------------------------
                using var pipeOut = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable); // 主写 -> 子读
                using var pipeIn = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);  // 主读 <- 子写

                // --------------------------
                // 2. 启动子程序（LicenseServer），传递管道句柄
                // --------------------------

                // 获取当前程序（调用示例）运行所在的目录
                string callerDir = AppDomain.CurrentDomain.BaseDirectory;

                // 动态构建子程序路径
                string childProcessPath = Path.Combine(callerDir, "LicenseServer\\LicenseServer.exe");

                /* // 可以不必需要，因为 LicenseServer.exe 是在 publish 目录下的
                // 如果在 publish 目录下没找到（例如是 dotnet run 开发阶段），则往上找项目编译的 Debug 目录
                if (!File.Exists(childProcessPath))
                {
                    // 退回到项目根目录，再进入主项目的 bin/Debug 目录
                    string projectRoot = Path.GetFullPath(Path.Combine(callerDir, "..\\..\\..\\..\\"));
                    childProcessPath = Path.Combine(projectRoot, "bin\\Debug\\net9.0-windows\\LicenseServer.exe");
                } */

                if (!File.Exists(childProcessPath))
                {
                    Console.WriteLine($"未找到 {childProcessPath} ，无法启动验证程序");
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = childProcessPath,          // 动态判断子程序路径
                    Arguments = "-pipemode",              // 启用管道模式指令
                    UseShellExecute = false,              // 必须关闭ShellExecute，否则会创建新窗口
                    CreateNoWindow = true,                // 静默无窗口
                    RedirectStandardInput = false,        // 管道模式禁用标准IO重定向
                    RedirectStandardOutput = false,       // 管道模式禁用标准IO重定向
                    RedirectStandardError = false         // 管道模式禁用标准IO重定向
                };

                // 获取管道客户端句柄，传递给子程序（核心：解决管道通信失败问题）
                string pipeOutHandle = pipeOut.GetClientHandleAsString();
                string pipeInHandle = pipeIn.GetClientHandleAsString();
                psi.Arguments += $" {pipeOutHandle} {pipeInHandle}";

                // --------------------------
                // 3. 启动子程序
                // --------------------------
                using Process process = Process.Start(psi);

                // 释放主进程的管道句柄（Windows管道通信必需），以保证子程序随主进程退出
                pipeOut.DisposeLocalCopyOfClientHandle();
                pipeIn.DisposeLocalCopyOfClientHandle();

                Console.WriteLine("已启动LicenseServer管道模式，开始发送验证请求...");

                // --------------------------
                // 4. 构建验证请求（和原有CLI逻辑一致）
                // --------------------------
                var verifyRequest = new PipeCommunication.VerifyRequest
                {
                    VerifyType = "local",           // 必选：local / remote
                    ApiUrl = "http://127.0.0.1:8090",  // 可选：默认为："http://127.0.0.1:8090"
                    LicenseKey = "",  // 可选；默认为空字符串，未填则弹窗提示输入
                    AppName = "MyApp"  // 可选；默认为："AppName"
                };

                // --------------------------
                // 5. 通过管道发送请求给子程序
                // --------------------------
                using (StreamWriter sw = new StreamWriter(pipeOut, Encoding.UTF8))
                {
                    sw.WriteLine(JsonConvert.SerializeObject(verifyRequest));
                    sw.Flush();
                }

                // --------------------------
                // 6. 读取子程序返回的验证结果，ReadLine自带阻塞等待子程序返回
                // --------------------------
                var verifyResult = new PipeCommunication.VerifyResponse();  // 初始化验证结果
                using (StreamReader sr = new StreamReader(pipeIn, Encoding.UTF8))
                {
                    string resultJson = sr.ReadLine();
                    verifyResult = JsonConvert.DeserializeObject<PipeCommunication.VerifyResponse>(resultJson);
                }


                /* // ==========================================================
                //  【不阻塞写法】在这里！循环监听，不阻塞、不死等
                // ==========================================================
                PipeCommunication.VerifyResponse verifyResult = null;
                string resultJson = null;

                using (StreamReader sr = new StreamReader(pipeIn, Encoding.UTF8))
                {
                    Debug.WriteLine("\n⌛ 等待子进程验证完成...（可等待用户操作）");

                    // 【真正正确的逻辑】
                    // 循环读取，直到有数据返回，不会立刻卡死
                    while (true)
                    {
                        // 延时100毫秒，不占CPU
                        Thread.Sleep(100);

                        // 检查管道是否有数据
                        if (pipeIn.IsConnected && pipeIn.CanRead)
                        {
                            // 尝试读一行（不会卡死，因为只有子进程写完才会读到）
                            resultJson = sr.ReadLine();

                            if (!string.IsNullOrEmpty(resultJson))
                            {
                                verifyResult = JsonConvert.DeserializeObject<PipeCommunication.VerifyResponse>(resultJson);
                                break; // 读到结果，退出循环
                            }
                        }

                        // 可选：子进程退出了就退出
                        if (process.HasExited)
                        {
                            Debug.WriteLine("❌ 子进程已退出");
                            break;
                        }
                    }
                } */


                // 输出结果
                Console.WriteLine("==================================");
                Console.WriteLine($"验证结果：{(verifyResult.Success ? "成功" : "失败")}");
                Console.WriteLine($"提示信息：{verifyResult.Msg}");
                // Console.WriteLine($"机器码：{verifyResult.MachineId ?? "获取失败"}");
                Console.WriteLine("==================================");

                if (verifyResult.Success)
                {
                    Console.WriteLine($"验证成功，许可证有效 \n 欢迎使用主程序！");
                }
                else
                {
                    Console.WriteLine($"验证失败，许可证无效 \n 请检查许可证密钥是否正确。");
                    process.WaitForExit();  // 等待子程序退出
                    Console.WriteLine("\n程序执行完毕，按任意键退出...");
                }
            }

            catch (Exception ex)
            {
                Console.WriteLine($"❌ 主程序错误：{ex.Message}");
            }
        }
    }
}