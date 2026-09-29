// Overlay: hook IDXGISwapChain::Present, draw the tweak panel with Dear ImGui,
// and keep mouse/keyboard away from the game while the panel is open.
#include "common.h"
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>
#include <cstring>
#include <mutex>
#include "MinHook.h"
#include "imgui.h"
#include "imgui_impl_dx11.h"
#include "imgui_impl_win32.h"

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND, UINT, WPARAM, LPARAM);

using PresentFn = HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT);
using ResizeBuffersFn = HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
static PresentFn s_origPresent;
static ResizeBuffersFn s_origResizeBuffers;

static bool s_ready;
static HWND s_hwnd;
static WNDPROC s_origWndProc;
static ID3D11Device* s_device;
static ID3D11DeviceContext* s_context;
static ID3D11RenderTargetView* s_rtv;
static std::recursive_mutex s_imguiLock;   // ImGui input arrives on the window thread, frames on the render thread

static std::atomic<bool> s_open{ false };
static ULONGLONG s_toastUntil;             // "loaded" notice shown for a few seconds at start-up

// ---------------------------------------------------------------------------
// Input
// ---------------------------------------------------------------------------
static bool IsInputMessage(UINT msg)
{
    return (msg >= WM_MOUSEFIRST && msg <= WM_MOUSELAST) || (msg >= WM_KEYFIRST && msg <= WM_KEYLAST) || msg == WM_INPUT;
}

static void TogglePanel()
{
    bool open = !s_open.load();
    s_open = open;
    if (!open) ConfigSave();   // picks up the panel position
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && wParam == g_settings.toggleKey && !(lParam & (1 << 30)))
    {
        TogglePanel();
        return 0;
    }

    if (s_open && s_ready)
    {
        {
            std::lock_guard<std::recursive_mutex> lock(s_imguiLock);
            ImGui_ImplWin32_WndProcHandler(hwnd, msg, wParam, lParam);
        }
        if (msg == WM_INPUT)
            return DefWindowProcW(hwnd, msg, wParam, lParam);   // lets Windows clean up the raw input
        if (IsInputMessage(msg))
            return 0;
    }
    return CallWindowProcW(s_origWndProc, hwnd, msg, wParam, lParam);
}

// ---------------------------------------------------------------------------
// Panel
// ---------------------------------------------------------------------------
static void Description(const char* text)
{
    ImGui::Indent(ImGui::GetFrameHeight() + ImGui::GetStyle().ItemInnerSpacing.x);
    ImGui::PushStyleColor(ImGuiCol_Text, ImGui::GetStyleColorVec4(ImGuiCol_TextDisabled));
    ImGui::TextWrapped("%s", text);
    ImGui::PopStyleColor();
    ImGui::Unindent(ImGui::GetFrameHeight() + ImGui::GetStyle().ItemInnerSpacing.x);
    ImGui::Spacing();
}

// Checkbox over an atomic<bool>; saves the ini on change.
static bool Toggle(const char* label, std::atomic<bool>& value)
{
    bool v = value.load();
    if (ImGui::Checkbox(label, &v))
    {
        value = v;
        ConfigSave();
        return true;
    }
    return false;
}

static void HealthBar(const char* label, float fraction, const ImVec4& color)
{
    ImGui::TextUnformatted(label);
    ImGui::SameLine(ImGui::GetFontSize() * 4.5f);
    ImGui::PushStyleColor(ImGuiCol_PlotHistogram, color);
    char overlay[16];
    snprintf(overlay, sizeof(overlay), "%.0f%%", fraction * 100.0f);
    ImGui::ProgressBar(fraction, ImVec2(-1, ImGui::GetFontSize() * 0.9f), overlay);
    ImGui::PopStyleColor();
}

