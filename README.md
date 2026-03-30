# 许可证验证工具（LicenseServer）使用说明文档
## 1、安装依赖
- 安装 Newtonsoft.Json
Install-Package Newtonsoft.Json
- 安装 System.Management
Install-Package System.Management

### 2、如果已安装dotnet，也可以使用dotnet add package安装依赖。
- 安装 Newtonsoft.Json
dotnet add package Newtonsoft.Json
- 安装 System.Management
dotnet add package System.Management

### 3、打包项目
- 打开项目文件夹，使用以下命令打包项目：
```
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```
- `.NET` 发布命令的详细参数解释

| 参数 | 作用 | 效果与解释 |
| :--- | :--- | :--- |
| **`-c Release`** | 指定构建配置。 | 使用**发布模式**进行编译和优化，生成更小、更快、适合生产环境的程序。 |
| **`-r win-x64`** | 指定目标运行时。 | 为**64位Windows操作系统**生成原生可执行文件，应用程序将在此特定平台上运行。 |
| **`--self-contained true`** | 启用自包含部署。 | 将**.NET运行时**与应用程序一起打包。最终程序可以在**没有安装.NET的机器**上独立运行，但体积较大。 |
| **`/p:PublishSingleFile=true`** | 启用单文件发布。 | 将所有依赖的DLL、资源等**打包成一个单独的.exe文件**，使分发和部署非常简洁。 |
| **`/p:IncludeNativeLibrariesForSelfExtract=true`** | 包含本机库用于自解压。 | 确保应用程序所需的**本机依赖库**也被打包进单文件中，避免运行时因缺少本机组件而失败。 |
- 打包完成后，在 `bin/Release/net6.0/win-x64/publish` 目录下即可找到 `LicenseServer.exe`（自包含单文件）。
**最终生成物**：一个可在任何 64 位 Windows 系统上直接运行的、独立的 `.exe` 文件。


# LicenseServer 许可证验证工具

## 项目简介
LicenseServer 是一个基于 .NET Windows Forms 开发的桌面/控制台应用程序，核心功能是实现硬件绑定的许可证验证与管理。
它支持两种验证模式：启动内置的 PocketBase 服务进行本地验证，或连接到指定的远程 API 服务进行远程验证。
旨在帮你完成复杂的验证逻辑，适配任何本地应用。程序可通过命令行参数无界面运行，同时也提供了图形化界面（GUI）供手动操作，并对外提供了可供其他应用程序调用的验证 API。

## 核心功能

-   **硬件绑定**：基于本机 CPU 序列号、主板序列号生成唯一机器码 (`machineId`)。
-   **双模式验证**：
    -   **本地验证**：在本地启动 PocketBase 服务，完成整个验证流程。
    -   **远程验证**：直接连接至用户指定的远程许可证服务器进行验证。
-   **许可证管理**：验证许可证状态、有效期，并管理许可证与设备（机器码）的绑定关系，确保设备绑定数量不超过上限。
-   **本地授权缓存**：验证成功后生成加密的本地授权文件，后续启动可优先校验本地文件，避免重复远程验证，提升体验。
-   **命令行驱动**：支持通过完整的命令行参数控制所有功能，无需启动 GUI，便于集成和自动化。
-   **对外 API**：提供 `RemoteVerifyApi` 方法，供其他 .NET 应用程序直接调用以验证许可证。

## 快速开始

### 命令行调用示例

程序的所有功能均可通过命令行参数调用。以下是常用指令示例：

| 指令示例 | 功能说明 |
| :--- | :--- |
| `.\LicenseServer.exe` | 以**交互模式**启动，显示图形化管理界面。 |
| `.\LicenseServer.exe -Action machineid` | 获取本机**机器码**并输出到控制台。 |
| `.\LicenseServer.exe -Action start` | **启动**本地验证服务。 |
| `.\LicenseServer.exe -Action stop` | **停止**本地验证服务。 |
| `.\LicenseServer.exe -Action status` | 检查服务状态。**服务运行中则进程退出码为0，未运行则为1**。 |
| `.\LicenseServer.exe -Action verify -licensekey "ABC-123"` | 使用**本地模式**验证许可证密钥，弹出结果窗口。 |
| `.\LicenseServer.exe -Action verify -licensekey "ABC-123" -port 8091` | 在指定端口(`8091`)启动服务并验证。 |
| `.\LicenseServer.exe -Action verify -licensekey "ABC-123" -verifytype remote -apiurl "http://your-server.com:8090"` | 使用**远程模式**验证，连接至指定API地址。 |
| `.\LicenseServer.exe -Action verify -verifytype remote -apiurl "http://your-server.com:8090"` | 远程验证，但不提供密钥，程序将**弹出输入框**让用户输入。 |
| `.\LicenseServer.exe -Action verify -licensekey "ABC-123" -verifytype remote -apiurl "http://your-server.com" -silentmode` | **静默模式**远程验证。成功时输出JSON到控制台并退出码为0；失败时输出错误信息并退出码为1。 |

