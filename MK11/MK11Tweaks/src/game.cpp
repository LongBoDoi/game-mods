// Game side: find the functions we need by byte signature, hook the per-tick
// script dispatch, and apply the cheats from there (on the game thread).
//
// Signatures and calling conventions come from ermaccer's MK11Hook
// (github.com/ermaccer/MK11Hook) and were re-checked against the 2025-11-13
// build: SetLife stores into +0xC20 clamped to [0, +0xC24]; SetMeter clamps to
// the bar's own max; GetObj's second arg is passed as 1 by every game caller.
#include "common.h"
#include <cstring>
#include <mutex>
#include <vector>

std::atomic<int> g_missingPatterns{ 0 };

// ---------------------------------------------------------------------------
// Pattern scanning over the exe's .text section
// ---------------------------------------------------------------------------
static uint8_t* s_textBegin;
static size_t   s_textSize;

static void FindText()
{
    auto base = (uint8_t*)GetModuleHandleW(nullptr);
    auto nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    auto sec = IMAGE_FIRST_SECTION(nt);
    for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, sec++)
        if (memcmp(sec->Name, ".text", 5) == 0)
        {
            s_textBegin = base + sec->VirtualAddress;
            s_textSize = sec->Misc.VirtualSize;
        }
}

// "48 8B ? ? 05" style pattern -> address of first match + offset, or 0
static uintptr_t Scan(const char* name, const char* pattern, int offset = 0)
{
    std::vector<int> bytes;
    for (const char* p = pattern; *p;)
    {
        if (*p == ' ') { p++; continue; }
        if (*p == '?') { bytes.push_back(-1); while (*p == '?') p++; continue; }
        bytes.push_back((int)strtoul(p, (char**)&p, 16));
    }

    const size_t n = bytes.size();
    const int first = bytes[0];
    for (size_t i = 0; i + n <= s_textSize; i++)
    {
        if (s_textBegin[i] != first) continue;
        size_t k = 1;
        while (k < n && (bytes[k] < 0 || s_textBegin[i + k] == bytes[k])) k++;
        if (k == n)
        {
            uintptr_t addr = (uintptr_t)(s_textBegin + i) + offset;
            Log("  %-22s found at exe+0x%llX", name, (unsigned long long)(addr - (uintptr_t)GetModuleHandleW(nullptr)));
            return addr;
        }
    }
    Log("  %-22s NOT FOUND", name);
    g_missingPatterns++;
    return 0;
}

// ---------------------------------------------------------------------------
// Game functions
// ---------------------------------------------------------------------------
enum { PLAYER1 = 0, PLAYER2 = 1 };
enum { BAR_Offensive = 0, BAR_Defensive = 1 };

using MKCharacter = uint8_t;
using PlayerInfo = uint8_t;
using FGGameInfo = uint8_t;

static MKCharacter* (*pGetObj)(int plr, int unk);
static PlayerInfo*  (*pGetInfo)(FGGameInfo* gi, int plr);
static void         (*pSetLife)(MKCharacter* obj, float life);
static void         (*pSetMeter)(PlayerInfo* info, int bar, float value);
static void         (*pSetXRayInfinite)(MKCharacter* obj, int enable, int unk);
static void         (*pSetXRayNoRequirement)(MKCharacter* obj, int enable, int unk);
static void         (*pSetInfiniteKrushingBlows)(MKCharacter* obj, int enable, int unk);
static void         (*pSetEasyKrushingBlows)(MKCharacter* obj, int enable, int unk);
static FGGameInfo** s_ppGameInfo;

static float GetLife(MKCharacter* obj)    { return *(float*)(obj + 0xC20); }
static float GetMaxLife(MKCharacter* obj) { return *(float*)(obj + 0xC24); }

static MKCharacter* GetObj(int plr) { return pGetObj(plr, 1); }

static PlayerInfo* GetInfo(int plr)
{
    FGGameInfo* gi = *s_ppGameInfo;
    return gi ? pGetInfo(gi, plr) : nullptr;
}

// ---------------------------------------------------------------------------
// Per-tick cheat logic
// ---------------------------------------------------------------------------
static std::mutex s_statusLock;
static GameStatus s_status;

GameStatus GameGetStatus()
{
    std::lock_guard<std::mutex> lock(s_statusLock);
    return s_status;
}

// Fatal Blow / Krushing Blow flags live on the character, so remember which
// one we set each flag on and clear it again when the option is switched off
// or the side changes. A character from a finished fight is gone, so it is
// only touched while it is still in play.
struct CharFlag
{
    void (**setter)(MKCharacter*, int, int);
    MKCharacter* setOn;

