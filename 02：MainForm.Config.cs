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
using System.Security.AccessControl;
using System.Security.Principal;

namespace LicenseServer
{
    public partial class MainForm : Form
    {
        #region 核心配置

        // 关于查找本地pocketbase服务启动器相关寻址变量
        private readonly string _scriptDir;
        private readonly string _pbExePath;
        private readonly string _dataDir;
        private readonly string _lockFile;


        // 初始化配置：默认端口8090
        private int _port = 8090;
        private string _LocalApiUrl => $"http://localhost:{_port}";      // 仅在启动本地pocketbase服务时使用，其余一律用RemoteApiUrl
        internal string _ApiUrl = ""; // 远程API根地址（优先级高于_port）     

        // 千万不能写自动补齐远程端口逻辑，因为大多数远程服务不需要端口，而且线上正式环境都是 80/443，补齐了反倒破坏了线上默认逻辑
        internal string RemoteApiUrl =>      //如果远程API根地址为空，直接默认使用本地地址
            string.IsNullOrEmpty(_ApiUrl)
            ? $"{_LocalApiUrl}"      // 没远程 → 用本地（自动带端口）
            : _ApiUrl;    // 有远程 → 直接用远程

        // 命令行参数        
        private string _action = "交互模式";
        private bool silentMode = false; // 新增：静默模式（隐藏主窗口）
        internal string _verifyType = "local"; // 验证类型：local-本地验证，remote-远程验证
        internal string _licenseKey = "";        // 许可证

        private int limitdate = 30;     // 验证完成后有效期小于多少天提醒过期
        private readonly int _forceRemoteCheckDays = 30; // 定期强制远程校验周期（天），避免本地文件永久有效
        private int maxLicensePerDevice = 1;      // 一台设备最多能绑定几个许可证



        // ====== 新增：管道模式标识 + 管道流字段 ======
        private bool _isPipeMode = false;       // 是否为管道模式（用于与主窗口通信）
        private StreamReader _pipeReader;       // 管道读取流（用于接收主窗口发送的指令）
        private StreamWriter _pipeWriter;       // 管道写入流（用于向主窗口发送验证结果）


        //private string LocalLicenseFilePath => Path.Combine(_scriptDir, "license.lic"); // 授权文件存储路径（程序目录下隐藏文件）：生产环境下建议配置为系统变量，推荐：`C:\ProgramData\LicenseServer\AppName` 如下：
        // 生产环境配置：系统API获取标准路径，自动适配所有Windows环境
        internal string _appName = "AppName";
        internal string LocalLicenseFilePath
        {
            get
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);     // 获取系统公共目录：C:\ProgramData
                string licenseDir = Path.Combine(programData, "LicenseServer", _appName);    // 拼接应用专属目录（自动创建，权限系统自动分配）

                // 如果目录不存在，则创建并分配权限
                if (!Directory.Exists(licenseDir))  // 检查目录是否存在
                {
                    DirectoryInfo di = Directory.CreateDirectory(licenseDir);  // 创建目录

                    try
                    {
                        // 获取当前目录的安全访问控制列表 (ACL)
                        DirectorySecurity security = di.GetAccessControl();

                        // 构造一个代表所有内置普通用户 (BuiltinUsers) 的安全标识符
                        SecurityIdentifier usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

                        // 赋予修改(Modify)权限，并且该权限向下继承给目录内的所有文件
                        security.AddAccessRule(new FileSystemAccessRule(
                            usersSid,
                            FileSystemRights.Modify,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));

                        // 应用新的权限规则
                        di.SetAccessControl(security);
                    }
                    catch (Exception ex)
                    {
                        // 记录日志或忽略：如果当前进程没有足够的权限去修改ACL（比如非管理员创建非自己所有的目录），会走到这里
                        Debug.WriteLine($"分配目录权限失败: {ex.Message}");
                    }
                }

                return Path.Combine(licenseDir, "license.lic");  // 返回完整授权文件路径
            }
        }

        #endregion
    }
}