static void DrawPanel()
{
    Settings& s = g_settings;
    const float em = ImGui::GetFontSize();

    ImGui::SetNextWindowPos(s.panelX >= 0 ? ImVec2(s.panelX, s.panelY) : ImVec2(em * 2, em * 2), ImGuiCond_Once);
    ImGui::SetNextWindowSize(ImVec2(em * 24, 0), ImGuiCond_Always);
    bool open = true;
    if (!ImGui::Begin("MK11 Tweaks", &open, ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoSavedSettings))
    {
        ImGui::End();
        return;
    }

    ImVec2 pos = ImGui::GetWindowPos();
    s.panelX = pos.x;
    s.panelY = pos.y;

    if (g_missingPatterns > 0)
    {
        ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(1.0f, 0.45f, 0.4f, 1.0f));
        ImGui::TextWrapped("This version of MK11 is not supported (%d signature(s) not found). "
                           "Nothing below will have any effect. See MK11Tweaks.log.", g_missingPatterns.load());
        ImGui::PopStyleColor();
        ImGui::Separator();
    }

    // status
    GameStatus st = GameGetStatus();
    if (st.inFight)
    {
        HealthBar("You", st.lifeYou, ImVec4(0.30f, 0.75f, 0.35f, 1.0f));
        HealthBar("Opponent", st.lifeOpp, ImVec4(0.80f, 0.25f, 0.22f, 1.0f));
    }
    else
        ImGui::TextDisabled("Not in a fight - options apply once one starts.");
    ImGui::Spacing();

    Toggle("Mod enabled", s.enabled);
    ImGui::SameLine(em * 12);
    ImGui::SetNextItemWidth(-1);
    int side = s.playerSide.load();
    if (ImGui::Combo("##side", &side, "I am Player 1\0I am Player 2\0"))
    {
        s.playerSide = side;
        ConfigSave();
    }

    ImGui::SeparatorText("Cheats");
    ImGui::BeginDisabled(!s.enabled.load());

    Toggle("Unlimited Health", s.unlimitedHealth);
    Description("Your health bar is refilled the moment you take damage.");

    Toggle("Unlimited Attack Meter", s.unlimitedAttackMeter);
    Description("Offensive bar always full: amplify specials as often as you like.");

    Toggle("Unlimited Defense Meter", s.unlimitedDefenseMeter);
    Description("Defensive bar always full: breakaways, wake-up rolls and blocks that cost meter.");

    Toggle("Weak Opponents", s.weakOpponents);
    Description("Every hit takes more health off your opponent.");
    ImGui::BeginDisabled(!s.weakOpponents.load());
    ImGui::Indent(ImGui::GetFrameHeight() + ImGui::GetStyle().ItemInnerSpacing.x);
    ImGui::SetNextItemWidth(-1);
    float dmg = s.weakOpponentsDamage.load();
    if (ImGui::SliderFloat("##dmg", &dmg, 1.0f, 20.0f, "Damage x%.1f", ImGuiSliderFlags_AlwaysClamp))
        s.weakOpponentsDamage = dmg;
    if (ImGui::IsItemDeactivatedAfterEdit()) ConfigSave();
    ImGui::Unindent(ImGui::GetFrameHeight() + ImGui::GetStyle().ItemInnerSpacing.x);
    ImGui::EndDisabled();
    ImGui::Spacing();

    Toggle("Unlimited Fatal Attacks", s.unlimitedFatalBlows);
    Description("Your Fatal Blow can be used again after it lands, as many times as you like.");
    ImGui::BeginDisabled(!s.unlimitedFatalBlows.load());
    ImGui::Indent(ImGui::GetFrameHeight() + ImGui::GetStyle().ItemInnerSpacing.x);
    Toggle("Available at any health", s.fatalBlowAnyHealth);
    ImGui::Unindent(ImGui::GetFrameHeight() + ImGui::GetStyle().ItemInnerSpacing.x);
    ImGui::EndDisabled();
    ImGui::Spacing();

    Toggle("Unlimited Krushing Blows", s.unlimitedKrushingBlows);
    Description("Each Krushing Blow can be landed again and again, not just once per match.");
    ImGui::BeginDisabled(!s.unlimitedKrushingBlows.load());
    ImGui::Indent(ImGui::GetFrameHeight() + ImGui::GetStyle().ItemInnerSpacing.x);
    Toggle("Easy - no trigger condition needed", s.easyKrushingBlows);
    ImGui::Unindent(ImGui::GetFrameHeight() + ImGui::GetStyle().ItemInnerSpacing.x);
    ImGui::EndDisabled();

    ImGui::EndDisabled();

    ImGui::Separator();
    ImGui::TextDisabled("%s closes this panel. Offline only. Settings save automatically.", KeyName(s.toggleKey));

    ImGui::End();
    if (!open) TogglePanel();
}

