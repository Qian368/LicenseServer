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
            public string ExpiresAtDisplay { get; set; } = "永久"; // 有效期显示文本
            public string MultiLicenseTip { get; set; } = ""; // 多许可证提示
            public string ExpireWarnTip { get; set; } = ""; // 过期提醒
            public string RelatedDeviceNamesStr { get; set; } = "无"; // 关联设备名称字符串
            public int CurrentTotalDeviceCount { get; set; } // 许可证当前总设备数
        }


        /// <summary>
        /// 第一步：一次性拉取所有需要的远程数据（仅请求，无业务判断）
        /// </summary>
        private LicenseVerificationRawData PullAllRequiredData(string key, string machineId)
        {
            var rawData = new LicenseVerificationRawData
            {
                MachineId = machineId,
                ComputerName = Environment.MachineName
            };

            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                // 1. 拉取待验证的许可证基础信息
                string licenseFilter = $"?filter=(key='{key}')&expand=devices&perPage=1";
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

                // 2. 拉取该许可证下的所有设备
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

                // 3. 拉取本机绑定的所有设备（全局跨许可证）
                var globalDeviceResult = GetAllDevicesByMachineId(machineId);
                rawData.GlobalDeviceList = globalDeviceResult.Devices;
                rawData.GlobalDeviceQuerySuccess = globalDeviceResult.Success;
                rawData.GlobalDeviceQueryMsg = globalDeviceResult.Msg;

                // 4. 预拉取本机绑定的其他许可证Key（提前拉取，避免第二步重复请求）
                if (rawData.GlobalDeviceList.Count > 0)
                {
                    string firstLicenseId = rawData.GlobalDeviceList
                        .Select(d => d["license"]?.ToString())
                        .FirstOrDefault(k => !string.IsNullOrEmpty(k));
                    if (!string.IsNullOrEmpty(firstLicenseId))
                    {
                        string singleLicenseFilter = $"?filter=(id='{firstLicenseId}')&perPage=1";
                        string singleLicenseUrl = $"{RemoteApiUrl}/api/collections/licenses/records{singleLicenseFilter}";
                        HttpResponseMessage singleLicenseResp = client.GetAsync(singleLicenseUrl).Result;
                        if (singleLicenseResp.IsSuccessStatusCode)
                        {
                            string singleLicenseJson = singleLicenseResp.Content.ReadAsStringAsync().Result;
                            JObject singleLicenseData = JObject.Parse(singleLicenseJson);
                            rawData.BoundOtherLicenseKey = singleLicenseData["items"]?[0]?["key"]?.ToString() ?? "未知";
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

            // 基础校验：许可证是否存在
            if (rawData.LicenseData["totalItems"]?.Value<int>() == 0)
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
                    DateTime serverTimeUtc = rawData.ServerTime.ToUniversalTime();
                    if (expiresTimeUtc < serverTimeUtc)
                    {
                        analysisResult.Success = false;
                        analysisResult.Msg = $"许可证已过期（服务器时间：{rawData.ServerTime:yyyy-MM-dd HH:mm:ss}，有效期至：{rawData.LicenseExpiresAtStr}）";
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
            analysisResult.TargetDevice = analysisResult.NeedUpdateDeviceName ? currentLicenseDevices[0] : null;

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
                    WriteColor($"解析有效期失败：{ex.Message}", ConsoleColor.Yellow);
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
                        $"{analysisResult.MultiLicenseTip}{analysisResult.ExpireWarnTip}";

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
            if (string.IsNullOrEmpty(key))
            {
                return (false, "许可证密钥不能为空");
            }

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



        #endregion

    }
}