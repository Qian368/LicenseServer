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
        #region 本地授权文件核心逻辑
        /// <summary>
        /// 本地授权文件模型（序列化后加密存储）
        /// </summary>
        private class LicenseFileModel
        {
            /// <summary>许可证密钥</summary>
            public string LicenseKey { get; set; } = "";
            /// <summary>绑定的机器码</summary>
            public string MachineId { get; set; } = "";
            /// <summary>许可证有效期</summary>
            public string ExpiresAt { get; set; } = "";
            /// <summary>许可证最大设备数</summary>
            public int MaxDevices { get; set; }
            /// <summary>当前绑定设备数</summary>
            public int CurrentDevices { get; set; }
            /// <summary>最后远程验证时间（用于定期强制校验）</summary>
            public DateTime LastRemoteVerifyTime { get; set; }
            /// <summary>授权文件签名（防止篡改）</summary>
            public string Signature { get; set; } = "";
            /// <summary>许可证绑定的所有设备名（逗号分隔）</summary>
            public string RelatedDeviceNames { get; set; } = ""; // 新增：关联设备名
        }

        /// <summary>
        /// 授权文件存储路径（程序目录下隐藏文件）
        /// </summary>
        private string LocalLicenseFilePath => Path.Combine(_scriptDir, "license.lic");

        /// <summary>
        /// 签名密钥（自定义，建议替换为随机32位字符串，防止授权文件被篡改）
        /// </summary>
        // private readonly string _licenseSignKey = "YourRandom32BitSignKey12345678"; // 需替换！
        private readonly string _licenseSignKey = "nY2LpD4qW9vSxqianyueduGzR6XwZyP5V"; // 已替换
        /// <summary>
        /// AES加密密钥（需替换为自定义32位字符串，AES-256要求密钥32字节）
        /// </summary>
        private readonly string _aesEncryptKey = "B3A5D2E7C4F1qianyuedu0F3E2D5A8C9"; // 必须32位

        /// <summary>
        /// 定期强制远程校验周期（天），避免本地文件永久有效
        /// </summary>
        private readonly int _forceRemoteCheckDays = 7;

        /// <summary>
        /// 生成授权文件签名（防止篡改）
        /// </summary>
        /// <param name="model">授权文件模型（排除Signature字段）</param>
        /// <returns>签名字符串</returns>
        private string GenerateLicenseSignature(LicenseFileModel model)
        {
            try
            {
                // 拼接待签名的原始字符串（新增 RelatedDeviceNames 字段）
                string rawData = $"{model.LicenseKey}|{model.MachineId}|{model.ExpiresAt}|{model.MaxDevices}|{model.CurrentDevices}|{model.LastRemoteVerifyTime:yyyy-MM-dd HH:mm:ss}|{model.RelatedDeviceNames}";
                using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_licenseSignKey)))
                {
                    byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                }
            }
            catch (Exception ex)
            {
                WriteColor($"生成授权签名失败：{ex.Message}", ConsoleColor.Red);
                return "";
            }
        }

        /// <summary>
        /// 验证授权文件签名是否有效
        /// </summary>
        /// <param name="model">授权文件模型</param>
        /// <returns>是否有效</returns>
        private bool VerifyLicenseSignature(LicenseFileModel model)
        {
            string expectedSign = GenerateLicenseSignature(model);
            return model.Signature == expectedSign;
        }

        // 示例AES加密（需新增AES工具方法）
        // 修改 EncryptAes 方法
        string EncryptAes(string plainText, string key)
        {
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Key = Encoding.UTF8.GetBytes(key.PadRight(32).Substring(0, 32));
                    aes.IV = new byte[16];
                    ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        using (StreamWriter sw = new StreamWriter(cs))
                        {
                            sw.Write(plainText);
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColor($"AES加密失败：{ex.Message}", ConsoleColor.Red);
                throw; // 抛出异常让上层处理
            }
        }

        // 修改 DecryptAes 方法
        string DecryptAes(string cipherText, string key)
        {
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Key = Encoding.UTF8.GetBytes(key.PadRight(32).Substring(0, 32));
                    aes.IV = new byte[16];
                    ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                    using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(cipherText)))
                    using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (StreamReader sr = new StreamReader(cs))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColor($"AES解密失败：{ex.Message}", ConsoleColor.Red);
                throw;
            }
        }

        /// <summary>
        /// 生成本地授权文件（远程验证成功后调用）
        /// </summary>
        /// <param name="licenseKey">许可证密钥</param>
        /// <param name="machineId">机器码</param>
        /// <param name="expiresAt">有效期</param>
        /// <param name="maxDevices">最大设备数</param>
        /// <param name="currentDevices">当前设备数</param>
        /// <param name="relatedDeviceNames">关联设备名（逗号分隔）</param> // 新增参数
        /// <returns>是否成功</returns>
        private bool GenerateLicenseFile(string licenseKey, string machineId, string expiresAt, int maxDevices, int currentDevices, string relatedDeviceNames)
        {
            try
            {
                // 构建授权模型（新增 RelatedDeviceNames 赋值）
                LicenseFileModel model = new LicenseFileModel
                {
                    LicenseKey = licenseKey,
                    MachineId = machineId,
                    ExpiresAt = expiresAt,
                    MaxDevices = maxDevices,
                    CurrentDevices = currentDevices,
                    LastRemoteVerifyTime = DateTime.Now,
                    RelatedDeviceNames = relatedDeviceNames // 赋值关联设备名
                };
                // 生成签名
                model.Signature = GenerateLicenseSignature(model);

                // 序列化 + 加密
                string jsonModel = JsonConvert.SerializeObject(model);
                string encryptContent = EncryptAes(jsonModel, _aesEncryptKey);

                // 写入文件
                File.WriteAllText(LocalLicenseFilePath, encryptContent, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                WriteColor($"生成授权文件失败：{ex.Message}", ConsoleColor.Red);
                return false;
            }
        }

        /// <summary>
        /// 验证本地授权文件是否有效
        /// </summary>
        /// <param name="machineId">本机机器码</param>
        /// <returns>验证结果+消息</returns>
        private (bool Success, string Msg) VerifyLocalLicenseFile(string machineId)
        {
            // 1. 检查文件是否存在
            if (!File.Exists(LocalLicenseFilePath))
            {
                return (false, "本地授权文件不存在");
            }

            try
            {
                /* // 2. 读取并解密文件
                string encryptStr = File.ReadAllText(LocalLicenseFilePath, Encoding.UTF8);
                string jsonStr = Encoding.UTF8.GetString(Convert.FromBase64String(encryptStr)); */
                // 2. 读取并AES解密文件
                string encryptStr = File.ReadAllText(LocalLicenseFilePath, Encoding.UTF8);
                string jsonStr = DecryptAes(encryptStr, _aesEncryptKey); // 调用AES解密
                LicenseFileModel model = JsonConvert.DeserializeObject<LicenseFileModel>(jsonStr)!;

                // 3. 验证签名（防止篡改）
                if (!VerifyLicenseSignature(model))
                {
                    File.Delete(LocalLicenseFilePath); // 篡改文件直接删除
                    return (false, "本地授权文件已被篡改，无效");
                }

                // 4. 验证机器码匹配
                if (model.MachineId != machineId)
                {
                    return (false, "本地授权文件与本机机器码不匹配");
                }

                // 5. 验证有效期
                if (!string.IsNullOrEmpty(model.ExpiresAt) && model.ExpiresAt != "永久")
                {
                    DateTime expiresTimeUtc = DateTime.Parse(model.ExpiresAt).ToUniversalTime();
                    if (expiresTimeUtc < DateTime.UtcNow)
                    {
                        File.Delete(LocalLicenseFilePath); // 过期文件直接删除
                        return (false, "本地授权文件已过期");
                    }
                }

                // 6. 检查是否需要强制远程校验（超过周期）
                TimeSpan lastCheckSpan = DateTime.Now - model.LastRemoteVerifyTime;
                if (lastCheckSpan.TotalDays > _forceRemoteCheckDays)
                {
                    return (false, $"本地授权文件已超过{_forceRemoteCheckDays}天未远程校验，需重新验证");
                }

                // 7. 验证通过，拼接成功消息
                string successMsg = $"验证通过！\n" +
                        $"许可证key：{model.LicenseKey}\n" +
                        $"机器码：{model.MachineId}\n" +
                        $"用户：{Environment.MachineName}\n" +
                        $"有效期：{model.ExpiresAt}\n" +
                        $"当前许可证绑定设备：{model.CurrentDevices}/{model.MaxDevices}\n" +
                        $"关联设备列表：{model.RelatedDeviceNames}\n";
                return (true, successMsg);
            }
            catch (Exception ex)
            {
                File.Delete(LocalLicenseFilePath); // 解析失败删除无效文件
                return (false, $"本地授权文件解析失败：{ex.Message}");
            }
        }
        #endregion

        /// <summary>
        /// 读取本地授权文件中的扩展信息（许可证key、关联设备名等）
        /// </summary>
        /// <returns>授权信息模型 / 失败返回null</returns>
        private LicenseFileModel? ReadLicenseFileInfo()         // 备用方法
        {
            try
            {
                if (!File.Exists(LocalLicenseFilePath))
                {
                    return null;
                }
                // 解密 + 反序列化
                string encryptContent = File.ReadAllText(LocalLicenseFilePath, Encoding.UTF8);
                string jsonModel = DecryptAes(encryptContent, _aesEncryptKey);
                LicenseFileModel model = JsonConvert.DeserializeObject<LicenseFileModel>(jsonModel)!;

                // 验证签名（防止篡改）
                if (!VerifyLicenseSignature(model))
                {
                    WriteColor("授权文件已被篡改", ConsoleColor.Red);
                    return null;
                }
                return model;
            }
            catch (Exception ex)
            {
                WriteColor($"读取授权文件失败：{ex.Message}", ConsoleColor.Red);
                return null;
            }
        }

    }
}