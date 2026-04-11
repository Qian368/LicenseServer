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

// using CertificatePinning.Security;   // 引入自定义的证书公钥锁定校验库


namespace LicenseServer
{
    public partial class MainForm : Form
    {
        #region 核心方法:验证

        /// <summary>
        /// 第一步：承载所有拉取的原始数据
        /// </summary>
        private class LicenseVerificationRawData
        {
            // 许可证基础信息
            public JObject LicenseData { get; set; } = new JObject();
            public JToken License { get; set; }
            public string LicenseId { get; set; } = "";
            public string LicenseKey { get; set; } = "";
            public bool LicenseIsActive { get; set; }
            public string LicenseExpiresAtStr { get; set; } = "";
            public int LicenseMaxDevices { get; set; }

            // 许可证关联的所有设备
            public JObject LicenseDevicesData { get; set; } = new JObject();
            public List<JToken> LicenseDeviceList { get; set; } = new List<JToken>();
            public int LicenseTotalDeviceCount { get; set; }

            // 本机绑定的所有设备（全局）
            public List<JToken> GlobalDeviceList { get; set; } = new List<JToken>();
            public bool GlobalDeviceQuerySuccess { get; set; }
            public string GlobalDeviceQueryMsg { get; set; } = "";

            // 服务端时间
            public DateTime ServerTime { get; set; }

            // 本机绑定的其他许可证Key
            public string BoundOtherLicenseKey { get; set; } = "未知";

            // 本机信息
            public string MachineId { get; set; } = "";
            public string ComputerName { get; set; } = Environment.MachineName;

            // 新增：记录用户原始输入的Key和自动找回状态
            public string InputKey { get; set; } = "";
            public bool IsAutoRecovered { get; set; }
        }

        /// <summary>
        /// 第二步：承载逻辑分析结果
        /// </summary>
        private class LicenseAnalysisResult
        {
            public bool Success { get; set; }
            public string Msg { get; set; } = "";
            public bool NeedAddDevice { get; set; } // 是否需要新增设备
            public bool NeedUpdateDeviceName { get; set; } // 是否需要更新设备名称
            public JToken TargetDevice { get; set; } // 需要更新的设备
            public List<string> RedundantDeviceIds { get; set; } = new List<string>(); // 新增：需要清理的冗余设备ID
            public string ExpiresAtDisplay { get; set; } = "永久"; // 有效期显示文本
            public string MultiLicenseTip { get; set; } = ""; // 多许可证提示
            public string ExpireWarnTip { get; set; } = ""; // 过期提醒
            public string RelatedDeviceNamesStr { get; set; } = "无"; // 关联设备名称字符串
            public int CurrentTotalDeviceCount { get; set; } // 许可证当前总设备数
            public string AutoRecoverTip { get; set; } = ""; // 新增：自动找回提示
        }


        // 全局常量，服务端客户端一致
        private const char FieldSeparator = '\u001F';   // 固定分隔符，不可见
        public class LicenseFullData
        {
            public string LicenseKey { get; set; } = string.Empty;
            public string MachineId { get; set; } = string.Empty;
            public string ComputerName { get; set; } = string.Empty;
            public string ExpiresAtDisplay { get; set; } = string.Empty;
            public int LicenseMaxDevices { get; set; }
            public int CurrentTotalDeviceCount { get; set; }
            public string RelatedDeviceNamesStr { get; set; } = string.Empty;
            public string MultiLicenseTip { get; set; } = string.Empty;
            public string ExpireWarnTip { get; set; } = string.Empty;
            public string AutoRecoverTip { get; set; } = string.Empty;
        }


