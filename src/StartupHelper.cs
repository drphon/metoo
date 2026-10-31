using System;
using Microsoft.Win32;

namespace TodoClock
{
    public static class StartupHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TodoClock";

        private static string ExePath => Environment.ProcessPath ?? "";

        public static bool IsEnabled()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return !string.IsNullOrEmpty(k?.GetValue(ValueName) as string);
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (k == null) return;
                if (enabled) k.SetValue(ValueName, "\"" + ExePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
            catch { }
        }

        /// <summary>If enabled but the exe moved, point the Run entry at the current exe.</summary>
        public static void RefreshPath()
        {
            if (IsEnabled()) Set(true);
        }
    }
}