### 参数详解

| 参数 | 必填 | 说明 |
| :--- | :--- | :--- |
| `-Action` | 否 | 指定操作。可选值：`start`, `stop`, `status`, `machineid`, `verify`, `交互模式`(默认)。 |
| `-VerifyType` | 验证时必填 | 验证类型。`local`(默认): 启动本地服务验证；`remote`: 远程API验证。 |
| `-LicenseKey` | 否 | 要验证的许可证密钥。在`verify`操作中若不提供，则会弹出输入框。 |
| `-Port` | 否 | 设置本地PocketBase服务的HTTP端口，默认为 `8090`。 |
| `-ApiUrl` | 否 | **（优先级最高）** 指定远程验证API的完整根地址 (如：`http://192.168.1.100:8090`)。设置后，`-Port` 参数对该次验证失效。 |
| `-LimitDate` | 否 | 设置许可证有效期剩余多少天时触发提醒，默认为 `30` 天。 |
| `-SilentMode` | 否 | 启用静默模式。程序主窗口将完全隐藏，验证结果输出到控制台，并通过进程退出码 (0成功/1失败) 表明状态。 |

## 验证流程说明

无论是本地还是远程验证，核心逻辑 (`RemoteVerifyLicense` 方法) 均包含以下步骤：
1.  **数据拉取**：查询许可证信息、关联设备、本机全局绑定设备等。
2.  **逻辑分析**：在内存中校验许可证是否存在、是否激活、是否过期、设备数量是否超限、本机是否已绑定等。
3.  **数据写入**：若校验通过，则新增或更新设备绑定记录，并生成本地加密授权文件。

**本地授权文件**：验证成功后生成，使用 AES-256 加密和 HMAC-SHA256 动态签名 + 额外加密规则，防止篡改。下次验证时优先检查此文件，若有效且在强制校验周期内（默认7天），则直接通过，无需连接网络。

## 后端数据库配置

工具需要后端数据库支持，表结构如下(详见pocketbase\pb_data\data.db)：

| 表名 | 字段 | 类型 | 说明 |
| :--- | :--- | :--- | :--- |
| **licenses** | `id` | Text | 主键 |
| | `key` | Text | 许可证密钥 |
| | `is_active` | Bool | 是否激活 |
| | `expires_at` | DateTime | 过期时间 |
| | `max_devices` | Number | 最大绑定设备数 |
| | `devices` | Relation | 关联的设备记录 (多对一) |
| **devices** | `id` | Text | 主键 |
| | `machine_id` | Text | 机器码 |
| | `computer_name` | Text | 计算机名 |
| | `license` | Relation | 所属的许可证 (一对多) |
| | `activate_time` | DateTime | 激活时间 |
| | `updated` | AutoDate | 最后更新时间，用于排序 |

## 集成 API 调用

如果你的应用程序需要验证许可证，可以直接实例化并调用 `RemoteVerifyApi` 方法。

**方法签名**:
```csharp
public string RemoteVerifyApi(string licenseKey, string apiUrl = "")
```

**调用示例 (C#)**:
```csharp
// 实例化工具类
MainForm licenseTool = new MainForm();

// 调用远程验证API
string resultJson = licenseTool.RemoteVerifyApi(
    "ABC-123-EXP",
    "http://your-license-server.com:8090"
);

// 解析返回的JSON结果
dynamic result = JsonConvert.DeserializeObject(resultJson);
if (result.Success == true)
{
    Console.WriteLine("验证成功: " + result.Msg);
    Console.WriteLine("机器码: " + result.MachineId);
}
else
{
    Console.WriteLine("验证失败: " + result.Msg);
}
```

**返回格式 (JSON)**:
```json
{
  "Success": true,
  "Msg": "验证通过！\n许可证key：ABC-123\n机器码：e4d909c2908d\n用户：PC-NAME\n有效期：永久\n当前许可证绑定设备：2/3\n关联设备列表：PC-NAME(本机)、PC-NAME2(其它设备)",
  "MachineId": "e4d909c2908d"
}
```