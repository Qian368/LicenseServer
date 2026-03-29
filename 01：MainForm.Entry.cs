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
                        form.ShowInTaskbar = false;
                        form.WindowState = FormWindowState.Minimized;
                        form.Visible = false;
                        form.Opacity = 0;
                        break;
                }
            }

            try
            {
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
                        break; // 修复case贯穿
                    case "stop":
                        var stopRes = form.StopServer();
                        form.WriteColor(stopRes.Msg, ConsoleColor.Yellow);
                        break; // 修复case贯穿
                    case "status":
                        string? pid = form.GetServerPid();
                        if (string.IsNullOrEmpty(pid))
                        {
                            form.WriteColor("服务未运行", ConsoleColor.Red);
                            form.StopServer(); // 退出前停止服务
                            Environment.Exit(1);
                        }
                        try
                        {
                            Process.GetProcessById(int.Parse(pid));
                            form.WriteColor("服务运行中", ConsoleColor.Green);
                            form.StopServer(); // 退出前停止服务
                            Environment.Exit(0);
                        }
                        catch
                        {
                            form.WriteColor("服务未运行", ConsoleColor.Red);
                            form.StopServer(); // 退出前停止服务
                            Environment.Exit(1);
                        }
                        break; // 修复case贯穿
                    case "machineid":
                        var machineRes = form.GetMachineId();
                        if (machineRes.Success && !string.IsNullOrEmpty(machineRes.MachineId))
                        {
                            Console.WriteLine(machineRes.MachineId);
                        }
                        else
                        {
                            form.WriteColor(machineRes.Msg, ConsoleColor.Red);
                        }
                        break; // 修复case贯穿
                    // ====== 修改后的 case "verify" 分支 ======
                    case "verify":
                        // 验证模式隐藏主窗口,只影响form实例这个主窗口。（不影响输入窗口form实例的方法中的inputForm实例 和 弹窗）
                        form.Visible = false;
                        form.ShowInTaskbar = false;
                        form.Opacity = 0;

                        // ========== 新增：优先校验本地授权文件 ==========
                        var machineResult = form.GetMachineId();
                        if (machineResult.Success && !string.IsNullOrEmpty(machineResult.MachineId))
                        {
                            var localVerifyResult = form.VerifyLocalLicenseFile(machineResult.MachineId);
                            if (localVerifyResult.Success)
                            {
                                if (form.silentMode)
                                {
                                    Console.WriteLine(JsonConvert.SerializeObject(new { Success = true, Msg = localVerifyResult.Msg }));
                                    form.StopServer(); // 退出前停止服务
                                    Environment.Exit(0);
                                }
                                else
                                {
                                    MessageBox.Show(localVerifyResult.Msg, "验证结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                    form.StopServer(); // 退出前停止服务
                                    Environment.Exit(0);
                                }
                            }
                            form.WriteColor($"本地授权校验失败：{localVerifyResult.Msg}，将进行远程验证", ConsoleColor.Yellow);
                        }

                        if (string.IsNullOrEmpty(form._licenseKey))
                        {
                            // 仅展示输入验证对话框
                            var verifyResult = form.ShowVerifyDialog();
                            MessageBox.Show(verifyResult.Msg, "验证结果", MessageBoxButtons.OK, verifyResult.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                            break;
                        }
                        else
                        {
                            (bool Success, string Msg) verifyRes;
                            if (form._verifyType == "remote")
                            {
                                // 远程验证逻辑（不启动本地服务，直接调用远程API）
                                var machineResult2 = form.GetMachineId();
                                if (!machineResult2.Success || string.IsNullOrEmpty(machineResult2.MachineId))
                                {
                                    verifyRes = (false, machineResult2.Msg);
                                }
                                else
                                {
                                    verifyRes = form.RemoteVerifyLicense(form._licenseKey, machineResult2.MachineId);
                                }
                            }
                            else
                            {
                                // 本地验证逻辑（原有逻辑，启动本地服务后验证）
                                verifyRes = form.VerifyLicense(form._licenseKey);
                            }
                            if (form.silentMode)
                            {

                                if (verifyRes.Success)
                                {
                                    Console.WriteLine(JsonConvert.SerializeObject(new { Success = true, Msg = verifyRes.Msg }));
                                    form.StopServer(); // 退出前停止服务
                                    Environment.Exit(0);
                                }
                                else
                                {
                                    form.WriteColor(verifyRes.Msg, ConsoleColor.Red);
                                    form.StopServer(); // 退出前停止服务
                                    Environment.Exit(1);
                                }
                            }
                            else
                            {
                                MessageBox.Show(verifyRes.Msg, "验证结果", MessageBoxButtons.OK, verifyRes.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                                form.StopServer(); // 退出前停止服务                               
                                Environment.Exit(verifyRes.Success ? 0 : 1);    // 非静默模式也需要退出，否则程序会挂起
                            }

                            break; // 修复case贯穿
                        }
                    default:
                        form.WriteColor("无效的Action参数，支持：start/stop/verify/status/machineid/交互模式", ConsoleColor.Red);
                        break; // 修复case贯穿
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"程序错误：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                form.WriteColor($"程序错误：{ex.Message}", ConsoleColor.Red);
            }
        }
        #endregion
    }
}