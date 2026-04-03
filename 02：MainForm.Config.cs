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

        private int limitdate = 30;     // 验证完成后有效期小于多少天提醒
        private readonly int _forceRemoteCheckDays = 30; // 定期强制远程校验周期（天），避免本地文件永久有效
        private string LocalLicenseFilePath => Path.Combine(_scriptDir, "license.lic"); // 授权文件存储路径（程序目录下隐藏文件）：生产环境下建议配置为系统变量，推荐：`C:\ProgramData\LicenseServer\AppName`
        private int maxLicensePerDevice = 1;      // 一台设备最多能绑定几个许可证



        // ====== 新增：管道模式标识 + 管道流字段 ======
        private bool _isPipeMode = false;       // 是否为管道模式（用于与主窗口通信）
        private StreamReader _pipeReader;       // 管道读取流（用于接收主窗口发送的指令）
        private StreamWriter _pipeWriter;       // 管道写入流（用于向主窗口发送验证结果）

        #endregion
    }
}