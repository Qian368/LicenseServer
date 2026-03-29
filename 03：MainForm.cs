
namespace LicenseServer
{
    public partial class MainForm : Form
    {
        public MainForm()
        {
            InitializeComponent();

            // 初始化路径配置，关于查找本地pocketbase服务启动器
            _scriptDir = AppDomain.CurrentDomain.BaseDirectory; // 程序运行目录
            string projectRoot = Path.GetFullPath(Path.Combine(_scriptDir, "../../../"));   // 项目根目录：往上退3级目录

            string debugPath = Path.Combine(projectRoot, "pocketbase", "pocketbase.exe");   // 拼接调试目录路径
            string publishPath = Path.Combine(_scriptDir, "pocketbase", "pocketbase.exe");   // 拼接发布目录路径

            // 优先找调试目录，找不到再用发布目录
            _pbExePath = File.Exists(debugPath) ? debugPath : publishPath;

            _dataDir = Path.Combine(_scriptDir, "pocketbase", "pb_data");   // 拼接数据目录路径
            _lockFile = Path.Combine(_scriptDir, "pocketbase", "server.pid");   // 拼接锁文件路径
        }

    }
}