static void DrawToast()
{
    const float em = ImGui::GetFontSize();
    ImGui::SetNextWindowPos(ImVec2(em, em));
    ImGui::SetNextWindowBgAlpha(0.8f);
    ImGui::Begin("##toast", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoInputs |
                                     ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoNav);
    if (g_missingPatterns > 0)
        ImGui::TextColored(ImVec4(1.0f, 0.45f, 0.4f, 1.0f), "MK11 Tweaks: game version not supported - see MK11Tweaks.log");
    else
        ImGui::Text("MK11 Tweaks loaded - press %s", KeyName(g_settings.toggleKey));
    ImGui::End();
}

static void ApplyStyle(float scale)
{
    ImGuiStyle& st = ImGui::GetStyle();
    ImGui::StyleColorsDark();
    st.WindowRounding = 8.0f;
    st.FrameRounding = 4.0f;
    st.GrabRounding = 4.0f;
    st.WindowPadding = ImVec2(14, 12);
    st.FramePadding = ImVec2(6, 4);
    st.ItemSpacing = ImVec2(8, 6);
    st.WindowTitleAlign = ImVec2(0.5f, 0.5f);

    ImVec4* c = st.Colors;
    const ImVec4 red(0.72f, 0.12f, 0.10f, 1.0f), redHi(0.85f, 0.20f, 0.16f, 1.0f), gold(0.95f, 0.72f, 0.25f, 1.0f);
    c[ImGuiCol_WindowBg] = ImVec4(0.07f, 0.07f, 0.08f, 0.96f);
    c[ImGuiCol_TitleBg] = c[ImGuiCol_TitleBgActive] = ImVec4(0.35f, 0.05f, 0.04f, 1.0f);
    c[ImGuiCol_FrameBg] = ImVec4(0.16f, 0.16f, 0.18f, 1.0f);
    c[ImGuiCol_FrameBgHovered] = ImVec4(0.24f, 0.20f, 0.20f, 1.0f);
    c[ImGuiCol_FrameBgActive] = ImVec4(0.30f, 0.18f, 0.16f, 1.0f);
    c[ImGuiCol_CheckMark] = gold;
    c[ImGuiCol_SliderGrab] = red;
    c[ImGuiCol_SliderGrabActive] = redHi;
    c[ImGuiCol_Button] = ImVec4(0.30f, 0.08f, 0.07f, 1.0f);
    c[ImGuiCol_ButtonHovered] = red;
    c[ImGuiCol_ButtonActive] = redHi;
    c[ImGuiCol_Header] = ImVec4(0.30f, 0.08f, 0.07f, 1.0f);
    c[ImGuiCol_HeaderHovered] = red;
    c[ImGuiCol_HeaderActive] = redHi;
    c[ImGuiCol_Separator] = ImVec4(0.35f, 0.30f, 0.30f, 1.0f);
    c[ImGuiCol_TextDisabled] = ImVec4(0.60f, 0.58f, 0.56f, 1.0f);

    st.ScaleAllSizes(scale);
    st.FontSizeBase = 17.0f;
    st.FontScaleMain = scale;
}

// ---------------------------------------------------------------------------
// D3D11
// ---------------------------------------------------------------------------
static void CreateRenderTarget(IDXGISwapChain* swap)
{
    ID3D11Texture2D* back = nullptr;
    if (FAILED(swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back)) || !back) return;

    D3D11_TEXTURE2D_DESC td;
    back->GetDesc(&td);
    D3D11_RENDER_TARGET_VIEW_DESC rd = {};
    rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    switch (td.Format)   // a typeless back buffer needs an explicit view format
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: rd.Format = DXGI_FORMAT_R8G8B8A8_UNORM; break;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: rd.Format = DXGI_FORMAT_B8G8R8A8_UNORM; break;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: rd.Format = DXGI_FORMAT_R10G10B10A2_UNORM; break;
    default: rd.Format = td.Format; break;
    }
    if (FAILED(s_device->CreateRenderTargetView(back, &rd, &s_rtv)))
        Log("CreateRenderTargetView failed (format %d)", (int)td.Format);
    back->Release();
}

