using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.IO.Pipes;
// using Internal;
using Newtonsoft.Json;

namespace LicenseServer
{
    /// <summary>
    /// 匿名管道通信辅助类（仅处理verify模式）
    /// </summary>
    public static class PipeCommunication
    {
        /// <summary>
        /// 管道通信的验证请求模型
        /// </summary>
        public class VerifyRequest
        {
            public string VerifyType { get; set; } // local/remote
            public string ApiUrl { get; set; } // 远程API地址（仅remote模式）
            public string LicenseKey { get; set; } // 许可证密钥
        }

        /// <summary>
        /// 管道通信的验证响应模型
        /// </summary>
        public class VerifyResponse
        {
            public bool Success { get; set; }
            public string Msg { get; set; }
            // public string MachineId { get; set; } // 可选：返回机器码
        }

        /// <summary>
        /// 从管道读取验证请求
        /// </summary>
        /// <param name="pipeReader">管道读取流</param>
        /// <returns>验证请求</returns>
        public static VerifyRequest ReadVerifyRequest(StreamReader pipeReader)
        {
            try
            {
                // 读取管道中的JSON字符串（主进程写入的请求）
                string requestJson = pipeReader.ReadLine();
                if (string.IsNullOrEmpty(requestJson))
                {
                    throw new Exception("管道读取不到验证请求数据");
                }
                return JsonConvert.DeserializeObject<VerifyRequest>(requestJson);
            }
            catch (Exception ex)
            {
                throw new Exception($"解析管道请求失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 向管道写入验证结果
        /// </summary>
        /// <param name="pipeWriter">管道写入流</param>
        /// <param name="response">验证响应</param>
        public static void WriteVerifyResponse(StreamWriter pipeWriter, VerifyResponse response)
        {
            try
            {
                // 序列化响应为JSON并写入管道
                string responseJson = JsonConvert.SerializeObject(response);
                pipeWriter.WriteLine(responseJson);
                pipeWriter.Flush(); // 强制刷新，确保主进程能读取到
            }
            catch (Exception ex)
            {
                throw new Exception($"写入管道响应失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 处理管道模式的验证逻辑
        /// </summary>
        /// <param name="mainForm">主窗体实例（复用其验证方法）</param>
        /// <param name="request">管道请求</param>
        /// <returns>验证响应</returns>
        public static VerifyResponse HandlePipeVerify(MainForm mainForm, VerifyRequest request)
        {
            var response = new VerifyResponse();

            try
            {
                // 3. 走远程/本地验证逻辑
                (bool Success, string Msg) verifyRes;
                // 远程验证（复用原有RemoteVerifyLicense）
                mainForm._verifyType = request.VerifyType?.ToLower() ?? mainForm._verifyType;   // 覆盖验证类型
                mainForm._ApiUrl = request.ApiUrl?.TrimEnd('/') ?? mainForm._ApiUrl; // 覆盖API地址

                (bool Success, string Msg) verifyResult;
                if (string.IsNullOrEmpty(request.LicenseKey))     // 未指定许可证密钥
                {
                    while (true)    // 未指定许可证密钥，持续展示验证对话框，直到成功或取消
                    {
                        // 仅展示输入验证对话框
                        verifyResult = mainForm.ShowVerifyDialog();
                        MessageBox.Show(verifyResult.Msg, "验证结果", MessageBoxButtons.OK, verifyResult.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                        Console.WriteLine(verifyResult.Msg, verifyResult.Success ? ConsoleColor.Green : ConsoleColor.Red);
                        if (verifyResult.Success || verifyResult.Msg.Contains("已取消验证"))
                        {
                            break;
                        }
                    }
                }
                else    // 有指定密钥，直接验证
                {
                    // 有指定密钥，直接验证
                    verifyResult = mainForm.WithKeyVerify();
                    MessageBox.Show(verifyResult.Msg, "验证结果", MessageBoxButtons.OK, verifyResult.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                    Console.WriteLine(verifyResult.Msg, verifyResult.Success ? ConsoleColor.Green : ConsoleColor.Red);
                }

                response.Success = verifyResult.Success;
                response.Msg = verifyResult.Msg;
                return response;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Msg = $"管道验证异常：{ex.Message}";
                MessageBox.Show(response.Msg, "无法连接验证进程", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // response.MachineId = null;
                return response;
            }
        }
    }
}