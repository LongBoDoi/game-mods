#pragma once
#include <windows.h>
#include <atomic>
#include <cstdint>

// ---------------------------------------------------------------------------
// Settings shared between the overlay (render thread) and the cheat tick
// (game thread). Plain atomics: the tick only ever reads them.
// ---------------------------------------------------------------------------
struct Settings
{
    std::atomic<bool>  enabled{ true };           // master switch
    std::atomic<int>   playerSide{ 0 };           // 0 = you are Player 1, 1 = Player 2

    std::atomic<bool>  unlimitedHealth{ false };
    std::atomic<bool>  unlimitedAttackMeter{ false };
    std::atomic<bool>  unlimitedDefenseMeter{ false };
    std::atomic<bool>  weakOpponents{ false };
    std::atomic<float> weakOpponentsDamage{ 3.0f }; // damage the opponent takes, as a multiple
    std::atomic<bool>  unlimitedFatalBlows{ false };
    std::atomic<bool>  fatalBlowAnyHealth{ true };  // also lift the "below 30% health" requirement
    std::atomic<bool>  unlimitedKrushingBlows{ false };
    std::atomic<bool>  easyKrushingBlows{ false };  // also skip each Krushing Blow's trigger condition

    UINT  toggleKey = VK_F1;
    float panelX = -1.0f, panelY = -1.0f;         // -1 = default corner
};

extern Settings g_settings;

// config.cpp
void ConfigLoad();
void ConfigSave();
const char* KeyName(UINT vk);

// main.cpp
void Log(const char* fmt, ...);
extern HMODULE g_module;

// game.cpp
bool GameInit();                                  // pattern scan + tick hook; false if the build is unsupported
struct GameStatus { bool inFight; float lifeYou, lifeOpp; };
GameStatus GameGetStatus();                       // snapshot for the panel, written by the tick
extern std::atomic<int> g_missingPatterns;

// overlay.cpp
bool OverlayInit();                               // hooks IDXGISwapChain::Present / ResizeBuffers
