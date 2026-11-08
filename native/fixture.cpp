#include <windows.h>
#include <stdio.h>
LRESULT CALLBACK Window(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == WM_CLOSE) { DestroyWindow(hwnd); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    WNDCLASSW cls{}; cls.lpfnWndProc = Window; cls.lpszClassName = L"C30SuperLogin.TestFixture"; cls.hInstance = GetModuleHandleW(nullptr);
    RegisterClassW(&cls);
    HWND hwnd = CreateWindowW(cls.lpszClassName, L"C30 injection test fixture", WS_OVERLAPPEDWINDOW, 0, 0, 400, 200, nullptr, nullptr, cls.hInstance, nullptr);
    HMODULE module = LoadLibraryW(L"DuiLib.dll"); if (!module || !hwnd) return 3;
    auto create = reinterpret_cast<void*(__cdecl*)(HWND, const wchar_t*)>(GetProcAddress(module, "CreateFixture"));
    if (!create || !create(hwnd, argv[2])) return 4;
    FILE* state{}; _wfopen_s(&state, argv[1], L"wb");
    if (state) { fprintf(state, "{\"pid\":%lu,\"hwnd\":\"%p\"}", GetCurrentProcessId(), hwnd); fclose(state); }
    MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
