#include "common.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>

Settings g_settings;

static char s_iniPath[MAX_PATH];

static const struct { UINT vk; const char* name; } kKeys[] = {
    { VK_F1, "F1" }, { VK_F2, "F2" }, { VK_F3, "F3" }, { VK_F4, "F4" }, { VK_F5, "F5" }, { VK_F6, "F6" },
    { VK_F7, "F7" }, { VK_F8, "F8" }, { VK_F9, "F9" }, { VK_F10, "F10" }, { VK_F11, "F11" }, { VK_F12, "F12" },
    { VK_INSERT, "Insert" }, { VK_DELETE, "Delete" }, { VK_HOME, "Home" }, { VK_END, "End" },
    { VK_PRIOR, "PageUp" }, { VK_NEXT, "PageDown" }, { VK_OEM_3, "Backquote" }, { VK_PAUSE, "Pause" },
};

const char* KeyName(UINT vk)
{
    for (auto& k : kKeys)
        if (k.vk == vk) return k.name;
    static char buf[8];
    snprintf(buf, sizeof(buf), "0x%02X", vk);
    return buf;
}

static UINT ParseKey(const char* s, UINT fallback)
{
    for (auto& k : kKeys)
        if (_stricmp(k.name, s) == 0) return k.vk;
    char* end = nullptr;
    unsigned long v = strtoul(s, &end, 0);             // also accept a raw virtual-key code, e.g. 0x70
    return (end != s && v > 0 && v < 256) ? (UINT)v : fallback;
}

static bool ReadBool(const char* sec, const char* key, bool def)
{
    return GetPrivateProfileIntA(sec, key, def ? 1 : 0, s_iniPath) != 0;
}

static float ReadFloat(const char* sec, const char* key, float def)
{
    char buf[32];
    GetPrivateProfileStringA(sec, key, "", buf, sizeof(buf), s_iniPath);
    return buf[0] ? (float)atof(buf) : def;
}

static void WriteStr(const char* sec, const char* key, const char* val)
{
    WritePrivateProfileStringA(sec, key, val, s_iniPath);
}

static void WriteBool(const char* sec, const char* key, bool v) { WriteStr(sec, key, v ? "1" : "0"); }

static void WriteFloat(const char* sec, const char* key, float v)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "%.2f", v);
    WriteStr(sec, key, buf);
}

void ConfigLoad()
{
    // MK11Tweaks.ini next to MK11Tweaks.asi
    GetModuleFileNameA(g_module, s_iniPath, MAX_PATH);
    if (char* dot = strrchr(s_iniPath, '.')) strcpy(dot, ".ini");

    Settings& s = g_settings;
    char buf[32];
    GetPrivateProfileStringA("General", "ToggleKey", "F1", buf, sizeof(buf), s_iniPath);
    s.toggleKey = ParseKey(buf, VK_F1);
    s.enabled = ReadBool("General", "Enabled", true);
    s.playerSide = GetPrivateProfileIntA("General", "PlayerSide", 1, s_iniPath) == 2 ? 1 : 0;
    s.panelX = ReadFloat("General", "PanelX", -1.0f);
    s.panelY = ReadFloat("General", "PanelY", -1.0f);

    s.unlimitedHealth = ReadBool("Cheats", "UnlimitedHealth", false);
    s.unlimitedAttackMeter = ReadBool("Cheats", "UnlimitedAttackMeter", false);
    s.unlimitedDefenseMeter = ReadBool("Cheats", "UnlimitedDefenseMeter", false);
    s.weakOpponents = ReadBool("Cheats", "WeakOpponents", false);
    float dmg = ReadFloat("Cheats", "WeakOpponentsDamage", 3.0f);
    s.weakOpponentsDamage = dmg < 1.0f ? 1.0f : (dmg > 50.0f ? 50.0f : dmg);
    s.unlimitedFatalBlows = ReadBool("Cheats", "UnlimitedFatalBlows", false);
    s.fatalBlowAnyHealth = ReadBool("Cheats", "FatalBlowAnyHealth", true);
    s.unlimitedKrushingBlows = ReadBool("Cheats", "UnlimitedKrushingBlows", false);
    s.easyKrushingBlows = ReadBool("Cheats", "EasyKrushingBlows", false);

    ConfigSave();   // writes any missing keys so the file documents every option
}

void ConfigSave()
{
    const Settings& s = g_settings;
    WriteStr("General", "ToggleKey", KeyName(s.toggleKey));
    WriteBool("General", "Enabled", s.enabled);
    WriteStr("General", "PlayerSide", s.playerSide == 1 ? "2" : "1");
    WriteFloat("General", "PanelX", s.panelX);
    WriteFloat("General", "PanelY", s.panelY);

    WriteBool("Cheats", "UnlimitedHealth", s.unlimitedHealth);
    WriteBool("Cheats", "UnlimitedAttackMeter", s.unlimitedAttackMeter);
    WriteBool("Cheats", "UnlimitedDefenseMeter", s.unlimitedDefenseMeter);
    WriteBool("Cheats", "WeakOpponents", s.weakOpponents);
    WriteFloat("Cheats", "WeakOpponentsDamage", s.weakOpponentsDamage);
    WriteBool("Cheats", "UnlimitedFatalBlows", s.unlimitedFatalBlows);
    WriteBool("Cheats", "FatalBlowAnyHealth", s.fatalBlowAnyHealth);
    WriteBool("Cheats", "UnlimitedKrushingBlows", s.unlimitedKrushingBlows);
    WriteBool("Cheats", "EasyKrushingBlows", s.easyKrushingBlows);
}
