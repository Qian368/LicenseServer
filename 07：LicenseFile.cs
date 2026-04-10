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
            public string RelatedDeviceNames { get; set; } = "";
            /// <summary>许可证有效期剩余天数</summary>
            public string ExpireWarnTip { get; set; } = "";
        }

        #region 新增：动态密钥生成（基于机器码）
        // 加密文件标识（开头拼接，用于判断加密状态）
        private const string ENCRYPT_FLAG = "LICENSE_ENCRYPTED_V1:";
        /// <summary>
        /// 基于机器码生成固定的签名密钥（32位）
        /// </summary>
        /// <param name="machineId">设备唯一机器码</param>
        /// <returns>32位签名密钥</returns>
        private string GenerateSignKeyFromMachineId(string machineId)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                // 机器码 + 固定盐值（轻量混淆，可自定义）
                string saltedMachineId = $"{machineId}_License_Saltqianyuedu1";
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(saltedMachineId));
                // 截取32位作为签名密钥
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLower().Substring(0, 32);
            }
        }

        /// <summary>
        /// 基于机器码生成固定的AES加密密钥（32位，满足AES-256要求）
        /// </summary>
        /// <param name="machineId">设备唯一机器码</param>
        /// <returns>32位AES密钥</returns>
        private string GenerateAesKeyFromMachineId(string machineId)
        {
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes("AES_Saltqianyuedu3456789")))
            {
                byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(machineId));
                // 截取32位作为AES密钥
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLower().Substring(0, 32);
            }
        }

        /// <summary>
        /// 基于AES密钥生成固定IV（16位，AES要求）
        /// </summary>
        /// <param name="aesKey">32位AES密钥</param>
        /// <returns>16位IV</returns>
        private byte[] GenerateAesIV(string aesKey)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] ivBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(aesKey));
                return ivBytes.Take(16).ToArray(); // IV固定16位
            }
        }
        #endregion

        #region 新增：个性化字符移位处理（增强破解难度）
        /// <summary>
        /// 加密前的自定义字符移位（每个字符ASCII码+5，循环处理）
        /// </summary>
        /// <param name="plainText">原始明文</param>
        /// <returns>移位后的字符串</returns>
        private string CustomCharShiftEncrypt(string plainText)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in plainText)
            {
                // 只处理可见字符（ASCII 32-126），其余不变
                if (c >= 32 && c <= 126)
                {
                    sb.Append((char)((c - 32 + 5) % 95 + 32));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 解密后的自定义字符移位（反向：每个字符ASCII码-5）
        /// </summary>
        /// <param name="shiftedText">移位后的字符串</param>
        /// <returns>原始明文</returns>
        private string CustomCharShiftDecrypt(string shiftedText)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in shiftedText)
            {
                if (c >= 32 && c <= 126)
                {
                    sb.Append((char)((c - 32 - 5 + 95) % 95 + 32));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
        #endregion

        /// <summary>
        /// 生成授权文件签名（防止篡改）
        /// </summary>
        /// <param name="model">授权文件模型（排除Signature字段）</param>
        /// <param name="machineId">机器码（用于生成签名密钥）</param>
        /// <returns>签名字符串</returns>
        private string GenerateLicenseSignature(LicenseFileModel model, string machineId)
        {
            try
            {
                // 拼接待签名的原始字符串
                string rawData = $"{model.LicenseKey}|{model.MachineId}|{model.ExpiresAt}|{model.MaxDevices}|{model.CurrentDevices}|{model.LastRemoteVerifyTime:yyyy-MM-dd HH:mm:ss}|{model.RelatedDeviceNames}";
                // 动态生成签名密钥（不再硬编码）
                string signKey = GenerateSignKeyFromMachineId(machineId);
                using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signKey)))
                {
                    byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"生成授权签名失败：{ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 验证授权文件签名是否有效
        /// </summary>
        /// <param name="model">授权文件模型</param>
        /// <param name="machineId">机器码</param>
        /// <returns>是否有效</returns>
        private bool VerifyLicenseSignature(LicenseFileModel model, string machineId)
        {
            string expectedSign = GenerateLicenseSignature(model, machineId);
            return model.Signature == expectedSign;
        }

        #region 修改：增强版AES加解密（个性化逻辑+动态密钥/IV）
        /// <summary>
        /// 增强版AES加密（自定义字符移位 + AES加密）
        /// </summary>
        /// <param name="plainText">原始明文</param>
        /// <param name="machineId">机器码（用于生成AES密钥）</param>
        /// <returns>加密后字符串</returns>
        string EncryptAesEnhanced(string plainText, string machineId)
        {
            try
            {
                // 步骤1：自定义字符移位（第一层混淆）
                string shiftedText = CustomCharShiftEncrypt(plainText);

                // 步骤2：动态生成AES密钥和IV（不再硬编码IV）
                string aesKey = GenerateAesKeyFromMachineId(machineId);
                byte[] keyBytes = Encoding.UTF8.GetBytes(aesKey);
                byte[] ivBytes = GenerateAesIV(aesKey);

                using (Aes aes = Aes.Create())
                {
                    aes.Key = keyBytes;
                    aes.IV = ivBytes;
                    aes.Mode = CipherMode.CBC; // 改用CBC模式（比ECB更安全）
                    aes.Padding = PaddingMode.PKCS7;

                    ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        using (StreamWriter sw = new StreamWriter(cs))
                        {
                            sw.Write(shiftedText);
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AES加密失败：{ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 增强版AES解密（AES解密 + 自定义字符移位还原）
        /// </summary>
        /// <param name="cipherText">加密后字符串</param>
        /// <param name="machineId">机器码（用于生成AES密钥）</param>
        /// <returns>原始明文</returns>
        string DecryptAesEnhanced(string cipherText, string machineId)
        {
            try
            {
                // 步骤1：动态生成AES密钥和IV
                string aesKey = GenerateAesKeyFromMachineId(machineId);
                byte[] keyBytes = Encoding.UTF8.GetBytes(aesKey);
                byte[] ivBytes = GenerateAesIV(aesKey);

                using (Aes aes = Aes.Create())
                {
                    aes.Key = keyBytes;
                    aes.IV = ivBytes;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                    using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(cipherText)))
                    using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (StreamReader sr = new StreamReader(cs))
                    {
                        // 步骤2：AES解密后，还原字符移位
                        string shiftedText = sr.ReadToEnd();
                        return CustomCharShiftDecrypt(shiftedText);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AES解密失败：{ex.Message}");
                throw;
            }
        }
        #endregion

        /// <summary>
        /// 生成本地授权文件（远程验证成功后调用）
        /// </summary>
        /// <param name="licenseKey">许可证密钥</param>
        /// <param name="machineId">机器码</param>
        /// <param name="expiresAt">有效期</param>
        /// <param name="maxDevices">最大设备数</param>
        /// <param name="currentDevices">当前设备数</param>
        /// <param name="relatedDeviceNames">关联设备名（逗号分隔）</param>
        /// <returns>是否成功</returns>
        private bool GenerateLicenseFile(string licenseKey, string machineId, string expiresAt, int maxDevices, int currentDevices, string relatedDeviceNames)
        {
            try
            {
                // 构建授权模型
                LicenseFileModel model = new LicenseFileModel
                {
                    LicenseKey = licenseKey,
                    MachineId = machineId,
                    ExpiresAt = expiresAt,
                    MaxDevices = maxDevices,
                    CurrentDevices = currentDevices,
                    LastRemoteVerifyTime = DateTime.Now,
                    RelatedDeviceNames = relatedDeviceNames
                };
                // 生成签名（传入机器码，动态生成签名密钥）
                model.Signature = GenerateLicenseSignature(model, machineId);

                // 序列化 + 增强版AES加密（不再用硬编码密钥）
                string jsonModel = JsonConvert.SerializeObject(model);
                string encryptContent = EncryptAesEnhanced(jsonModel, machineId);


                // 【新增】拼接加密标识（核心修改）
                string finalContent = ENCRYPT_FLAG + encryptContent;
                // 写入文件
                File.WriteAllText(LocalLicenseFilePath, finalContent, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"生成授权文件失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 验证本地授权文件是否有效
        /// </summary>
        /// <param name="machineId">本机机器码</param>
        /// <returns>验证结果+消息</returns>
        internal (bool Success, string Msg) VerifyLocalLicenseFile(string machineId)
        {
            // 1. 检查文件是否存在
            if (!File.Exists(LocalLicenseFilePath))
            {
                return (false, "本地授权文件不存在");
            }

            try
            {
                string fileContent = File.ReadAllText(LocalLicenseFilePath, Encoding.UTF8);

                // 【新增】第一步：校验加密标识
                if (!fileContent.StartsWith(ENCRYPT_FLAG))
                {
                    // 无标识 → 不是合法加密文件（可能是明文/篡改/重复加密）
                    File.Delete(LocalLicenseFilePath);
                    return (false, "本地授权文件无合法加密标识（可能是明文/已篡改/重复加密），已自动清理");
                }

                // 【新增】第二步：截取标识后的真实密文
                string encryptStr = fileContent.Substring(ENCRYPT_FLAG.Length);

                // 【原有逻辑】解密（注意：这里用截取后的encryptStr，不是原fileContent）
                string jsonStr = DecryptAesEnhanced(encryptStr, machineId);
                LicenseFileModel model = JsonConvert.DeserializeObject<LicenseFileModel>(jsonStr)!;

                // 3. 验证签名（动态密钥）
                if (!VerifyLicenseSignature(model, machineId))
                {
                    File.Delete(LocalLicenseFilePath);
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
                        File.Delete(LocalLicenseFilePath);
                        return (false, "本地授权文件已过期");
                    }
                    else
                    {
                        // 计算剩余天数
                        double remainingDays = (expiresTimeUtc - DateTime.UtcNow).TotalDays;
                        if (remainingDays > 0 && remainingDays <= limitdate)
                        {
                            model.ExpireWarnTip = $"⚠️ 警告：许可证即将过期！剩余{Math.Ceiling(remainingDays)}天，请联系管理员延期";
                        }
                    }
                }

                // 6. 检查是否需要强制远程校验
                TimeSpan lastCheckSpan = DateTime.Now - model.LastRemoteVerifyTime;
                if (lastCheckSpan.TotalDays > _forceRemoteCheckDays)
                {
                    return (false, $"本地授权文件已超过{_forceRemoteCheckDays}天未远程校验，需重新验证");
                }

                // 7. 验证通过
                string successMsg = $"本机已完成验证，信息如下：\n" +
                        $"许可证key：{model.LicenseKey}\n" +
                        $"机器码：{model.MachineId}\n" +
                        $"用户：{Environment.MachineName}\n" +
                        $"有效期：{model.ExpiresAt}\n" +
                        $"当前许可证绑定设备：{model.CurrentDevices}/{model.MaxDevices}\n" +
                        $"关联设备列表：{model.RelatedDeviceNames}\n" +
                        $"{model.ExpireWarnTip}\n";
                return (true, successMsg);
            }
            catch (Exception ex)
            {
                File.Delete(LocalLicenseFilePath);
                return (false, $"本地授权文件解析失败：{ex.Message}");
            }
        }
        #endregion

        /// <summary>
        /// 读取本地授权文件中的扩展信息
        /// </summary>
        /// <param name="machineId">本机机器码</param>
        /// <returns>授权信息模型 / 失败返回null</returns>
        private LicenseFileModel? ReadLicenseFileInfo(string machineId)
        {
            try
            {
                if (!File.Exists(LocalLicenseFilePath))
                {
                    return null;
                }
                string fileContent = File.ReadAllText(LocalLicenseFilePath, Encoding.UTF8);

                // 【新增】校验加密标识
                if (!fileContent.StartsWith(ENCRYPT_FLAG))
                {
                    Debug.WriteLine("授权文件无合法加密标识，判定为无效文件");
                    return null;
                }
                // 【新增】截取真实密文
                string encryptContent = fileContent.Substring(ENCRYPT_FLAG.Length);

                // 【原有逻辑】解密+验证签名
                string jsonModel = DecryptAesEnhanced(encryptContent, machineId);
                LicenseFileModel model = JsonConvert.DeserializeObject<LicenseFileModel>(jsonModel)!;

                // 验证签名
                if (!VerifyLicenseSignature(model, machineId))
                {
                    Debug.WriteLine("授权文件已被篡改");
                    return null;
                }
                return model;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取授权文件失败：{ex.Message}");
                return null;
            }
        }

    }
}