static bool InitImGui(IDXGISwapChain* swap)
{
    DXGI_SWAP_CHAIN_DESC sd;
    if (FAILED(swap->GetDesc(&sd)) || FAILED(swap->GetDevice(__uuidof(ID3D11Device), (void**)&s_device)))
        return false;
    s_device->GetImmediateContext(&s_context);
    s_hwnd = sd.OutputWindow;

    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.LogFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;   // leave the game's cursor alone

    RECT rc;
    GetClientRect(s_hwnd, &rc);
    float scale = (rc.bottom - rc.top) / 1080.0f;
    if (scale < 1.0f) scale = 1.0f;

    char font[MAX_PATH];
    GetWindowsDirectoryA(font, MAX_PATH);
    strcat(font, "\\Fonts\\segoeui.ttf");
    if (GetFileAttributesA(font) != INVALID_FILE_ATTRIBUTES)
        io.Fonts->AddFontFromFileTTF(font);
    else
        io.Fonts->AddFontDefault();
    ApplyStyle(scale);

    ImGui_ImplWin32_Init(s_hwnd);
    ImGui_ImplDX11_Init(s_device, s_context);
    CreateRenderTarget(swap);

    s_origWndProc = (WNDPROC)SetWindowLongPtrW(s_hwnd, GWLP_WNDPROC, (LONG_PTR)WndProc);
    s_toastUntil = GetTickCount64() + 7000;
    Log("Overlay ready (%ldx%ld, UI scale %.2f)", rc.right - rc.left, rc.bottom - rc.top, scale);
    return true;
}

static HRESULT WINAPI PresentHook(IDXGISwapChain* swap, UINT sync, UINT flags)
{
    if (!s_ready)
        s_ready = InitImGui(swap);

    const bool toast = GetTickCount64() < s_toastUntil;
    if (s_ready && (s_open || toast) && !(flags & DXGI_PRESENT_TEST))
    {
        if (!s_rtv) CreateRenderTarget(swap);

        std::lock_guard<std::recursive_mutex> lock(s_imguiLock);
        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();

        // draw our own cursor only when the game has hidden the system one
        CURSORINFO ci = { sizeof(ci) };
        ImGui::GetIO().MouseDrawCursor = s_open && GetCursorInfo(&ci) && !(ci.flags & CURSOR_SHOWING);

        ImGui::NewFrame();
        if (s_open) DrawPanel();
        if (toast && !s_open) DrawToast();
        ImGui::Render();

        if (s_rtv)
        {
            ID3D11RenderTargetView* prevRtv = nullptr;
            ID3D11DepthStencilView* prevDsv = nullptr;
            s_context->OMGetRenderTargets(1, &prevRtv, &prevDsv);
            s_context->OMSetRenderTargets(1, &s_rtv, nullptr);
            ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
            s_context->OMSetRenderTargets(1, &prevRtv, prevDsv);
            if (prevRtv) prevRtv->Release();
            if (prevDsv) prevDsv->Release();
        }
    }
    return s_origPresent(swap, sync, flags);
}

static HRESULT WINAPI ResizeBuffersHook(IDXGISwapChain* swap, UINT count, UINT w, UINT h, DXGI_FORMAT fmt, UINT flags)
{
    if (s_rtv) { s_rtv->Release(); s_rtv = nullptr; }   // recreated on the next Present
    return s_origResizeBuffers(swap, count, w, h, fmt, flags);
}

bool OverlayInit()
{
    // A throwaway device + swap chain gives us the vtable shared by every swap chain in the process.
    WNDCLASSEXW wc = { sizeof(wc), 0, DefWindowProcW, 0, 0, GetModuleHandleW(nullptr), nullptr, nullptr, nullptr, nullptr, L"MK11TweaksDummy" };
    RegisterClassExW(&wc);
    HWND wnd = CreateWindowW(wc.lpszClassName, L"", WS_OVERLAPPED, 0, 0, 16, 16, nullptr, nullptr, wc.hInstance, nullptr);

    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 1;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = wnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    IDXGISwapChain* swap = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_0;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_NULL, nullptr, 0, &fl, 1, D3D11_SDK_VERSION, &sd, &swap, &dev, nullptr, &ctx);
    if (FAILED(hr))
    {
        Log("Dummy D3D11 device failed (0x%08lX) - no overlay", hr);
        DestroyWindow(wnd);
        return false;
    }
    void** vtbl = *(void***)swap;
    void* present = vtbl[8];
    void* resize = vtbl[13];
    swap->Release(); dev->Release(); ctx->Release();
    DestroyWindow(wnd);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);

    MH_Initialize();
    if (MH_CreateHook(present, (void*)&PresentHook, (void**)&s_origPresent) != MH_OK ||
        MH_CreateHook(resize, (void*)&ResizeBuffersHook, (void**)&s_origResizeBuffers) != MH_OK ||
        MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
    {
        Log("Could not hook Present - no overlay");
        return false;
    }
    Log("Present hooked");
    return true;
}
