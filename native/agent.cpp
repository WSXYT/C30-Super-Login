#include "protocol.h"
#include <stdint.h>
#include <string.h>
#include <wchar.h>

using ExecJS = void(__thiscall*)(void*, const char*);
using IsReady = bool(__thiscall*)(void*);
using GetHost = HWND(__thiscall*)(void*);

static bool TryExecute(void* candidate, void* vtable, ExecJS exec, IsReady ready, GetHost host, SharedRequest* request) {
    __try {
        if (*(void**)candidate != vtable) return false;
        HWND hwnd = host(candidate);
        DWORD pid = 0;
        DWORD tid = GetWindowThreadProcessId(hwnd, &pid);
        if (pid != GetCurrentProcessId() || tid != GetCurrentThreadId()) return false;
        HWND intended = reinterpret_cast<HWND>(request->hwnd);
        if (hwnd != intended && !IsChild(intended, hwnd) && GetAncestor(hwnd, GA_ROOT) != intended) return false;
        if (!ready(candidate)) return false;
        exec(candidate, request->script);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static void ScanAndExecute(SharedRequest* request) {
    HMODULE module = GetModuleHandleW(L"DuiLib.dll");
    if (!module) { request->state = ModuleMissing; return; }
    auto exec = reinterpret_cast<ExecJS>(GetProcAddress(module, "?ExecJS@CCefBrowserUI@DuiLib@@QAEXPBD@Z"));
    auto ready = reinterpret_cast<IsReady>(GetProcAddress(module, "?IsCefCtrlReady@CCefBrowserUI@DuiLib@@QAE_NXZ"));
    auto host = reinterpret_cast<GetHost>(GetProcAddress(module, "?GetHostWindow@CCefBrowserUI@DuiLib@@QBEPAUHWND__@@XZ"));
    void* vtable = reinterpret_cast<void*>(GetProcAddress(module, "??_7CCefBrowserUI@DuiLib@@6BCControlUI@1@@"));
    if (!exec || !ready || !host || !vtable) { request->state = ExportMissing; return; }
    SYSTEM_INFO info{}; GetSystemInfo(&info);
    uintptr_t current = reinterpret_cast<uintptr_t>(info.lpMinimumApplicationAddress);
    uintptr_t maximum = reinterpret_cast<uintptr_t>(info.lpMaximumApplicationAddress);
    constexpr uint32_t budget = 128 * 1024 * 1024;
    // Only object heads in committed writable private memory, never arbitrary guessed code offsets.
    while (current < maximum && request->scannedBytes < budget) {
        MEMORY_BASIC_INFORMATION region{};
        if (!VirtualQuery(reinterpret_cast<void*>(current), &region, sizeof(region))) break;
        uintptr_t begin = reinterpret_cast<uintptr_t>(region.BaseAddress);
        uintptr_t end = begin + region.RegionSize;
        if (end <= current) break;
        DWORD access = region.Protect & 0xff;
        if (region.State == MEM_COMMIT && region.Type == MEM_PRIVATE && !(region.Protect & PAGE_GUARD) &&
            (access == PAGE_READWRITE || access == PAGE_EXECUTE_READWRITE)) {
            uintptr_t limit = begin + min(static_cast<uintptr_t>(budget - request->scannedBytes), region.RegionSize);
            __try {
                for (uintptr_t p = begin; p + sizeof(void*) <= limit; p += sizeof(void*)) {
                    request->scannedBytes += sizeof(void*);
                    if (*reinterpret_cast<void**>(p) != vtable) continue;
                    request->candidates++;
                    if (TryExecute(reinterpret_cast<void*>(p), vtable, exec, ready, host, request)) {
                        request->queued++;
                        if (request->queued >= 8) { request->state = Queued; return; }
                    }
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) { /* page changed while scanning; skip it */ }
        }
        current = end;
    }
    request->state = request->queued ? Queued : request->scannedBytes >= budget ? ScanLimit : NoControl;
}
extern "C" __declspec(dllexport) LRESULT CALLBACK C30HookProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0 && lParam) {
        __try {
            auto message = reinterpret_cast<CWPSTRUCT*>(lParam);
            if (message->message == WM_COPYDATA) {
                auto data = reinterpret_cast<COPYDATASTRUCT*>(message->lParam);
                if (data && data->dwData == C30_COPYDATA && data->cbData >= 32 && data->cbData <= 256 && data->cbData % 2 == 0 && data->lpData) {
                    const wchar_t* name = static_cast<const wchar_t*>(data->lpData);
                    if (name[data->cbData / sizeof(wchar_t) - 1] == 0 && wcsncmp(name, L"Local\\C30SuperLogin-", 20) == 0) {
                        HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
                        if (mapping) {
                            auto request = static_cast<SharedRequest*>(MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(SharedRequest)));
                            if (request) {
                                if (request->magic == C30_MAGIC && request->size == sizeof(SharedRequest) && request->state == Pending &&
                                    request->pid == GetCurrentProcessId() && request->tid == GetCurrentThreadId() && request->hwnd == reinterpret_cast<uint32_t>(message->hwnd) &&
                                    request->scriptBytes > 0 && request->scriptBytes < C30_SCRIPT_CAPACITY && request->script[request->scriptBytes] == 0) ScanAndExecute(request);
                                UnmapViewOfFile(request);
                            }
                            CloseHandle(mapping);
                        }
                    }
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) { }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