        /// <summary>
        /// 第一步：一次性拉取所有需要的远程数据（仅请求，无业务判断）
        /// </summary>
        private LicenseVerificationRawData PullAllRequiredData(string key, string machineId)
        {
            var rawData = new LicenseVerificationRawData
            {
                InputKey = key,
                MachineId = machineId,
                ComputerName = Environment.MachineName
            };


            // using (HttpClient client = SecureHttpClientFactory.CreateClientWithPublicKeyPinning(10))     // 使用自定义工厂创建安全的HttpClient,会启用自动验证公钥是否匹配的功能
            // var handler = new SimplePublicKeyPinningHandler();   // 直接创建带公钥锁定的Handler（只要这一步，验证就会生效），后续正常创建HttpClient即可
            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                // 1. 先拉取本机绑定的所有设备（全局跨许可证）
                var globalDeviceResult = GetAllDevicesByMachineId(machineId);
                rawData.GlobalDeviceList = globalDeviceResult.Devices;
                rawData.GlobalDeviceQuerySuccess = globalDeviceResult.Success;
                rawData.GlobalDeviceQueryMsg = globalDeviceResult.Msg;

                string targetKeyToVerify = key; // 默认使用用户输入的key

                // 2. 综合查询：合并用户输入的Key和本机已绑定的所有License ID，统一查询并挑选最优
                var queryConditions = new List<string>();
                if (!string.IsNullOrEmpty(key))
                {
                    queryConditions.Add($"key='{key}'");
                }

                if (rawData.GlobalDeviceList.Count > 0)
                {
                    var boundLicenseIds = rawData.GlobalDeviceList
                        .Select(d => d["license"]?.ToString())
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Distinct()
                        .ToList();

                    foreach (var id in boundLicenseIds)
                    {
                        queryConditions.Add($"id='{id}'");
                    }
                }

                if (queryConditions.Count > 0)
                {
                    string combinedFilter = string.Join("||", queryConditions);
                    string licensesUrl = $"{RemoteApiUrl}/api/collections/licenses/records?filter=({combinedFilter})";

                    HttpResponseMessage licensesResp = client.GetAsync(licensesUrl).Result;
                    if (licensesResp.IsSuccessStatusCode)
                    {
                        string licensesJson = licensesResp.Content.ReadAsStringAsync().Result;
                        JObject licensesData = JObject.Parse(licensesJson);
                        var candidateLicenses = licensesData["items"] as JArray;

                        if (candidateLicenses != null && candidateLicenses.Count > 0)
                        {
                            // 核心优先级逻辑：挑选最优许可证
                            var bestLicense = candidateLicenses
                                .OrderByDescending(l => l["is_active"]?.Value<bool>() ?? false) // 优先级1：处于激活状态
                                .ThenByDescending(l => string.IsNullOrEmpty(l["expires_at"]?.ToString())) // 优先级2：永久有效（时间为空）
                                .ThenByDescending(l =>
                                {
                                    // 优先级3：有效期越晚越好
                                    string expStr = l["expires_at"]?.ToString();
                                    if (string.IsNullOrEmpty(expStr)) return DateTime.MaxValue;
                                    if (DateTime.TryParse(expStr, out DateTime exp)) return exp;
                                    return DateTime.MinValue;
                                })
                                .FirstOrDefault();

                            if (bestLicense != null)
                            {
                                string bestKey = bestLicense["key"]?.ToString() ?? "";

                                // 记录本机曾绑定的最优Key（用于提示）
                                var bestBoundLicense = candidateLicenses
                                    .Where(l => rawData.GlobalDeviceList.Any(d => d["license"]?.ToString() == l["id"]?.ToString()))
                                    .OrderByDescending(l => l["is_active"]?.Value<bool>() ?? false)
                                    .ThenByDescending(l => string.IsNullOrEmpty(l["expires_at"]?.ToString()))
                                    .ThenByDescending(l =>
                                    {
                                        string expStr = l["expires_at"]?.ToString();
                                        if (string.IsNullOrEmpty(expStr)) return DateTime.MaxValue;
                                        if (DateTime.TryParse(expStr, out DateTime exp)) return exp;
                                        return DateTime.MinValue;
                                    })
                                    .FirstOrDefault();

                                if (bestBoundLicense != null)
                                {
                                    rawData.BoundOtherLicenseKey = bestBoundLicense["key"]?.ToString() ?? "未知";
                                }

                                // 如果最优许可证的Key不是用户输入的Key，触发自动找回/纠正
                                if (string.IsNullOrEmpty(key) || key != bestKey)
                                {
                                    rawData.IsAutoRecovered = true;
                                    targetKeyToVerify = bestKey;
                                }
                            }
                        }
                    }
                }

                // 3. 拉取待验证的许可证基础信息 (使用 targetKeyToVerify)
                if (!string.IsNullOrEmpty(targetKeyToVerify))
                {
                    string licenseFilter = $"?filter=(key='{targetKeyToVerify}')&expand=devices&perPage=1";
                    string licenseUrl = $"{RemoteApiUrl}/api/collections/licenses/records{licenseFilter}";
                    HttpResponseMessage licenseResp = client.GetAsync(licenseUrl).Result;
                    if (licenseResp.IsSuccessStatusCode)
                    {
                        string licenseJson = licenseResp.Content.ReadAsStringAsync().Result;
                        rawData.LicenseData = JObject.Parse(licenseJson);
                        // 提取许可证核心字段
                        if (rawData.LicenseData["totalItems"]?.Value<int>() > 0)
                        {
                            rawData.License = rawData.LicenseData["items"]?[0];
                            rawData.LicenseId = rawData.License?["id"]?.ToString() ?? "";
                            rawData.LicenseKey = rawData.License?["key"]?.ToString() ?? "";
                            rawData.LicenseIsActive = rawData.License?["is_active"]?.Value<bool>() ?? false;
                            rawData.LicenseExpiresAtStr = rawData.License?["expires_at"]?.ToString() ?? "";
                            rawData.LicenseMaxDevices = rawData.License?["max_devices"]?.Value<int>() ?? 0;
                        }
                        // 获取服务端时间
                        rawData.ServerTime = GetServerTime(licenseResp);
                    }
                }

                // 4. 拉取该许可证下的所有设备
                if (!string.IsNullOrEmpty(rawData.LicenseId))
                {
                    string licenseDevicesFilter = $"?filter=(license.id='{rawData.LicenseId}')&perPage=100";
                    string licenseDevicesUrl = $"{RemoteApiUrl}/api/collections/devices/records{licenseDevicesFilter}";
                    HttpResponseMessage licenseDevicesResp = client.GetAsync(licenseDevicesUrl).Result;
                    if (licenseDevicesResp.IsSuccessStatusCode)
                    {
                        rawData.LicenseDevicesData = JObject.Parse(licenseDevicesResp.Content.ReadAsStringAsync().Result);
                        rawData.LicenseTotalDeviceCount = rawData.LicenseDevicesData["totalItems"]?.Value<int>() ?? 0;
                        if (rawData.LicenseTotalDeviceCount > 0)
                        {
                            rawData.LicenseDeviceList = ((JArray)rawData.LicenseDevicesData["items"]).ToList();
                        }
                    }
                }
            }