    // re-applied every tick, as MK11Hook does: the game clears some of these when the move lands
    void Update(MKCharacter* me, bool want)
    {
        if (setOn && (!want || setOn != me))
        {
            if (setOn == GetObj(PLAYER1) || setOn == GetObj(PLAYER2))
                (*setter)(setOn, 0, 1);
            setOn = nullptr;
        }
        if (want)
        {
            (*setter)(me, 1, 1);
            setOn = me;
        }
    }
};

static CharFlag s_fatalInfinite{ &pSetXRayInfinite };
static CharFlag s_fatalAnyHealth{ &pSetXRayNoRequirement };
static CharFlag s_kbInfinite{ &pSetInfiniteKrushingBlows };
static CharFlag s_kbEasy{ &pSetEasyKrushingBlows };

// Weak Opponents: scale each drop in the opponent's health by the multiplier.
static MKCharacter* s_weakChar;
static float        s_weakLastLife;

static void ApplyWeakOpponent(MKCharacter* opp)
{
    float life = GetLife(opp);
    if (opp != s_weakChar || life >= s_weakLastLife)
    {
        // new fight, new round, or health going up (training refill): just track it
        s_weakChar = opp;
        s_weakLastLife = life;
        return;
    }
    float damage = s_weakLastLife - life;
    float scaled = s_weakLastLife - damage * g_settings.weakOpponentsDamage.load();
    if (scaled < 0.0f) scaled = 0.0f;
    if (scaled < life)
        pSetLife(opp, scaled);
    s_weakLastLife = GetLife(opp);
}

static void Tick()
{
    const Settings& s = g_settings;
    const int you = s.playerSide.load();
    const int them = 1 - you;

    MKCharacter* me = GetObj(you);
    MKCharacter* opp = GetObj(them);

    {
        std::lock_guard<std::mutex> lock(s_statusLock);
        s_status.inFight = me && opp;
        s_status.lifeYou = me && GetMaxLife(me) > 0 ? GetLife(me) / GetMaxLife(me) : 0.0f;
        s_status.lifeOpp = opp && GetMaxLife(opp) > 0 ? GetLife(opp) / GetMaxLife(opp) : 0.0f;
    }

    const bool on = s.enabled.load() && me;

    const bool wantFatal = on && s.unlimitedFatalBlows.load();
    s_fatalInfinite.Update(me, wantFatal);
    s_fatalAnyHealth.Update(me, wantFatal && s.fatalBlowAnyHealth.load());

    const bool wantKB = on && s.unlimitedKrushingBlows.load();
    s_kbInfinite.Update(me, wantKB);
    s_kbEasy.Update(me, wantKB && s.easyKrushingBlows.load());

    if (!on)
    {
        s_weakChar = nullptr;
        return;
    }

    if (s.unlimitedHealth.load() && GetLife(me) < GetMaxLife(me))
        pSetLife(me, GetMaxLife(me));

    if (s.unlimitedAttackMeter.load() || s.unlimitedDefenseMeter.load())
        if (PlayerInfo* info = GetInfo(you))
        {
            if (s.unlimitedAttackMeter.load())  pSetMeter(info, BAR_Offensive, 1000.0f);
            if (s.unlimitedDefenseMeter.load()) pSetMeter(info, BAR_Defensive, 1000.0f);
        }

    if (opp && s.weakOpponents.load())
        ApplyWeakOpponent(opp);
    else
        s_weakChar = nullptr;
}

// ---------------------------------------------------------------------------
// Tick hook: the game calls its script dispatcher once per tick through a
// plain CALL rel32. Point that call at a relay next to the exe that jumps to
// us; we run the cheats and then call the real dispatcher.
// ---------------------------------------------------------------------------
using DispatchFn = void* (*)(void*, void*, void*, void*);
static DispatchFn s_origDispatch;

static void* TickHook(void* a, void* b, void* c, void* d)
{
    Tick();
    return s_origDispatch(a, b, c, d);
}

static void* AllocNear(uintptr_t target)
{
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    const uintptr_t gran = si.dwAllocationGranularity;
    const uintptr_t lo = target > 0x7FF00000 ? target - 0x7FF00000 : gran;
    const uintptr_t hi = target + 0x7FF00000;
    for (uintptr_t p = (target & ~(gran - 1)) - gran; p > lo; p -= gran)
        if (void* m = VirtualAlloc((void*)p, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE)) return m;
    for (uintptr_t p = (target & ~(gran - 1)) + gran; p < hi; p += gran)
        if (void* m = VirtualAlloc((void*)p, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE)) return m;
    return nullptr;
}

