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
        private string _ApiUrl = ""; // 远程API根地址（优先级高于_port）     

        // 千万不能写自动补齐远程端口逻辑，因为大多数远程服务不需要端口，而且线上正式环境都是 80/443，补齐了反倒破坏了线上默认逻辑
        private string RemoteApiUrl =>      //如果远程API根地址为空，直接默认使用本地地址
            string.IsNullOrEmpty(_ApiUrl)
            ? $"{_LocalApiUrl}"      // 没远程 → 用本地（自动带端口）
            : _ApiUrl;    // 有远程 → 直接用远程

        // 命令行参数        
        private string _action = "交互模式";
        private bool silentMode = false; // 新增：静默模式（隐藏主窗口）
        private string _verifyType = "local"; // 验证类型：local-本地验证，remote-远程验证
        private string _licenseKey = "";        // 许可证

        private int limitdate = 30;     // 不使用远程验证，只验证本地授权文件的周期
        private int maxLicensePerDevice = 1;      // 一台设备最多能绑定几个许可证

        #endregion
    }
}