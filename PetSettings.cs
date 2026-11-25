// ============================================================================
// PetSettings.cs - 用户配置持久化与 Windows 开机自启管理
// 核心原理：
//   1. PetSettingsStore 使用 XmlSerializer 读写 %LOCALAPPDATA%\GptDragonPet\settings.xml，
//      写入时先落盘 .tmp 再原子替换（File.Replace），避免异常退出损坏配置；
//   2. AutoStartManager 通过当前用户注册表 HKCU\...\Run 写入自启项，无需管理员权限。
// ============================================================================
using System;
using System.IO;
using System.Reflection;
using System.Security;
using System.Xml.Serialization;
using Microsoft.Win32;

public sealed class PetSettings
{
    public int Size = 174;
    public bool AlwaysOnTop = true;
    public bool Animate = true;
    public bool FollowMouse = true;
    public bool SnapEdges = true;
    public bool CompactDock = true;
    public bool IdleActions = true;
    public bool CodexNotices = true;
    public string Dock = "None";
    public bool HasPosition;
    public double Left;
    public double Top;
    public bool AutoStart;
    public bool TimeGreetings = true;
    public bool HealthReminder;
    public int HealthReminderMinutes = 45;
    public bool ShowCodexFeatures;
}

internal sealed class PetSettingsStore
{
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(PetSettings));
    private readonly string path;

    internal PetSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GptDragonPet", "settings.xml"))
    {
    }

    internal PetSettingsStore(string path) { this.path = path; }

    internal PetSettings Load()
    {
        try
        {
            using (var file = File.OpenRead(path))
            {
                var loaded = (PetSettings)Serializer.Deserialize(file);
                if (loaded.HealthReminderMinutes != 30 && loaded.HealthReminderMinutes != 45 && loaded.HealthReminderMinutes != 60)
                    loaded.HealthReminderMinutes = 45;
                return loaded;
            }
        }
        catch (IOException) { return new PetSettings(); }
        catch (UnauthorizedAccessException) { return new PetSettings(); }
        catch (InvalidOperationException) { return new PetSettings(); }
    }

    internal void Save(PetSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // 先写临时文件再原子替换，防止中途断电或强制结束进程导致配置文件写残。
            var temporary = path + ".tmp";
            using (var file = File.Create(temporary)) Serializer.Serialize(file, settings);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class AutoStartManager
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GptDragonPet";

    internal static bool IsEnabled()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunSubKey, false))
            {
                if (key == null) return false;
                var val = key.GetValue(ValueName) as string;
                return !String.IsNullOrWhiteSpace(val);
            }
        }
        catch (SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    internal static bool SetEnabled(bool enabled, string exePath = null)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunSubKey, true) ?? Registry.CurrentUser.CreateSubKey(RunSubKey))
            {
                if (key == null) return false;
                if (enabled)
                {
                    string target = !String.IsNullOrWhiteSpace(exePath) ? exePath : Assembly.GetExecutingAssembly().Location;
                    if (String.IsNullOrWhiteSpace(target)) return false;
                    key.SetValue(ValueName, "\"" + target + "\"");
                }
                else
                {
                    if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
                return true;
            }
        }
        catch (SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }
}