static bool HookCall(uintptr_t callSite, void* detour, void** original)
{
    if (*(uint8_t*)callSite != 0xE8) return false;
    *original = (void*)(callSite + 5 + *(int32_t*)(callSite + 1));

    auto relay = (uint8_t*)AllocNear(callSite);
    if (!relay) return false;
    relay[0] = 0xFF; relay[1] = 0x25; *(uint32_t*)(relay + 2) = 0;   // jmp [rip+0]
    *(void**)(relay + 6) = detour;
    FlushInstructionCache(GetCurrentProcess(), relay, 14);

    DWORD old;
    VirtualProtect((void*)(callSite + 1), 4, PAGE_EXECUTE_READWRITE, &old);
    *(int32_t*)(callSite + 1) = (int32_t)((uintptr_t)relay - (callSite + 5));
    VirtualProtect((void*)(callSite + 1), 4, old, &old);
    FlushInstructionCache(GetCurrentProcess(), (void*)callSite, 5);
    return true;
}

bool GameInit()
{
    FindText();
    Log("Scanning MK11.exe (.text %zu MB)", s_textSize >> 20);

    uintptr_t dispatchCall = Scan("TickDispatchCall", "E8 ? ? ? ? 48 8B 1D ? ? ? ? 48 8D 54 24 40 48 8B CB E8 ? ? ? ?");
    pGetObj = (decltype(pGetObj))Scan("GetObj", "40 53 48 83 EC 20 0F B6 DA 45 33 C0 8B D1 48 8B 0D ? ? ? ?");
    uintptr_t giRef = Scan("GameInfoRef", "40 53 48 83 EC 20 0F B6 DA 45 33 C0 8B D1 48 8B 0D ? ? ? ?", 17);
    pGetInfo = (decltype(pGetInfo))Scan("GetInfo", "83 FA 03 77 0C 48 63 C2 48 8B 84 C1 78 07 00 00 C3");
    pSetLife = (decltype(pSetLife))Scan("SetLife", "40 53 48 83 EC 70 48 C7 44 24 30 FE FF FF FF 0F 29 74 24 60 0F 28 F1");
    pSetMeter = (decltype(pSetMeter))Scan("SetMeter", "40 53 48 83 EC 30 0F 29 74 24 20 0F 57 F6 8B DA");
    pSetXRayInfinite = (decltype(pSetXRayInfinite))Scan("SetXRayInfinite",
        "40 53 48 83 EC 20 48 8B 81 ? ? ? ? 48 8B D9 48 85 C0 74 5B 80 F2 01 48 89 74 24 ? 48 89 7C 24 ? 0F B6 FA 89 B8");
    pSetXRayNoRequirement = (decltype(pSetXRayNoRequirement))Scan("SetXRayNoRequirement",
        "48 89 5C 24 ? 57 48 83 EC 20 33 C0 8B FA 41 83 F8 03 48 BA ? ? ? ? ? ? ? ? 44 0F 47 C0 49 63 C0 4C 8D 0C C1 4D 8B 81 ? ? ? ? 49 8B C0 48 0B C2");
    // bit setters on the character's ability-flag word at +0xDF0 (enable 0 clears the bit)
    pSetInfiniteKrushingBlows = (decltype(pSetInfiniteKrushingBlows))Scan("SetInfiniteKrushingBlows", "33 C0 49 B9 FF FF FF FF FF FF DF FF 41 83 F8 03");
    pSetEasyKrushingBlows = (decltype(pSetEasyKrushingBlows))Scan("SetEasyKrushingBlows", "33 C0 49 B9 FF FF FF FF FF FF FF FB 41 83 F8 03");

    if (g_missingPatterns > 0)
    {
        Log("%d signature(s) missing - this game version is not supported. Cheats disabled.", g_missingPatterns.load());
        return false;
    }

    s_ppGameInfo = (FGGameInfo**)(giRef + 4 + *(int32_t*)giRef);   // rip-relative mov rcx,[GameInfo]

    if (!HookCall(dispatchCall, (void*)&TickHook, (void**)&s_origDispatch))
    {
        Log("Could not hook the tick dispatch. Cheats disabled.");
        g_missingPatterns++;
        return false;
    }
    Log("Tick hook installed");
    return true;
}