            return rawData;
        }


        /// <summary>
        /// 第二步：纯内存逻辑分析（无网络请求、无数据写入）
        /// </summary>
        private LicenseAnalysisResult AnalyzeLicenseData(LicenseVerificationRawData rawData, int maxLicensePerDevice)
        {
            var analysisResult = new LicenseAnalysisResult();

            // 基础校验：如果没有提供key，且没有自动找回
            if (string.IsNullOrEmpty(rawData.InputKey) && !rawData.IsAutoRecovered)
            {
                analysisResult.Success = false;
                analysisResult.Msg = "许可证密钥不能为空，且未检测到本机的历史绑定记录";
                return analysisResult;
            }

            // 基础校验：许可证是否存在
            if (rawData.LicenseData["totalItems"]?.Value<int>() == 0 || rawData.LicenseData["totalItems"] == null)
            {
                analysisResult.Success = false;
                analysisResult.Msg = "许可证不存在";
                return analysisResult;
            }

            // 许可证数据异常
            if (rawData.License == null)
            {
                analysisResult.Success = false;
                analysisResult.Msg = "许可证数据异常";
                return analysisResult;
            }

            // 许可证ID为空
            if (string.IsNullOrEmpty(rawData.LicenseId))
            {
                analysisResult.Success = false;
                analysisResult.Msg = "许可证ID为空";
                return analysisResult;
            }

            // 许可证已停用
            if (!rawData.LicenseIsActive)
            {
                analysisResult.Success = false;
                analysisResult.Msg = "许可证已被停用";
                return analysisResult;
            }

            // 许可证设备数限制为0
            if (rawData.LicenseMaxDevices == 0)
            {
                analysisResult.Success = false;
                analysisResult.Msg = "许可证设备数量限制为0，无法激活";
                return analysisResult;
            }

            // 许可证过期校验
            if (!string.IsNullOrEmpty(rawData.LicenseExpiresAtStr))
            {
                try
                {
                    DateTime expiresTimeUtc = DateTime.Parse(rawData.LicenseExpiresAtStr).ToUniversalTime();
                    DateTime serverTimeUtc = DateTime.MinValue;
                    if (rawData.ServerTime == DateTime.MinValue)
                    {
                        analysisResult.Success = false;
                        analysisResult.Msg = "无法获取服务器时间（服务端时间为空），无法校验许可证有效期期";
                        return analysisResult;
                    }
                    else
                    {
                        serverTimeUtc = rawData.ServerTime.ToUniversalTime();
                    }
                    if (expiresTimeUtc < serverTimeUtc)
                    {
                        analysisResult.Success = false;
                        analysisResult.Msg = $"许可证已过期（服务器时间：{serverTimeUtc:yyyy-MM-dd HH:mm:ss}，有效期至：{expiresTimeUtc:yyyy-MM-dd HH:mm:ss}）";
                        return analysisResult;
                    }
                }
                catch (Exception ex)
                {
                    analysisResult.Success = false;
                    analysisResult.Msg = $"许可证有效期格式异常：{ex.Message}";
                    return analysisResult;
                }
            }

            // 全局设备查询失败
            if (!rawData.GlobalDeviceQuerySuccess)
            {
                analysisResult.Success = false;
                analysisResult.Msg = rawData.GlobalDeviceQueryMsg;
                return analysisResult;
            }

            // 筛选本机在当前许可证下的设备数
            List<JToken> currentLicenseDevices = rawData.GlobalDeviceList
                .Where(d => d["license"]?.ToString() == rawData.LicenseId)
                .ToList();
            int currentLocalDeviceCount = currentLicenseDevices.Count;
            analysisResult.CurrentTotalDeviceCount = rawData.LicenseTotalDeviceCount;

            // 标记是否需要新增/更新设备
            analysisResult.NeedAddDevice = currentLocalDeviceCount == 0;
            analysisResult.NeedUpdateDeviceName = currentLocalDeviceCount > 0;
            // 修复：如果出现脏数据（一台机器在同一个许可证下绑定了多次），我们只取第一条（最新的一条）作为更新目标
            analysisResult.TargetDevice = analysisResult.NeedUpdateDeviceName ? currentLicenseDevices[0] : null;

            // 脏数据清理：如果当前许可证下，本机的绑定记录大于 1 条，则记录需要被清理的冗余设备 ID
            if (currentLocalDeviceCount > 1)
            {
                analysisResult.RedundantDeviceIds = currentLicenseDevices.Skip(1).Select(d => d["id"]?.ToString()).Where(id => !string.IsNullOrEmpty(id)).ToList();
            }

            // 新增设备时的校验
            if (analysisResult.NeedAddDevice)
            {
                // 校验1：许可证总设备数超限
                if (rawData.LicenseTotalDeviceCount >= rawData.LicenseMaxDevices)
                {
                    analysisResult.Success = false;
                    analysisResult.Msg = $"当前许可证已绑定{rawData.LicenseTotalDeviceCount}台设备，达到上限{rawData.LicenseMaxDevices}台";
                    return analysisResult;
                }

                // 校验2：本机绑定许可证数超限
                if (rawData.GlobalDeviceList.Count >= maxLicensePerDevice)
                {
                    analysisResult.Success = false;
                    analysisResult.Msg = $"本机已绑定{rawData.GlobalDeviceList.Count}个许可证（上限{maxLicensePerDevice}个），禁止重复绑定\n已绑定许可证key：{rawData.BoundOtherLicenseKey}";
                    return analysisResult;
                }
            }

            // 拼接关联设备名称（标注本机/其他）
            List<string> relatedDeviceNames = new List<string>();

            // 按激活时间降序排序（最新激活的设备在前），处理activate_time为空/解析失败的情况
            var sortedDeviceList = rawData.LicenseDeviceList.OrderByDescending(device =>        // OrderByDescending为降序；如果要最早在前（升序），改为OrderBy即可
            {
                // 尝试解析激活时间（兼容UTC格式：yyyy-MM-ddTHH:mm:ss.fffZ）
                if (DateTime.TryParse(device["updated"]?.ToString(), out DateTime activateTime))  // updated为最新更新验证时间；可自定义排序字段,可改为激活时间：activate_time
                {
                    return activateTime;
                }
                // 空值/解析失败的设备排到最后（用最小时间值）
                return DateTime.MinValue;
            }).ToList();

            // 新增设备时，将本机设备插入到排序列表的最前面（最新激活）
            if (analysisResult.NeedAddDevice)
            {
                // 构造本机新增设备的JToken（包含遍历所需的核心字段）
                JObject newLocalDevice = new JObject
                {
                    ["computer_name"] = rawData.ComputerName, // 本机名称
                    ["machine_id"] = rawData.MachineId,       // 本机机器码
                    ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") // 最新更新时间（确保排第一）
                };
                // 插入到列表头部（优先级最高）
                sortedDeviceList.Insert(0, newLocalDevice);
            }

            // 遍历排序后的列表
            foreach (JToken device in sortedDeviceList) // 改为遍历排序后的列表
            {
                string deviceName = device["computer_name"]?.ToString() ?? "未知设备";
                string deviceMachineId = device["machine_id"]?.ToString() ?? "";
                string deviceLabel = deviceMachineId == rawData.MachineId
                    ? $"{deviceName}(本机)"
                    : $"{deviceName}(其他设备)";
                relatedDeviceNames.Add(deviceLabel);
            }

            analysisResult.RelatedDeviceNamesStr = relatedDeviceNames.Count > 0
                ? string.Join("、", relatedDeviceNames)
                : "无";

            // 处理有效期显示文本
            analysisResult.ExpiresAtDisplay = string.IsNullOrEmpty(rawData.LicenseExpiresAtStr)
                ? "永久"
                : rawData.LicenseExpiresAtStr;

            // 多许可证绑定提示
            bool alreadyActivatedInOtherLicense = rawData.GlobalDeviceList.Count > currentLocalDeviceCount;
            analysisResult.MultiLicenseTip = alreadyActivatedInOtherLicense
                ? $"\n⚠ 本机同时绑定了{rawData.GlobalDeviceList.Count}个许可证（超出建议上限{maxLicensePerDevice}个）"
                : "";

            // 过期提醒
            if (!string.IsNullOrEmpty(rawData.LicenseExpiresAtStr))
            {
                try
                {
                    DateTime expiresTimeUtc = DateTime.Parse(rawData.LicenseExpiresAtStr).ToUniversalTime();
                    DateTime serverTimeUtc = rawData.ServerTime.ToUniversalTime();
                    TimeSpan remainingTime = expiresTimeUtc - serverTimeUtc;
                    if (remainingTime.TotalDays > 0 && remainingTime.TotalDays <= limitdate)
                    {
                        analysisResult.ExpireWarnTip = $"\n⚠️ 警告：许可证即将过期！剩余{Math.Ceiling(remainingTime.TotalDays)}天，请联系管理员延期";
                    }
                }
                catch (Exception ex)
                {
                    analysisResult.Success = false;
                    analysisResult.Msg = $"解析有效期失败：{ex.Message}";
                    return analysisResult;
                }
            }

            // 自动找回提醒
            if (rawData.IsAutoRecovered)
            {
                if (string.IsNullOrEmpty(rawData.InputKey))
                {
                    analysisResult.AutoRecoverTip = $"\n💡 提示：未提供许可证密钥，检测到本机已绑定许可证，已自动为您找回并验证。";
                }
                else
                {
                    analysisResult.AutoRecoverTip = $"\n💡 提示：本设备已有绑定许可证，已自动从 {rawData.InputKey} 切换为您已绑定的更优许可证进行验证。";
                }
            }

            // 分析通过
            analysisResult.Success = true;
            analysisResult.Msg = "验证通过";
            return analysisResult;
        }


