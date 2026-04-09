// CertificatePinning.cs
// 文件名: CertificatePinning.cs
// 使用说明: 
// 1. 将此文件添加到项目中
// 2. 替换 YOUR_PUBLIC_KEY_FINGERPRINT_HERE 为你的服务器公钥指纹
// 3. 在需要时取消 csproj 中的 <Compile Remove="CertificatePinning.cs" />
// 4. 使用 SimplePublicKeyPinningHandler 创建 HttpClient

using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertificatePinning.Security
{
    /// <summary>
    /// 简单的公钥锁定处理器
    /// 在系统证书验证的基础上增加公钥指纹验证
    /// </summary>
    public class SimplePublicKeyPinningHandler : HttpClientHandler
    {
        /// <summary>
        /// 服务器公钥指纹 (SHA256 Base64格式)
        /// 获取方法: openssl x509 -in server.crt -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | openssl enc -base64
        /// </summary>
        private const string SERVER_PUBLIC_KEY_FINGERPRINT = "YOUR_PUBLIC_KEY_FINGERPRINT_HERE";

        public SimplePublicKeyPinningHandler()
        {
            // 设置自定义证书验证回调
            this.ServerCertificateCustomValidationCallback = ValidateCertificateWithPublicKeyPinning;
        }

        /// <summary>
        /// 证书验证回调函数
        /// 1. 先让系统验证证书基本有效性
        /// 2. 再验证公钥指纹是否匹配
        /// </summary>
        private bool ValidateCertificateWithPublicKeyPinning(HttpRequestMessage requestMessage,
                                                            X509Certificate2 certificate,
                                                            X509Chain chain,
                                                            System.Net.Security.SslPolicyErrors sslPolicyErrors)
        {
            // 第一步: 使用系统验证结果
            // 系统会验证证书链、有效期、域名匹配、吊销状态等
            if (sslPolicyErrors != System.Net.Security.SslPolicyErrors.None)
            {
                // 系统验证失败，记录日志（可选）
                return false;
            }

            // 第二步: 验证公钥指纹
            try
            {
                string actualFingerprint = GetPublicKeyFingerprint(certificate);
                if (string.IsNullOrEmpty(actualFingerprint))
                {
                    return false; // 无法获取公钥指纹
                }

                // 比较公钥指纹
                bool fingerprintMatches = string.Equals(actualFingerprint,
                                                       SERVER_PUBLIC_KEY_FINGERPRINT,
                                                       StringComparison.Ordinal);

                if (!fingerprintMatches)
                {
                    // 公钥不匹配，可能是中间人攻击！
                    return false;
                }

                // 所有验证通过
                return true;
            }
            catch
            {
                // 计算指纹过程中发生异常，安全起见拒绝连接
                return false;
            }
        }

        /// <summary>
        /// 从证书中提取公钥并计算SHA256指纹
        /// 支持RSA和ECDSA证书（兼容.NET 6及以下）
        /// </summary>
        public static string GetPublicKeyFingerprint(X509Certificate2 certificate)
        {
            try
            {
                byte[] publicKeyBytes = null;

                // 1. 尝试获取RSA公钥（兼容低版本.NET）
                using (RSA rsaPublicKey = certificate.GetRSAPublicKey())
                {
                    if (rsaPublicKey != null)
                    {
                        publicKeyBytes = rsaPublicKey.ExportSubjectPublicKeyInfo();
                    }
                }

                // 2. RSA公钥不存在则尝试ECDSA
                if (publicKeyBytes == null)
                {
                    using (ECDsa ecdsaPublicKey = certificate.GetECDsaPublicKey())
                    {
                        if (ecdsaPublicKey != null)
                        {
                            publicKeyBytes = ecdsaPublicKey.ExportSubjectPublicKeyInfo();
                        }
                    }
                }

                // 公钥提取失败
                if (publicKeyBytes == null || publicKeyBytes.Length == 0)
                {
                    return null;
                }

                // 计算SHA256哈希
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(publicKeyBytes);
                    return Convert.ToBase64String(hash);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 用于创建和使用带有公钥锁定的HttpClient的工具类
    /// </summary>
    public static class SecureHttpClientFactory
    {
        /// <summary>
        /// 创建带有公钥锁定的HttpClient
        /// </summary>
        /// <returns>配置了公钥锁定的HttpClient实例</returns>
        public static HttpClient CreateClientWithPublicKeyPinning()
        {
            var handler = new SimplePublicKeyPinningHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                        System.Net.DecompressionMethods.Deflate,
                AllowAutoRedirect = false, // 禁止自动重定向，避免安全风险
                UseCookies = false, // 根据需求决定是否使用Cookie
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // 设置默认请求头
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SecureClient/1.0 (PublicKeyPinning)");
            client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

            return client;
        }

        /// <summary>
        /// 创建带有公钥锁定和自定义超时的HttpClient
        /// </summary>
        /// <param name="timeoutSeconds">超时时间（秒）</param>
        /// <returns>配置了公钥锁定的HttpClient实例</returns>
        public static HttpClient CreateClientWithPublicKeyPinning(int timeoutSeconds)
        {
            var client = CreateClientWithPublicKeyPinning();
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return client;
        }

        /// <summary>
        /// 从证书文件计算公钥指纹（兼容低版本.NET，抑制过时警告）
        /// </summary>
        /// <param name="certificatePath">证书文件路径(.crt或.cer)</param>
        /// <returns>Base64格式的SHA256公钥指纹</returns>
        public static string CalculatePublicKeyFingerprintFromFile(string certificatePath)
        {
            if (!File.Exists(certificatePath))
            {
                throw new FileNotFoundException($"证书文件不存在: {certificatePath}");
            }

            try
            {
                // 抑制SYSLIB0057警告（低版本.NET无需X509CertificateLoader）
#pragma warning disable SYSLIB0057
                var certificate = new X509Certificate2(certificatePath);
#pragma warning restore SYSLIB0057
                // 直接调用 SimplePublicKeyPinningHandler 里的方法
                return SimplePublicKeyPinningHandler.GetPublicKeyFingerprint(certificate);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法从证书文件计算公钥指纹: {ex.Message}", ex);
            }
        }
    }
}