using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO.Pipes;

namespace LicenseServer
{
    public partial class MainForm : Form
    {
        #region 程序入口

        [STAThread]
        static void Main(string[] args)
        {
            // 新增：注册应用程序退出事件，兜底停止服务
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                var form = new MainForm();
                form.StopServer();
                form.WriteColor("程序退出，已停止验证服务", ConsoleColor.Yellow);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm form = new MainForm();

            // ====== 新增：解析管道模式参数 ======
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].ToLower() == "-pipemode" || args[i].ToLower() == "/pipemode")   // 解析管道模式指令
                {
                    form._isPipeMode = true;
                    // 👇 替换原有代码：使用主进程传递的管道句柄创建流（更稳定）
                    string pipeReadHandle = args[i + 1];  // 读句柄
                    string pipeWriteHandle = args[i + 2]; // 写句柄
                    form._pipeReader = new StreamReader(
                        new AnonymousPipeClientStream(PipeDirection.In, pipeReadHandle),
                        Encoding.UTF8);
                    form._pipeWriter = new StreamWriter(
                        new AnonymousPipeClientStream(PipeDirection.Out, pipeWriteHandle),
                        Encoding.UTF8);
                    form._pipeWriter.AutoFlush = true;
                    i += 2; // 跳过管道句柄参数
                    break;
                }
            }

            // 解析命令行参数
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "-action":
                    case "/action":
                        if (i + 1 < args.Length)
                        {
                            form._action = args[i + 1];
                            i++;
                        }
                        break;
                    case "-licensekey":
                    case "/licensekey":
                        if (i + 1 < args.Length)
                        {
                            form._licenseKey = args[i + 1];
                            i++;
                        }
                        break;
                    case "-verifytype":
                    case "/verifytype":
                        if (i + 1 < args.Length)
                        {
                            form._verifyType = args[i + 1].ToLower();
                            i++;
                        }
                        break;
                    case "-port":
                    case "/port":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int port)) //
                        {
                            form._port = port;
                            i++;
                        }
                        break;
                    case "-limitdate":
                    case "/limitdate":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int limitdate)) //
                        {
                            form.limitdate = limitdate;
                            i++;
                        }
                        break;
                    // 新增：指定远程API完整URL（优先级最高）
                    case "-apiurl":
                    case "/apiurl":
                        if (i + 1 < args.Length)
                        {
                            form._ApiUrl = args[i + 1].TrimEnd('/'); // 移除末尾/，避免拼接错误
                            i++;
                        }
                        break;
                    case "-silentmode":
                    case "/silentmode":
                        form.silentMode = true;
                        // 解析到参数后，直接设置窗口隐藏属性
                        form.Visible = false;
                        form.ShowInTaskbar = false;
                        form.Opacity = 0;
                        form.WindowState = FormWindowState.Minimized;
                        break;
                }
            }


            // 程序退出码：0-成功，1-失败，2-未知错误
            int exitCode = 1;
            try
            {

                // ====== 新增：管道模式处理（仅处理verify） ======
                if (form._isPipeMode)
                {
                    form.HandlePipeVerifyMode(); // 处理管道验证
                    return; // 管道模式执行完直接退出，不走原有CLI逻辑
                }

                if (form._action == "交互模式")
                {
                    Application.Run(form);
                    return;
                }

                switch (form._action.ToLower())
                {
                    case "start":
                        var startRes = form.StartServer();
                        form.WriteColor(startRes.Msg, startRes.Success ? ConsoleColor.Green : ConsoleColor.Red);
                        if (!startRes.Success)
                        {
                            exitCode = 0;
                            Environment.Exit(exitCode);
                        }
                        break; // 修复case贯穿
                    case "stop":
                        var stopRes = form.StopServer();
                        form.WriteColor(stopRes.Msg, ConsoleColor.Yellow);
                        if (!stopRes.Success)
                        {
                            exitCode = 0;
                        }
                        break; // 修复case贯穿
                    case "status":
                        string? pid = form.GetServerPid();
                        if (string.IsNullOrEmpty(pid))
                        {
                            form.WriteColor("服务未运行", ConsoleColor.Red);
                            exitCode = 0;
                        }
                        try
                        {
                            Process.GetProcessById(int.Parse(pid));
                            form.WriteColor("服务运行中", ConsoleColor.Green);
                            exitCode = 0;
                        }
                        catch
                        {
                            form.WriteColor("服务未运行", ConsoleColor.Red);
                            exitCode = 1;
                        }
                        Environment.Exit(exitCode);
                        break; // 修复case贯穿
                    case "machineid":
                        var machineRes = form.GetMachineId();
                        if (machineRes.Success && !string.IsNullOrEmpty(machineRes.MachineId))
                        {
                            Console.WriteLine(machineRes.MachineId);
                            exitCode = 0;
                        }
                        else
                        {
                            form.WriteColor(machineRes.Msg, ConsoleColor.Red);
                            exitCode = 1;
                        }
                        break; // 修复case贯穿
                    // ====== 修改后的 case "verify" 分支 ======
                    case "verify":
                        // 验证模式隐藏主窗口,只影响form实例这个主窗口。（不影响输入窗口form实例的方法中的inputForm实例 和 弹窗）
                        form.Visible = false;   // 隐藏主窗口（可见性）
                        form.ShowInTaskbar = false; // 隐藏主窗口（任务栏）
                        form.Opacity = 0;   // 隐藏主窗口（透明度为0）
                        form.WindowState = FormWindowState.Minimized;   // 隐藏主窗口（最小化）

                        (bool Success, string Msg) verifyResult;
                        if (string.IsNullOrEmpty(form._licenseKey))     // 未指定许可证密钥
                        {
                            while (true)    // 未指定许可证密钥，持续展示验证对话框，直到成功或取消
                            {
                                // 仅展示输入验证对话框
                                verifyResult = form.ShowVerifyDialog();
                                exitCode = verifyResult.Success ? 0 : 1;
                                MessageBox.Show(verifyResult.Msg, "验证结果", MessageBoxButtons.OK, verifyResult.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                                form.WriteColor(verifyResult.Msg, verifyResult.Success ? ConsoleColor.Green : ConsoleColor.Red);
                                if (verifyResult.Success || verifyResult.Msg.Contains("已取消验证"))
                                {
                                    break;
                                }
                            }
                        }
                        else    // 有指定密钥，直接验证
                        {
                            // 有指定密钥，直接验证
                            verifyResult = form.WithKeyVerify();
                            exitCode = verifyResult.Success ? 0 : 1;
                            MessageBox.Show(verifyResult.Msg, "验证结果", MessageBoxButtons.OK, verifyResult.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                            form.WriteColor(verifyResult.Msg, verifyResult.Success ? ConsoleColor.Green : ConsoleColor.Red);
                        }
                        break;
                    default:
                        form.WriteColor("无效的Action参数，支持：start/stop/verify/status/machineid/交互模式", ConsoleColor.Red);
                        break; // 修复case贯穿
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"程序错误：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                form.WriteColor($"程序错误：{ex.Message}", ConsoleColor.Red);
                exitCode = 2;
            }
            finally
            {
                form.StopServer();      // 除了启动和查看状态，其他操作退出前都需要停止服务
                Environment.Exit(exitCode);
            }
        }
        #endregion
    }
}