        /// <summary>
        /// 第三步：数据写入（新增/更新设备、更新许可证）+ 结果返回
        /// </summary>
        private (bool Success, string Msg) ProcessDataAndReturn(LicenseVerificationRawData rawData, LicenseAnalysisResult analysisResult, int maxLicensePerDevice)
        {
            // 分析失败直接返回
            if (!analysisResult.Success)
            {
                return (false, analysisResult.Msg);
            }

            try
            {
                using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    string devicesUrl = $"{RemoteApiUrl}/api/collections/devices/records";

                    // 1. 新增设备逻辑
                    if (analysisResult.NeedAddDevice)
                    {
                        JObject newDeviceBody = new JObject
                        {
                            ["machine_id"] = rawData.MachineId,
                            ["computer_name"] = rawData.ComputerName,
                            ["license"] = rawData.LicenseId,
                            ["activate_time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                        };

                        StringContent deviceContent = new StringContent(
                            newDeviceBody.ToString(),
                            Encoding.UTF8,
                            "application/json"
                        );

                        HttpResponseMessage newDeviceResp = client.PostAsync(devicesUrl, deviceContent).Result;
                        if (!newDeviceResp.IsSuccessStatusCode)
                        {
                            string errorContent = newDeviceResp.Content.ReadAsStringAsync().Result;
                            return (false, $"新增设备失败：{newDeviceResp.StatusCode} | 详情：{errorContent}");
                        }

                        // 更新许可证总设备数
                        analysisResult.CurrentTotalDeviceCount++;

                        // 更新许可证的devices字段
                        JToken? devicesToken = rawData.License["devices"];
                        List<string> updatedDeviceIds = new List<string>();
                        if (devicesToken != null)
                        {
                            if (devicesToken.Type == JTokenType.Array)
                            {
                                updatedDeviceIds = ((JArray)devicesToken)
                                    .Select(d => d.ToString().Trim())
                                    .Where(id => !string.IsNullOrEmpty(id))
                                    .Distinct()
                                    .ToList();
                            }
                            else if (devicesToken.Type == JTokenType.String)
                            {
                                updatedDeviceIds = devicesToken.ToString()
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(id => id.Trim())
                                    .Distinct()
                                    .ToList();
                            }
                        }

                        // 新增设备ID到许可证关联列表
                        JObject newDevice = JObject.Parse(newDeviceResp.Content.ReadAsStringAsync().Result);
                        string? newDeviceId = newDevice["id"]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(newDeviceId) && !updatedDeviceIds.Contains(newDeviceId))
                        {
                            updatedDeviceIds.Add(newDeviceId);
                        }

                        // 构建许可证更新请求体
                        JObject licenseUpdateBody = new JObject();
                        if (rawData.License["devices"]?.Type == JTokenType.Array || true)
                        {
                            licenseUpdateBody["devices"] = new JArray(updatedDeviceIds);
                        }
                        else
                        {
                            licenseUpdateBody["devices"] = string.Join(",", updatedDeviceIds);
                        }

                        StringContent licenseUpdateContent = new StringContent(
                            licenseUpdateBody.ToString(),
                            Encoding.UTF8,
                            "application/json"
                        );

                        HttpResponseMessage licenseUpdateResp = client.PatchAsync(
                            $"{RemoteApiUrl}/api/collections/licenses/records/{rawData.LicenseId}",
                            licenseUpdateContent
                        ).Result;
                        if (!licenseUpdateResp.IsSuccessStatusCode)
                        {
                            string updateError = licenseUpdateResp.Content.ReadAsStringAsync().Result;
                            return (false, $"更新许可证设备绑定失败：{licenseUpdateResp.StatusCode} | 详情：{updateError}");
                        }
                    }
                    // 2. 更新设备名称逻辑
                    else if (analysisResult.NeedUpdateDeviceName)
                    {
                        if (analysisResult.TargetDevice == null || string.IsNullOrEmpty(analysisResult.TargetDevice["id"]?.ToString()))
                        {
                            return (false, "本机已激活，但未找到当前许可证关联的设备ID");
                        }

                        JObject updateBody = new JObject { ["computer_name"] = rawData.ComputerName };
                        StringContent updateContent = new StringContent(
                            updateBody.ToString(),
                            Encoding.UTF8,
                            "application/json"
                        );

                        HttpResponseMessage updateResp = client.PatchAsync(
                            $"{devicesUrl}/{analysisResult.TargetDevice["id"]}",
                            updateContent
                        ).Result;
                        if (!updateResp.IsSuccessStatusCode)
                        {
                            return (false, $"更新设备名称失败：{updateResp.StatusCode}");
                        }
                    }

                    // 2.5 脏数据清理逻辑：删除冗余的设备记录
                    if (analysisResult.RedundantDeviceIds != null && analysisResult.RedundantDeviceIds.Count > 0)
                    {
                        foreach (string redundantId in analysisResult.RedundantDeviceIds)
                        {
                            HttpResponseMessage deleteResp = client.DeleteAsync($"{devicesUrl}/{redundantId}").Result;
                            if (deleteResp.IsSuccessStatusCode)
                            {
                                // 清理成功后，记得把许可证当前绑定的总设备数减去 1
                                analysisResult.CurrentTotalDeviceCount--;
                            }
                            // 如果删除失败，这里选择静默忽略，不影响主流程的验证通过
                        }
                    }

                    // 3. 生成本地授权文件
                    GenerateLicenseFile(
                        rawData.LicenseKey,
                        rawData.MachineId,
                        analysisResult.ExpiresAtDisplay,
                        rawData.LicenseMaxDevices,
                        analysisResult.CurrentTotalDeviceCount,
                        analysisResult.RelatedDeviceNamesStr
                    );

                    // 4. 组装最终成功消息
                    string successMsg = $"验证通过！\n" +
                        $"许可证key：{rawData.LicenseKey}\n" +
                        $"机器码：{rawData.MachineId}\n" +
                        $"用户：{rawData.ComputerName}\n" +
                        $"有效期：{analysisResult.ExpiresAtDisplay}\n" +
                        $"当前许可证绑定设备：{analysisResult.CurrentTotalDeviceCount}/{rawData.LicenseMaxDevices}\n" +
                        $"关联设备列表：{analysisResult.RelatedDeviceNamesStr}\n" +
                        $"{analysisResult.MultiLicenseTip}{analysisResult.ExpireWarnTip}{analysisResult.AutoRecoverTip}";

                    return (true, successMsg);
                }
            }
            catch (Exception ex)
            {
                return (false, $"数据处理失败：{ex.Message}\n堆栈：{ex.StackTrace}");
            }
        }



