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
        #region 界面初始化（修复所有空引用+语法问题）
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
            WriteColor(stopRes.Msg, ConsoleColor.Yellow);
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
                var res = ShowVerifyDialog();
                MessageBox.Show(res.Msg, "验证结果", MessageBoxButtons.OK,
                    res.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            };
        }
        #endregion
    }
}