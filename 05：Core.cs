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
        private void WriteColor(string message, ConsoleColor color = ConsoleColor.White)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.ResetColor();
        }
        #region 数据库文件加密解密（复用授权文件加密逻辑）
        // 数据库文件加密标识（用于判断加密状态）
        private const string DB_ENCRYPT_FLAG = "DB_ENCRYPTED_V1:";

        /// <summary>
        /// 加密data.db文件（覆盖原文件）
        /// </summary>
        /// <param name="machineId">本机机器码（用于生成加密密钥）</param>
        /// <param name="dbFilePath">数据库文件路径（_dataDbPath）</param>
        /// <returns>是否加密成功</returns>
        private bool EncryptDbFile(string machineId, string dbFilePath)
        {
            try
            {
                // 1. 检查文件是否存在
                if (!File.Exists(dbFilePath))
                {
                    Debug.WriteLine("数据库文件不存在");
                    return true;
                }

                // 【新增】先检查是否已加密（防止重复加密）
                string existingContent = File.ReadAllText(dbFilePath, Encoding.UTF8);
                if (existingContent.StartsWith(DB_ENCRYPT_FLAG))
                {
                    Debug.WriteLine("数据库文件已加密，无需重复加密");
                    return true; // 已加密直接返回成功，避免重复操作
                }

                // 2. 读取数据库文件字节流
                byte[] dbBytes = File.ReadAllBytes(dbFilePath);
                // 转换为Base64字符串（二进制转文本，便于字符移位和AES处理）
                string dbBase64 = Convert.ToBase64String(dbBytes);

                // 3. 复用现有增强版加密逻辑（字符移位 + AES）
                string encryptedContent = EncryptAesEnhanced(dbBase64, machineId);

                // 【新增】拼接加密标识
                string finalEncryptedContent = DB_ENCRYPT_FLAG + encryptedContent;

                // 4. 覆盖写入加密后的内容（替换原db文件）
                File.WriteAllText(dbFilePath, finalEncryptedContent, Encoding.UTF8);

                Debug.WriteLine("数据库文件加密成功");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加密数据库文件失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 解密data.db文件（覆盖原文件）
        /// </summary>
        /// <param name="machineId">本机机器码（用于生成解密密钥）</param>
        /// <param name="dbFilePath">数据库文件路径（_dataDbPath）</param>
        /// <returns>是否解密成功</returns>
        private bool DecryptDbFile(string machineId, string dbFilePath)
        {
            try
            {
                // 1. 检查文件是否存在
                if (!File.Exists(dbFilePath))
                {
                    Debug.WriteLine("数据库文件不存在");
                    return true;
                }

                // 2. 读取加密后的文本内容
                string encryptedContent = File.ReadAllText(dbFilePath, Encoding.UTF8);

                // 【新增】校验加密标识
                if (!encryptedContent.StartsWith(DB_ENCRYPT_FLAG))
                {
                    Debug.WriteLine("数据库文件无合法加密标识（未加密/已篡改/重复解密），解密失败");
                    return true;
                }

                // 【新增】截取标识后的真实密文
                string realEncryptedContent = encryptedContent.Substring(DB_ENCRYPT_FLAG.Length);

                // 3. 复用现有增强版解密逻辑（AES + 字符移位还原）
                string dbBase64 = DecryptAesEnhanced(realEncryptedContent, machineId);

                // 4. Base64转回二进制字节流
                byte[] dbBytes = Convert.FromBase64String(dbBase64);
                // 5. 覆盖写入解密后的二进制数据（恢复原db文件）
                File.WriteAllBytes(dbFilePath, dbBytes);

                Debug.WriteLine("数据库文件解密成功");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"解密数据库文件失败：{ex.Message}");
                return false;
            }
        }
        #endregion


        #region 核心方法：验证前置方法
        private string? GetServerPid()
        {
            if (File.Exists(_lockFile))
            {
                string pid = File.ReadAllText(_lockFile).Trim();
                try
                {
                    Process.GetProcessById(int.Parse(pid));
                    return pid;
                }
                catch
                {
                    File.Delete(_lockFile);
                }
            }
            return null;
        }

        private (bool Success, string Msg) StartServer()
        {
            string? pid = GetServerPid();
            if (!string.IsNullOrEmpty(pid))
            {
                return (true, $"验证服务已在运行 (PID: {pid})");
            }

            if (!File.Exists(_pbExePath))
            {
                return (false, $"错误：未找到 pocketbase.exe 文件");
            }

            try
            {
                // 获取机器ID
                var machineResult = GetMachineId();
                if (!machineResult.Success || string.IsNullOrEmpty(machineResult.MachineId))
                {
                    return (false, machineResult.Msg);
                }
                // 解密数据库文件
                if (!DecryptDbFile(machineResult.MachineId, _dataDbPath))
                {
                    return (false, "数据库解密失败");
                }

                // 启动PocketBase服务
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = _pbExePath,
                    Arguments = $"serve --http=localhost:{_port} --dir=\"{_dataDir}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                Process? proc = Process.Start(psi);
                if (proc == null)
                {
                    return (false, "启动PocketBase失败：进程创建为空");
                }
                File.WriteAllText(_lockFile, proc.Id.ToString(), Encoding.UTF8);

                using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) })
                {
                    for (int i = 1; i <= 15; i++)
                    {
                        try
                        {
                            HttpResponseMessage resp = client.GetAsync($"{_LocalApiUrl}/api/health").Result;
                            if (resp.IsSuccessStatusCode)
                            {
                                return (true, $"服务启动成功 (PID: {proc.Id})");
                            }
                        }
                        catch { Thread.Sleep(500); }
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, $"启动服务异常：{ex.Message}");
            }

            StopServer();
            return (false, $"服务启动超时，请检查端口{_port}是否被占用");
        }

        private (bool Success, string Msg) StopServer()
        {
            string? pid = GetServerPid();
            if (!string.IsNullOrEmpty(pid))
            {
                try
                {
                    // 信号停止服务
                    Process.GetProcessById(int.Parse(pid)).Kill();
                    File.Delete(_lockFile);

                    // 获取机器ID
                    var machineResult = GetMachineId();
                    if (!machineResult.Success || string.IsNullOrEmpty(machineResult.MachineId))
                    {
                        return (false, machineResult.Msg);
                    }
                    // 加密数据库文件
                    if (!EncryptDbFile(machineResult.MachineId, _dataDbPath))
                    {
                        return (false, "数据库加密失败");
                    }
                    return (true, $"服务已停止（PID: {pid}）");
                }
                catch (Exception ex)
                {
                    File.Delete(_lockFile);
                    return (true, $"停止服务失败：{ex.Message}，已清理锁文件");
                }
            }
            return (true, "服务未运行，无需停止");
        }

        internal (bool Success, string? MachineId, string Msg) GetMachineId()
        // 从硬件信息生成机器码
        {
            try
            {
                List<string> ids = new List<string>();

                // CPU序列号
                ManagementObjectSearcher cpuSearcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
                foreach (ManagementObject mo in cpuSearcher.Get())
                {
                    string? cpuId = mo["ProcessorId"]?.ToString();
                    if (!string.IsNullOrEmpty(cpuId)) { ids.Add(cpuId); break; }
                }

                // 主板序列号
                ManagementObjectSearcher boardSearcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (ManagementObject mo in boardSearcher.Get())
                {
                    string? boardId = mo["SerialNumber"]?.ToString();
                    if (!string.IsNullOrEmpty(boardId)) { ids.Add(boardId); break; }
                }

                /* // 磁盘序列号    //节省应用大小
                ManagementObjectSearcher diskSearcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive WHERE Index=0");
                foreach (ManagementObject mo in diskSearcher.Get())
                {
                    string? diskId = mo["SerialNumber"]?.ToString();
                    if (!string.IsNullOrEmpty(diskId)) { ids.Add(diskId); break; }
                } */

                if (ids.Count == 0)
                {
                    return (false, null, "无法获取硬件信息生成机器码");
                }

                string raw = string.Join("|", ids);
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    string hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                    string machineId = hash.Substring(0, 16);
                    return (true, machineId, string.Empty);
                }
            }
            catch (Exception ex)
            {
                return (false, null, $"获取机器码失败：{ex.Message}\n请尝试用管理员权限运行应用");
            }
        }

        private DateTime GetServerTime(HttpResponseMessage? licenseResp = null)
        // 从服务器响应头获取时间，若为空则返回本地时间
        {
            try
            {
                if (licenseResp != null && licenseResp.Headers.Date.HasValue)
                {
                    return licenseResp.Headers.Date.Value.LocalDateTime;
                }

                using (HttpClient client = new HttpClient())
                {
                    // 改用RemoteApiUrl获取服务器时间
                    HttpResponseMessage resp = client.GetAsync($"{RemoteApiUrl}/api/health").Result;
                    if (resp.Headers.Date.HasValue)
                    {
                        return resp.Headers.Date.Value.LocalDateTime;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"无法获取服务器时间：{ex.Message}");
                // 返回空值
                return DateTime.MinValue;

            }
            return DateTime.Now;
        }


        /// <summary>
        /// 全局查询绑定该机器码的所有设备（跨所有许可证）
        /// </summary>
        /// <param name="machineId">本机机器码</param>
        /// <returns>设备列表 + 查询结果</returns>
        private (List<JToken> Devices, bool Success, string Msg) GetAllDevicesByMachineId(string machineId)
        {
            try
            {
                using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                    // 全局过滤：所有machine_id等于本机的设备（跨许可证）
                    string filter = $"?filter=(machine_id='{machineId}')&expand=license&perPage=100";
                    string devicesUrl = $"{RemoteApiUrl}/api/collections/devices/records{filter}";

                    HttpResponseMessage resp = client.GetAsync(devicesUrl).Result;
                    if (!resp.IsSuccessStatusCode)
                    {
                        return (new List<JToken>(), false, $"查询设备失败：{resp.StatusCode}");
                    }

                    string devicesJson = resp.Content.ReadAsStringAsync().Result;
                    JObject devicesData = JObject.Parse(devicesJson);

                    List<JToken> deviceList = new List<JToken>();
                    if (devicesData["totalItems"]?.Value<int>() > 0)
                    {
                        deviceList = ((JArray)devicesData["items"]).ToList();
                    }

                    return (deviceList, true, "查询成功");
                }
            }
            catch (Exception ex)
            {
                return (new List<JToken>(), false, $"全局查询设备异常：{ex.Message}");
            }
        }

        #endregion

        // ====== 新增：管道模式验证处理方法（完整实现） ======
        /// <summary>
        /// 处理匿名管道模式的许可证验证（仅verify，复用原有所有逻辑）
        /// </summary>
        private void HandlePipeVerifyMode()
        {
            try
            {
                // 1. 强制隐藏窗体（管道模式无主界面，静默运行）
                this.Visible = false;
                this.ShowInTaskbar = false;
                this.Opacity = 0;
                this.WindowState = FormWindowState.Minimized;

                // 2. 从匿名管道读取 主进程发送的验证请求(JSON)
                var verifyRequest = PipeCommunication.ReadVerifyRequest(_pipeReader);

                // 3. 核心：调用管道通信类，复用原有本地/远程验证逻辑
                var verifyResponse = PipeCommunication.HandlePipeVerify(this, verifyRequest);

                // 4. 将验证结果写回匿名管道，返回给主进程
                PipeCommunication.WriteVerifyResponse(_pipeWriter, verifyResponse);

                // 5. 验证完成，停止服务并退出程序
                StopServer();
                Environment.Exit(verifyResponse.Success ? 0 : 1);
            }
            catch (Exception ex)
            {
                // 异常处理：生成错误响应，写回管道
                var errorResponse = new PipeCommunication.VerifyResponse
                {
                    Success = false,
                    Msg = $"管道验证异常：{ex.Message}",
                    // MachineId = null
                };

                // 写入异常结果
                PipeCommunication.WriteVerifyResponse(_pipeWriter, errorResponse);

                // 停止服务并退出
                StopServer();
                Environment.Exit(1);
            }
            finally
            {
                // 释放管道流资源（安全兜底）
                _pipeReader?.Dispose();
                _pipeWriter?.Dispose();
            }
        }


        /// <summary>
        /// 对外公开的远程验证API（供应用程序调用）
        /// </summary>
        /// <param name="licenseKey">许可证密钥</param>
        /// <param name="apiUrl">远程API地址（可选，为空则用配置的默认值）</param>
        /// <returns>验证结果（JSON格式，便于解析）</returns>
        public string RemoteVerifyApi(string licenseKey, string apiUrl = "")
        {
            // 临时覆盖远程API地址（不修改全局配置）
            string oldApiUrl = _ApiUrl;     // 存放全局地址
            if (!string.IsNullOrEmpty(apiUrl))
            {
                _ApiUrl = apiUrl.TrimEnd('/');
            }

            try
            {
                // 获取机器码
                var machineResult = GetMachineId();
                if (!machineResult.Success)
                {
                    return JsonConvert.SerializeObject(new { Success = false, Msg = machineResult.Msg });
                }

                // 调用远程验证
                var verifyResult = RemoteVerifyLicense(licenseKey, machineResult.MachineId!);
                return JsonConvert.SerializeObject(new
                {
                    Success = verifyResult.Success,
                    Msg = verifyResult.Msg,
                    MachineId = machineResult.MachineId
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Msg = $"API调用异常：{ex.Message}" });
            }
            finally
            {
                // 恢复原有配置
                _ApiUrl = oldApiUrl;        // 恢复全局地址
            }
        }

    }
}