using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace ModUpdater
{
    /// <summary>
    /// Puts the Check and Update buttons on the Status row of this mod's page in MOD Settings
    /// (OC2ModSettings by CH3NGYZ). MOD Settings lists every plugin's config on its own, so the
    /// AutoUpdate switch and the Status line need nothing extra; only the buttons go through its
    /// public API, which is reached by name so this mod still loads without it.
    /// </summary>
    internal sealed class SettingsBridge : IDisposable
    {
        internal const string Guid = "com.ch3ngyz.plugin.overcooked2.modsettings";

        // The buttons grey out on their own: MOD Settings asks CanExecute while the page is open.
        private readonly Plugin plugin;
        private readonly Updater updater;
        private readonly List<IDisposable> leases = new List<IDisposable>();
        private object host;
        private MethodInfo registerAction;
        private MethodInfo refresh;

        internal SettingsBridge(Plugin plugin, Updater updater)
        {
            this.plugin = plugin;
            this.updater = updater;
        }

        /// <summary>After a reload the reloaded mods have new config objects; have MOD Settings
        /// list those instead of the old ones.</summary>
        internal void Refresh()
        {
            if (host == null || refresh == null) return;
            try { refresh.Invoke(host, null); }
            catch (Exception error) { Plugin.Log.LogWarning("MOD Settings không làm mới được: " + error.Message); }
        }

        internal void Connect()
        {
            try
            {
                PluginInfo info;
                if (!Chainloader.PluginInfos.TryGetValue(Guid, out info) || info.Instance == null) return;
                object instance = info.Instance;
                Type type = instance.GetType();
                MethodInfo apiVersion = type.GetMethod("GetApiVersionV1", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo status = type.GetMethod("GetStatusV1", BindingFlags.Instance | BindingFlags.Public);
                registerAction = type.GetMethod("RegisterActionV1", BindingFlags.Instance | BindingFlags.Public);
                refresh = type.GetMethod("RefreshV1", BindingFlags.Instance | BindingFlags.Public);
                if (apiVersion == null || status == null || registerAction == null) return;
                if ((int)apiVersion.Invoke(instance, null) != 1 || (string)status.Invoke(instance, null) != "ready") return;
                host = instance;

                Register("check", "Kiểm tra", "Hỏi GitHub xem có bản mới không, và tải về nếu có.", 2,
                    delegate { updater.Start(false); },
                    delegate { return updater.ScriptFound && !updater.Busy; }, false);
                Register("update", "Cập nhật",
                    "Nạp bản vừa tải vào game ngay, không cần tắt game. Chỉ dùng được ở menu chính, khi không ở trong phòng trực tuyến.", 1,
                    plugin.LoadNow,
                    delegate { return plugin.CanLoadNow(updater.Read()) && Reloader.SafeNow(); }, false);
            }
            catch (Exception error)
            {
                host = null;
                Plugin.Log.LogWarning("Không thêm được nút vào MOD Settings: " + error.Message);
            }
        }

        private void Register(string id, string name, string description, int order, Action execute,
            Func<bool> canExecute, bool closePage)
        {
            var descriptor = new Dictionary<string, object>();
            descriptor["Version"] = 1;
            descriptor["DisplayName"] = name;
            descriptor["Description"] = description;
            descriptor["Order"] = order;
            descriptor["Entry"] = Plugin.Status;
            descriptor["ClosePage"] = closePage;
            descriptor["Execute"] = execute;
            descriptor["CanExecute"] = canExecute;
            var lease = registerAction.Invoke(host, new object[] { Plugin.Id, id, descriptor }) as IDisposable;
            if (lease != null) leases.Add(lease);
        }

        public void Dispose()
        {
            foreach (IDisposable lease in leases)
            {
                try { lease.Dispose(); } catch { }
            }
            leases.Clear();
            host = null;
        }
    }
}
