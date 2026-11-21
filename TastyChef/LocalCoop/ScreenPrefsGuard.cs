using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using UnityEngine;

namespace LocalCoop
{
    // Unity keeps its window settings (monitor, position, size, fullscreen mode) in the registry,
    // shared by every copy of the game, and rewrites them whenever a window changes - so after a
    // co-op session the next normal start would open on a guest's monitor, at a guest's size.
    //
    // When the first guest opens, Player 1 copies those values and starts a hidden watcher that
    // waits until every copy of the game has closed (in whatever order, crashes included) and
    // then writes the copy back.
    internal static class ScreenPrefsGuard
    {
        private static bool s_armed;

        public static void Arm()
        {
            if (s_armed) return;
            s_armed = true;
            try
            {
                string keyPath = $@"Software\{Application.companyName}\{Application.productName}";
                var keep = new List<string>();
                var names = new List<string>();
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    if (key == null) return;
                    foreach (string name in key.GetValueNames())
                    {
                        if (!IsScreenSetting(name)) continue;
                        names.Add(name);
                        keep.Add(SetLine(name, key.GetValue(name), key.GetValueKind(name)));
                    }
                }

                string exe = Process.GetCurrentProcess().MainModule.FileName;
                var ps = new StringBuilder();
                ps.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
                ps.AppendLine($"$exe = '{Quote(exe)}'");
                ps.AppendLine("do { Start-Sleep -Seconds 2 } while (Get-Process -Name 'TastyChef' | Where-Object { $_.Path -eq $exe })");
                ps.AppendLine($"$k = 'HKCU:\\{Quote(keyPath)}'");
                ps.AppendLine("$keep = @(" + string.Join(",", names.Select(n => "'" + Quote(n) + "'")) + ")");
                ps.AppendLine("foreach ($n in (Get-Item -LiteralPath $k).GetValueNames()) {");
                ps.AppendLine("  if (($n -like 'Screenmanager*' -or $n -like 'UnitySelectMonitor*') -and $keep -notcontains $n) { Remove-ItemProperty -LiteralPath $k -Name $n }");
                ps.AppendLine("}");
                foreach (string line in keep) ps.AppendLine(line);

                Directory.CreateDirectory(Coop.Dir);
                string script = Coop.FilePath("restore-window-settings.ps1");
                File.WriteAllText(script, ps.ToString(), new UTF8Encoding(true));
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi)?.Dispose();
                Plugin.Log.LogInfo($"Saved {names.Count} window settings; they come back once every window has closed.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not save the window settings (" + e.Message + "); the next start may open where a co-op window was.");
            }
        }

        private static bool IsScreenSetting(string name) =>
            name.StartsWith("Screenmanager", StringComparison.Ordinal) || name.StartsWith("UnitySelectMonitor", StringComparison.Ordinal);

        private static string SetLine(string name, object value, RegistryValueKind kind)
        {
            string n = Quote(name);
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    return $"New-ItemProperty -LiteralPath $k -Name '{n}' -PropertyType DWord -Value {unchecked((uint)Convert.ToInt32(value)).ToString(CultureInfo.InvariantCulture)} -Force | Out-Null";
                case RegistryValueKind.QWord:
                    return $"New-ItemProperty -LiteralPath $k -Name '{n}' -PropertyType QWord -Value {unchecked((ulong)Convert.ToInt64(value)).ToString(CultureInfo.InvariantCulture)} -Force | Out-Null";
                case RegistryValueKind.Binary:
                    var bytes = (byte[])value;
                    return $"New-ItemProperty -LiteralPath $k -Name '{n}' -PropertyType Binary -Value ([byte[]]@({string.Join(",", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture)))})) -Force | Out-Null";
                default:
                    return $"New-ItemProperty -LiteralPath $k -Name '{n}' -PropertyType String -Value '{Quote(Convert.ToString(value, CultureInfo.InvariantCulture))}' -Force | Out-Null";
            }
        }

        private static string Quote(string s) => (s ?? string.Empty).Replace("'", "''");
    }
}