        /// <summary>
        /// 远程验证许可证（核心底层函数，不启动本地服务）
        /// </summary>
        /// <param name="key">许可证密钥</param>
        /// <param name="machineId">本机机器码（外部传入，避免重复计算）</param>
        /// <returns>验证结果</returns>
        internal (bool Success, string Msg) RemoteVerifyLicense(string key, string machineId)
        {

            // 前置基础校验（非业务逻辑，提前拦截）
            if (string.IsNullOrEmpty(machineId))
            {
                return (false, "机器码为空，无法验证");
            }
            try
            {
                // ========== 第一步：一次性拉取所有数据（仅请求） ==========
                LicenseVerificationRawData rawData = PullAllRequiredData(key, machineId);

                // ========== 第二步：纯内存逻辑分析（仅判断） ==========
                LicenseAnalysisResult analysisResult = AnalyzeLicenseData(rawData, maxLicensePerDevice);

                // ========== 第三步：数据写入 + 结果返回 ==========
                return ProcessDataAndReturn(rawData, analysisResult, maxLicensePerDevice);
            }
            catch (Exception ex)
            {
                return (false, $"远程验证失败：{ex.Message}\n堆栈：{ex.StackTrace}");
            }
        }


        internal (bool Success, string Msg) VerifyLicense(string key)
        {
            // 1. 获取本机机器码
            var machineResult = GetMachineId();
            if (!machineResult.Success)
            {
                return (false, machineResult.Msg);
            }
            string? machineId = machineResult.MachineId;
            if (string.IsNullOrEmpty(machineId))
            {
                return (false, "机器码为空，无法验证");
            }

            // 2. 启动本地验证服务（原有逻辑保留）
            var startResult = StartServer();
            if (!startResult.Success)
            {
                return (false, $"验证服务启动失败：{startResult.Msg}");
            }

            // 3. 调用远程验证函数（复用核心逻辑）
            var remoteResult = RemoteVerifyLicense(key, machineId);

            // （可选）如果验证失败，停止本地服务（根据业务需求决定）
            // if (!remoteResult.Success) StopServer();

            return remoteResult;
        }


