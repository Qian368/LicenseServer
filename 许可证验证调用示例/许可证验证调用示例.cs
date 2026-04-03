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
            public string LicenseKey { get; set; }
            public string VerifyType { get; set; }
            public string ApiUrl { get; set; }
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
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "publish\\LicenseServer.exe",       // 你的子程序路径
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

                Console.WriteLine("✅ 已启动LicenseServer管道模式，开始发送验证请求...");

                // --------------------------
                // 4. 构建验证请求（和原有CLI逻辑一致）
                // --------------------------
                var verifyRequest = new PipeCommunication.VerifyRequest
                {
                    LicenseKey = "你的许可证密钥",  // 替换为真实密钥
                    VerifyType = "local",           // local / remote
                    ApiUrl = "http://127.0.0.1:8090"  // 远程模式才需要填写，本地模式可以不填
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
                // 6. 读取子程序返回的验证结果
                // --------------------------
                var verifyResult = new PipeCommunication.VerifyResponse();  // 初始化验证结果
                using (StreamReader sr = new StreamReader(pipeIn, Encoding.UTF8))
                {
                    string resultJson = sr.ReadLine();
                    verifyResult = JsonConvert.DeserializeObject<PipeCommunication.VerifyResponse>(resultJson);
                }

                // 输出结果
                Console.WriteLine("==================================");
                Console.WriteLine($"验证结果：{(verifyResult.Success ? "成功 ✅" : "失败 ❌")}");
                Console.WriteLine($"提示信息：{verifyResult.Msg}");
                // Console.WriteLine($"机器码：{verifyResult.MachineId ?? "获取失败"}");
                Console.WriteLine("==================================");

                if (verifyResult.Success)
                {
                    Console.WriteLine($"✅ 验证成功，许可证有效 \n 欢迎使用主程序！");
                }
                else
                {
                    Console.WriteLine($"❌ 验证失败，许可证无效 \n 请检查许可证密钥是否正确。");
                    process.WaitForExit();  // 等待子程序退出
                    Console.WriteLine("\n程序执行完毕，按任意键退出...");
                    Console.ReadKey();
                }
            }

            catch (Exception ex)
            {
                Console.WriteLine($"❌ 主程序错误：{ex.Message}");
                Console.ReadKey();
            }
        }
    }
}