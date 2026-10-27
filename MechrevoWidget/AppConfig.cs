using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace MechrevoWidget;

/// <summary>
/// 应用配置:MQTT 身份常量、开机自启(任务计划程序,最高权限,免 UAC)、MainOption.json 读取。
/// </summary>
internal static class AppConfig
{
    // GCUBridge 本地 broker 与客户端身份:使用官方托盘程序的 MyTray 账户,
    // 不会与官方后台 GCUService(身份 MyControlCenter)冲突。
    public const string MqttHost = "127.0.0.1";
    public const int MqttPort = 13688;
    public const string ClientId = "MyTray_19";
    public const string Username = "MyTray_User_19";
    public const string Password = "MyTray_Pwd888881772688_19";

    public const string MainOptionPath =
        @"C:\Program Files\OEM\机械革命控制中心\AiStoneService\MyControlCenter\UserPofiles\MainOption.json";

    // ───────────── 开机自启 ─────────────
    // 程序 manifest 是 requireAdministrator,HKCU\Run 启动会被 UAC 拦(甚至直接被跳过)。
    // 改用任务计划程序:登录触发 + HighestAvailable,登录后静默以管理员启动。

    private const string TaskName = "MechrevoWidget";
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "MechrevoWidget";

    private static (int code, string output) Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, stdout);
        }
        catch (Exception ex)
        {
            MainWindow.Log("schtasks " + args + ": " + ex.Message);
            return (-1, "");
        }
    }

    private static bool? _autoStart;

    /// <summary>缓存的自启状态;尚未查询过时同步查一次(schtasks 约 100ms)。</summary>
    public static bool IsAutoStartEnabled() =>
        _autoStart ??= Schtasks($"/Query /TN \"{TaskName}\"").code == 0;

    /// <summary>开/关自启;返回是否成功。</summary>
    public static bool SetAutoStart(bool enable)
    {
        if (!enable)
        {
            RemoveLegacyRun();
            Schtasks($"/Delete /TN \"{TaskName}\" /F");
            _autoStart = null;
            return !IsAutoStartEnabled();
        }

        var exe = Compat.ProcessPath;
        if (exe is null || exe.Length == 0) return false;

        var xmlPath = Path.Combine(Path.GetTempPath(), "mechrevo_widget_task.xml");
        try
        {
            // schtasks /XML 要求 UTF-16
            File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);
            var (code, _) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            MainWindow.Log($"autostart: create task -> {code}");
            if (code == 0) RemoveLegacyRun();
            _autoStart = code == 0 ? true : null;
            return code == 0;
        }
        catch (Exception ex)
        {
            MainWindow.Log("autostart: " + ex.Message);
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    /// <summary>
    /// 启动时(后台)调用:旧版写在 HKCU\Run 的自启迁移到计划任务;
    /// 已有计划任务时重新注册一次,保证 exe 挪位置后路径仍正确。顺带预热自启状态缓存。
    /// </summary>
    public static void MigrateAutoStart() => Task.Run(() =>
    {
        try
        {
            bool legacy;
            using (var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey))
                legacy = key?.GetValue(LegacyRunValue) is not null;
            if (legacy || IsAutoStartEnabled())
                SetAutoStart(true);
        }
        catch (Exception ex) { MainWindow.Log("autostart migrate: " + ex.Message); }
    });

    private static void RemoveLegacyRun()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
        catch { }
    }

    private static string BuildTaskXml(string exe)
    {
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var cmd = SecurityElement.Escape(exe);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        // 笔记本要点:默认"仅交流电时启动 / 切电池即停止"必须关掉;无执行时间上限
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>机械革命任务栏温度/档位小组件</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{cmd}</Command>
      <WorkingDirectory>{dir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    // ───────────── MainOption.json ─────────────

    /// <summary>读 MainOption.json 拿到某档位当前激活的子 Profile 序号(与 UI 一致);读不到回退 "0"。</summary>
    public static string GetProfileIndex(string modeKey)
    {
        var field = modeKey switch
        {
            "office" => "OfficeProfileIndex",
            "gaming" => "GamingProfileIndex",
            "turbo" => "TurboProfileIndex",
            "custom" => "CustomProfileIndex",
            _ => null,
        };
        if (field is null) return "0";
        try
        {
            var obj = MiniJson.ParseObject(File.ReadAllText(MainOptionPath, Encoding.UTF8));
            if (obj?.GetString(field) is string v && int.TryParse(v, out int idx))
                return idx.ToString();
        }
        catch { }
        return "0";
    }
}