        /// <summary>
        /// 远程验证许可证（核心底层函数，不启动本地服务）
        /// </summary>
        /// <param name="key">许可证密钥</param>
        /// <param name="machineId">本机机器码（外部传入，避免重复计算）</param>
        /// <returns>验证结果</returns>
        internal (bool Success, string Msg) real_RemoteVerifyLicense(string real_RemoteApiUrl, string key, string machineId)
        {
            // 远程验证：直接调用API
            try
            {
                // using (HttpClient client = SecureHttpClientFactory.CreateClientWithPublicKeyPinning(10))     // 使用自定义工厂创建安全的HttpClient,会启用自动验证公钥是否匹配的功能
                using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    // 构建请求体数据（完全不变）
                    var postData = new Dictionary<string, string>
                    {
                        ["key"] = key,
                        ["machineId"] = machineId
                    };

                    // 序列化JSON（完全不变）
                    string jsonContent = Newtonsoft.Json.JsonConvert.SerializeObject(postData);
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    // 构建URL（完全不变）
                    string remoteVerifyUrl = $"{real_RemoteApiUrl.TrimEnd('/')}/api/license/verify";

                    // 修复：同步调用异步，避免死锁
                    HttpResponseMessage response = client.PostAsync(remoteVerifyUrl, content).GetAwaiter().GetResult();

                    if (response.IsSuccessStatusCode)
                    {
                        // 修复：同步调用异步，避免死锁
                        string responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        dynamic? responseData = Newtonsoft.Json.JsonConvert.DeserializeObject(responseContent);

                        if (responseData != null)
                        {
                            // ==============================================
                            // 【加密版核心改动】
                            // 1. 获取加密的 data 字段
                            // ==============================================
                            string encryptedData = responseData.data?.ToString();
                            if (string.IsNullOrEmpty(encryptedData))
                            {
                                return (false, "服务器返回无效数据");
                            }

                            // ==============================================
                            // 2. 解密得到原始的 { success, msg }
                            // ==============================================
                            string decryptedJson = AesHelper.Decrypt(encryptedData, machineId);
                            if (decryptedJson == null)
                            {
                                return (false, "授权数据被篡改或非法请求");
                            }

                            // ==============================================
                            // 3. 解析回原始结构（你原来的逻辑完全不变）
                            // ==============================================
                            dynamic? result = Newtonsoft.Json.JsonConvert.DeserializeObject(decryptedJson);
                            if (result == null)
                            {
                                return (false, "解析服务器响应失败");
                            }

                            // ==================== 以下完全是你原来的代码 ====================
                            bool success = result.success ?? false;
                            string msg = result.msg?.ToString() ?? "未返回消息";

                            if (success && TryUnpackLicenseBase64(msg, out var d))
                            {
                                // 生成本地授权文件
                                GenerateLicenseFile(
                                    d.LicenseKey,
                                    d.MachineId,
                                    d.ExpiresAtDisplay,
                                    d.LicenseMaxDevices,
                                    d.CurrentTotalDeviceCount,
                                    d.RelatedDeviceNamesStr);

                                // 客户端自行拼接展示文案，和你原来一模一样
                                string showMsg = $"验证通过！\n" +
                                $"许可证key：{d.LicenseKey}\n" +
                                $"机器码：{d.MachineId}\n" +
                                $"用户：{d.ComputerName}\n" +
                                $"有效期：{d.ExpiresAtDisplay}\n" +
                                $"当前许可证绑定设备：{d.CurrentTotalDeviceCount}/{d.LicenseMaxDevices}\n" +
                                $"关联设备列表：{d.RelatedDeviceNamesStr}\n" +
                                $"{d.MultiLicenseTip}{d.ExpireWarnTip}{d.AutoRecoverTip}";

                                // 需要展示就用 showMsg
                                return (success, showMsg);
                            }
                            else
                            {
                                return (success, msg);
                            }
                        }
                        else
                        {
                            return (false, "解析服务器响应失败");
                        }
                    }
                    else
                    {
                        return (false, $"服务器返回错误: {response.StatusCode}");
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                return (false, $"请求超时: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                return (false, $"网络请求失败: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"验证过程出错: {ex.Message}");
            }

        }

        #endregion

    }
}