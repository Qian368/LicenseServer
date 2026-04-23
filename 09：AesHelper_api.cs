using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class AesHelper
{
    // 固定根密钥（前后端必须一致）
    private static readonly string _rootKey = "X9$pR2!kQ7sG5dC2zF0jH6bN3aS8lK0p";
    private static readonly string _rootIv = "P7sD2gK5zB9nR1xJ";

    /// <summary>
    /// 绑定机器码的加密（别人偷了密文也无法在其他机器使用）
    /// </summary>
    public static string Encrypt(string plainText, string machineId)
    {
        try
        {
            // 机器码参与生成动态密钥 → 强绑定设备
            var keyBytes = CombineAndHash(Encoding.UTF8.GetBytes(_rootKey), Encoding.UTF8.GetBytes(machineId));
            var ivBytes = CombineAndHash(Encoding.UTF8.GetBytes(_rootIv), Encoding.UTF8.GetBytes(machineId));

            // 截取 AES 要求的标准长度
            byte[] aesKey = new byte[32];
            byte[] aesIv = new byte[16];
            Array.Copy(keyBytes, aesKey, 32);
            Array.Copy(ivBytes, aesIv, 16);

            using (Aes aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = aesIv;
                ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

                using (MemoryStream ms = new MemoryStream())
                using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                using (StreamWriter sw = new StreamWriter(cs))
                {
                    sw.Write(plainText);
                    sw.Flush();
                    cs.FlushFinalBlock();
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 绑定机器码的解密（必须同一台机器才能解开）
    /// </summary>
    public static string Decrypt(string cipherText, string machineId)
    {
        try
        {
            // 和加密用完全一样的动态密钥规则
            var keyBytes = CombineAndHash(Encoding.UTF8.GetBytes(_rootKey), Encoding.UTF8.GetBytes(machineId));
            var ivBytes = CombineAndHash(Encoding.UTF8.GetBytes(_rootIv), Encoding.UTF8.GetBytes(machineId));

            byte[] aesKey = new byte[32];
            byte[] aesIv = new byte[16];
            Array.Copy(keyBytes, aesKey, 32);
            Array.Copy(ivBytes, aesIv, 16);

            byte[] buffer = Convert.FromBase64String(cipherText);
            using (Aes aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = aesIv;
                ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

                using (MemoryStream ms = new MemoryStream(buffer))
                using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (StreamReader sr = new StreamReader(cs))
                {
                    return sr.ReadToEnd();
                }
            }
        }
        catch
        {
            return null;
        }
    }

    // 辅助：密钥 + 机器码 混合哈希 → 生成唯一设备密钥
    private static byte[] CombineAndHash(byte[] a, byte[] b)
    {
        byte[] combined = new byte[a.Length + b.Length];
        Array.Copy(a, 0, combined, 0, a.Length);
        Array.Copy(b, 0, combined, a.Length, b.Length);

        using (SHA256 sha = SHA256.Create())
        {
            return sha.ComputeHash(combined);
        }
    }
}