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
        #region 界面初始化（仅主界面）
        private void InitializeComponent()
        {
            this.Text = "许可证验证工具";
            this.Size = new Size(423, 280);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            // 标题标签
            Label title = new Label
            {
                Text = "许可证服务管理",
                Font = new Font("微软雅黑", 12F),
                AutoSize = true,
                Location = new Point(120, 20)
            };
            this.Controls.Add(title);

            // 按钮样式统一
            Size btnSize = new Size(170, 35);
            Font btnFont = new Font("微软雅黑", 9F);

            // 1. 启动验证服务
            Button btnStart = new Button
            {
                Text = "启动验证服务",
                Size = btnSize,
                Location = new Point(30, 60),
                Font = btnFont,
                Name = "btnStart"
            };
            this.Controls.Add(btnStart);

            // 2. 停止验证服务
            Button btnStop = new Button
            {
                Text = "停止验证服务",
                Size = btnSize,
                Location = new Point(210, 60),
                Font = btnFont,
                Name = "btnStop"
            };
            this.Controls.Add(btnStop);

            // 3. 查看服务状态
            Button btnStatus = new Button
            {
                Text = "查看服务状态",
                Size = btnSize,
                Location = new Point(30, 105),
                Font = btnFont,
                Name = "btnStatus"
            };
            this.Controls.Add(btnStatus);

            // 4. 获取本机机器码
            Button btnMachineId = new Button
            {
                Text = "获取本机机器码",
                Size = btnSize,
                Location = new Point(210, 105),
                Font = btnFont,
                Name = "btnMachineId"
            };
            this.Controls.Add(btnMachineId);

            // 5. 验证许可证密钥
            Button btnVerify = new Button
            {
                Text = "验证许可证密钥",
                Size = new Size(350, 35),
                Location = new Point(30, 150),
                Font = btnFont,
                Name = "btnVerify"
            };
            this.Controls.Add(btnVerify);

            // 绑定按钮事件
            BindButtonEvents();
            // 新增：绑定窗体关闭事件，退出时停止服务
            this.FormClosing += MainForm_FormClosing;
        }

        // 新增窗体关闭事件处理方法
        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // 停止验证服务
            var stopRes = StopServer();
            Debug.WriteLine(stopRes.Msg);
        }

        private void BindButtonEvents()
        {
            this.Controls["btnStart"].Click += (s, e) =>
            {
                var res = StartServer();
                MessageBox.Show(res.Msg, "提示", MessageBoxButtons.OK,
                    res.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            };

            this.Controls["btnStop"].Click += (s, e) =>
            {
                var res = StopServer();
                MessageBox.Show(res.Msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            this.Controls["btnStatus"].Click += (s, e) =>
            {
                string? pid = GetServerPid();
                if (string.IsNullOrEmpty(pid))
                {
                    MessageBox.Show("服务未运行", "状态", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    Process.GetProcessById(int.Parse(pid));
                    MessageBox.Show($"服务运行中\nPID：{pid}\n端口：{_port}", "状态",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch
                {
                    MessageBox.Show("服务未运行", "状态", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            this.Controls["btnMachineId"].Click += (s, e) =>
            {
                var res = GetMachineId();
                if (res.Success)
                {
                    Clipboard.SetText(res.MachineId!);
                    MessageBox.Show($"机器码：{res.MachineId}\n(已复制到剪贴板)", "机器码",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(res.Msg, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            this.Controls["btnVerify"].Click += (s, e) =>
            {
                while (true)
                {
                    var verifyResult = ShowVerifyDialog();
                    MessageBox.Show(verifyResult.Msg, "验证结果", MessageBoxButtons.OK, verifyResult.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                    Debug.WriteLine(verifyResult.Msg);
                    if (verifyResult.Success || verifyResult.Msg.Contains("已取消验证"))
                    {
                        break;
                    }
                }
            };
        }
        #endregion



        /// <summary>
        /// 不带许可证密钥时的验证流程：实例子类inputForm，不与主窗口共享隐藏属性
        /// 1. 先获取本机机器码
        /// 2. 校验本地授权文件（仅一次）
        /// 3. 校验远程授权文件
        /// 4. 循环验证，直到验证通过或关闭输入框
        /// </summary>
        /// <returns>验证结果</returns>
        public (bool Success, string Msg) ShowVerifyDialog()
        {
            Form inputForm = new Form
            {
                Text = "验证许可证",
                Size = new Size(300, 150),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            Label label = new Label { Text = "请输入许可证密钥：", Location = new Point(10, 20), AutoSize = true };
            TextBox textBox = new TextBox { Location = new Point(10, 50), Width = 260 };
            Button okBtn = new Button { Text = "确定", Location = new Point(65, 85), DialogResult = DialogResult.OK };
            Button cancelBtn = new Button { Text = "取消", Location = new Point(150, 85), DialogResult = DialogResult.Cancel };

            inputForm.Controls.Add(label);
            inputForm.Controls.Add(textBox);
            inputForm.Controls.Add(okBtn);
            inputForm.Controls.Add(cancelBtn);
            inputForm.AcceptButton = okBtn;
            inputForm.CancelButton = cancelBtn;

            // ========== 第一步：获取机器码（唯一一次） ==========
            var machineResult = GetMachineId();
            if (!machineResult.Success || string.IsNullOrEmpty(machineResult.MachineId))
            {
                return (false, machineResult.Msg);
            }

            // ========== 第二步：本地授权文件校验（唯一一次，绝不重复） ==========
            var localVerifyResult = VerifyLocalLicenseFile(machineResult.MachineId);
            if (localVerifyResult.Success)
            {
                return localVerifyResult; // 本地通过，直接返回
            }
            Debug.WriteLine($"本地授权校验失败：{localVerifyResult.Msg}，将进行远程验证");

            // ========== 第三步：尝试读取本地文件中的密钥，自动验证 ==========
            try
            {
                var licenseInfo = ReadLicenseFileInfo(machineResult.MachineId);
                if (!string.IsNullOrEmpty(licenseInfo?.LicenseKey))
                {
                    // 直接调用密钥验证，无任何重复校验
                    return VerifyLicenseByKey(licenseInfo.LicenseKey, machineResult.MachineId);
                }
            }
            catch
            {
                return (false, "远程验证失败");
            }

            // ========== 第四步：弹窗输入密钥，手动验证 ==========
            if (inputForm.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(textBox.Text))
            {
                return VerifyLicenseByKey(textBox.Text, machineResult.MachineId);
            }
            else
            {
                return (false, "已取消验证");
            }
        }

        /// <summary>
        /// 带许可证密钥时的验证流程：一次性验证，无需循环输入密钥
        /// </summary>
        /// <returns>验证结果</returns>
        public (bool Success, string Msg) WithKeyVerify()
        {
            // ========== 第一步：获取机器码（唯一一次） ==========
            var machineResult = GetMachineId();
            if (!machineResult.Success || string.IsNullOrEmpty(machineResult.MachineId))
            {
                return (false, machineResult.Msg);
            }

            // ========== 第二步：本地授权文件校验（唯一一次） ==========
            var localVerifyResult = VerifyLocalLicenseFile(machineResult.MachineId);
            if (localVerifyResult.Success)
            {
                return localVerifyResult;
            }
            Debug.WriteLine($"本地授权校验失败：{localVerifyResult.Msg}，将进行远程验证");

            // ========== 第三步：直接用内置密钥验证，无重复校验 ==========
            return VerifyLicenseByKey(_licenseKey, machineResult.MachineId);
        }

        /// <summary>
        /// 【公共核心方法】仅做密钥验证（远程/本地分支）
        /// ✅ 无本地文件校验，杜绝重复执行
        /// ✅ 两个公开方法共用，消除代码冗余
        /// </summary>
        /// <param name="licenseKey">许可证密钥</param>
        /// <param name="machineId">机器码（已提前获取，唯一一次）</param>
        /// <returns>验证结果</returns>
        private (bool Success, string Msg) VerifyLicenseByKey(string licenseKey, string machineId)
        {
            if (_verifyType == "remote")
            {
                // 远程验证：直接调用API
                return RemoteVerifyLicense(licenseKey, machineId);
            }
            else
            {
                // 本地验证：直接调用本地服务
                return VerifyLicense(licenseKey);
            }
        }

    }
}