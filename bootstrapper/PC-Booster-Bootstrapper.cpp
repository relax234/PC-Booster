#define NOMINMAX
#include <windows.h>
#include <wininet.h>
#include <bcrypt.h>
#include <commctrl.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <fstream>
#include <sstream>
#include <algorithm>

#pragma comment(lib, "wininet.lib")
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")

static HWND g_status = nullptr, g_progress = nullptr, g_window = nullptr;
static const wchar_t* MANIFEST_URL = L"https://raw.githubusercontent.com/relax234/PC-Booster/main/latest.json";
static const wchar_t* SETUP_URL = L"https://github.com/relax234/PC-Booster/releases/latest/download/PC-Booster-Setup.exe";

static void SetStatus(const std::wstring& s) {
    if (g_status) SetWindowTextW(g_status, s.c_str());
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
}
static std::string Utf8(const std::string& s) { return s; }

static bool HttpGet(const wchar_t* url, std::vector<unsigned char>& out, unsigned long long expected, bool showProgress) {
    HINTERNET session = InternetOpenW(L"PC Booster Setup/1.0", INTERNET_OPEN_TYPE_PRECONFIG, nullptr, nullptr, 0);
    if (!session) return false;
    HINTERNET request = InternetOpenUrlW(session, url, nullptr, 0, INTERNET_FLAG_RELOAD | INTERNET_FLAG_NO_CACHE_WRITE | INTERNET_FLAG_SECURE, 0);
    if (!request) { InternetCloseHandle(session); return false; }
    DWORD status = 0, statusSize = sizeof(status);
    HttpQueryInfoW(request, HTTP_QUERY_STATUS_CODE | HTTP_QUERY_FLAG_NUMBER, &status, &statusSize, nullptr);
    if (status < 200 || status >= 300) { InternetCloseHandle(request); InternetCloseHandle(session); return false; }
    DWORD length = 0, lengthSize = sizeof(length);
    unsigned long long total = expected;
    if (HttpQueryInfoW(request, HTTP_QUERY_CONTENT_LENGTH | HTTP_QUERY_FLAG_NUMBER, &length, &lengthSize, nullptr) && length > 0) total = length;
    BYTE buffer[65536]; DWORD read = 0; unsigned long long received = 0;
    do {
        if (!InternetReadFile(request, buffer, sizeof(buffer), &read)) { InternetCloseHandle(request); InternetCloseHandle(session); return false; }
        if (read) {
            out.insert(out.end(), buffer, buffer + read); received += read;
            if (out.size() > 250ull * 1024 * 1024) { InternetCloseHandle(request); InternetCloseHandle(session); return false; }
            if (showProgress && g_progress) {
                int pos = total ? (int)std::min(100ull, received * 100ull / total) : 0;
                SendMessageW(g_progress, PBM_SETPOS, pos, 0);
                SetStatus(L"Загрузка установщика: " + std::to_wstring(received / 1048576) + L" МБ");
            }
        }
    } while (read);
    InternetCloseHandle(request); InternetCloseHandle(session);
    return !out.empty() && (!expected || received == expected);
}
static std::string ReadString(const std::vector<unsigned char>& v) {
    return std::string(v.begin(), v.end());
}
static std::string JsonString(const std::string& json, const std::string& key) {
    std::string token = "\"" + key + "\"";
    size_t p = json.find(token); if (p == std::string::npos) return {};
    p = json.find(':', p + token.size()); if (p == std::string::npos) return {};
    p = json.find('"', p + 1); if (p == std::string::npos) return {};
    size_t e = json.find('"', p + 1); if (e == std::string::npos) return {};
    return json.substr(p + 1, e - p - 1);
}
static unsigned long long JsonNumber(const std::string& json, const std::string& key) {
    std::string token = "\"" + key + "\"";
    size_t p = json.find(token); if (p == std::string::npos) return 0;
    p = json.find(':', p + token.size()); if (p == std::string::npos) return 0;
    return _strtoui64(json.c_str() + p + 1, nullptr, 10);
}
static std::string Sha256(const std::wstring& path) {
    std::ifstream f(path, std::ios::binary); if (!f) return {};
    BCRYPT_ALG_HANDLE alg = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD objLen = 0, cb = 0, hashLen = 0;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    BCryptGetProperty(alg, BCRYPT_OBJECT_LENGTH, (PUCHAR)&objLen, sizeof(objLen), &cb, 0);
    BCryptGetProperty(alg, BCRYPT_HASH_LENGTH, (PUCHAR)&hashLen, sizeof(hashLen), &cb, 0);
    std::vector<UCHAR> obj(objLen), digest(hashLen);
    if (BCryptCreateHash(alg, &hash, obj.data(), objLen, nullptr, 0, 0) < 0) { BCryptCloseAlgorithmProvider(alg,0); return {}; }
    char buf[65536];
    while (f) { f.read(buf, sizeof(buf)); auto n = (ULONG)f.gcount(); if (n && BCryptHashData(hash, (PUCHAR)buf, n, 0) < 0) break; }
    bool ok = !f.bad() && BCryptFinishHash(hash, digest.data(), hashLen, 0) >= 0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(alg,0);
    if (!ok) return {};
    static const char* hex = "0123456789abcdef";
    std::string out; out.reserve(hashLen * 2);
    for (auto b : digest) { out += hex[b >> 4]; out += hex[b & 15]; }
    return out;
}
static std::wstring TempSetupPath() {
    wchar_t temp[MAX_PATH]{}; GetTempPathW(MAX_PATH, temp);
    return std::wstring(temp) + L"PC-Booster-Setup-latest.exe";
}
static bool RunSetup() {
    std::vector<unsigned char> manifestBytes;
    SetStatus(L"Проверяю последнюю версию...");
    if (!HttpGet(MANIFEST_URL, manifestBytes, 0, false)) return false;
    std::string manifest = ReadString(manifestBytes);
    std::string expectedHash = JsonString(manifest, "installerSha256");
    unsigned long long expectedSize = JsonNumber(manifest, "installerSize");
    if (expectedHash.size() != 64 || expectedSize < 1000000 || expectedSize > 200ull * 1024 * 1024) return false;
    std::vector<unsigned char> installer;
    SetStatus(L"Скачиваю установщик PC Booster...");
    if (!HttpGet(SETUP_URL, installer, expectedSize, true)) return false;
    std::wstring path = TempSetupPath();
    { std::ofstream f(path, std::ios::binary | std::ios::trunc); if (!f) return false; f.write((const char*)installer.data(), installer.size()); if (!f) return false; }
    SetStatus(L"Проверяю целостность файла...");
    std::string actual = Sha256(path);
    if (actual.empty() || _stricmp(actual.c_str(), expectedHash.c_str()) != 0) { DeleteFileW(path.c_str()); return false; }
    SetStatus(L"Запускаю установщик PC Booster...");
    SHELLEXECUTEINFOW info{}; info.cbSize = sizeof(info); info.fMask = SEE_MASK_NOCLOSEPROCESS;
    info.lpVerb = L"open"; info.lpFile = path.c_str(); info.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&info)) return false;
    if (info.hProcess) CloseHandle(info.hProcess);
    return true;
}
static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_CREATE) {
        g_status = CreateWindowW(L"STATIC", L"Подготовка...", WS_CHILD | WS_VISIBLE, 24, 24, 430, 28, h, nullptr, nullptr, nullptr);
        g_progress = CreateWindowExW(0, PROGRESS_CLASSW, nullptr, WS_CHILD | WS_VISIBLE, 24, 66, 430, 20, h, nullptr, nullptr, nullptr);
        SendMessageW(g_progress, PBM_SETRANGE, 0, MAKELPARAM(0,100));
        return 0;
    }
    if (m == WM_CLOSE) { DestroyWindow(h); return 0; }
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h,m,w,l);
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    INITCOMMONCONTROLSEX cc{sizeof(cc), ICC_PROGRESS_CLASS}; InitCommonControlsEx(&cc);
    WNDCLASSW wc{}; wc.lpfnWndProc = WndProc; wc.hInstance = instance; wc.lpszClassName = L"PCBoosterBootstrapper";
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW); wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    RegisterClassW(&wc);
    g_window = CreateWindowW(wc.lpszClassName, L"Установка PC Booster", WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
        CW_USEDEFAULT, CW_USEDEFAULT, 500, 150, nullptr, nullptr, instance, nullptr);
    if (!g_window) return 1;
    ShowWindow(g_window, show); UpdateWindow(g_window);
    bool ok = RunSetup();
    if (ok) {
        MessageBoxW(g_window, L"Установщик запущен. Следуйте инструкциям на экране.", L"PC Booster", MB_OK | MB_ICONINFORMATION);
    } else {
        MessageBoxW(g_window, L"Не удалось загрузить или проверить установщик. Проверьте подключение к интернету и попробуйте ещё раз.", L"PC Booster", MB_OK | MB_ICONERROR);
    }
    if (g_window && IsWindow(g_window)) DestroyWindow(g_window);
    return ok ? 0 : 1;
}
