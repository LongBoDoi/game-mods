// MK11 Tweaks - an in-game panel of offline cheats for Mortal Kombat 11.
// Loaded by Ultimate ASI Loader (dinput8.dll) from Binaries\Retail.
#include "common.h"
#include <cstdarg>
#include <cstdio>
#include <cstring>

HMODULE g_module;
static FILE* s_log;

void Log(const char* fmt, ...)
{
    if (!s_log) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(s_log, "[%02d:%02d:%02d] ", t.wHour, t.wMinute, t.wSecond);
    va_list ap;
    va_start(ap, fmt);
    vfprintf(s_log, fmt, ap);
    va_end(ap);
    fputc('\n', s_log);
    fflush(s_log);
}

static DWORD WINAPI OverlayThread(LPVOID)
{
    OverlayInit();
    return 0;
}

static bool IsMK11()
{
    char exe[MAX_PATH];
    GetModuleFileNameA(nullptr, exe, MAX_PATH);
    const char* name = strrchr(exe, '\\');
    name = name ? name + 1 : exe;
    return _stricmp(name, "MK11.exe") == 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(module);
    g_module = module;

    char path[MAX_PATH];
    GetModuleFileNameA(module, path, MAX_PATH);
    if (char* dot = strrchr(path, '.')) strcpy(dot, ".log");
    s_log = fopen(path, "w");
    Log("MK11 Tweaks (built " __DATE__ ")");

    if (!IsMK11())
    {
        // MK11_DX12.exe uses a D3D12 swap chain this overlay does not draw on
        Log("Not MK11.exe - launch the DirectX 11 version of the game. Doing nothing.");
        return TRUE;
    }

    ConfigLoad();
    // Patch the tick call now, before the game's main loop starts running it.
    GameInit();
    CreateThread(nullptr, 0, OverlayThread, nullptr, 0, nullptr);
    return TRUE;
}
