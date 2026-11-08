#include "protocol.h"
#include <sddl.h>
#include <stdio.h>
#include <string>
#include <iostream>
#include <iterator>
#include <objbase.h>
#include <io.h>
#include <fcntl.h>

static int Output(const char* code, DWORD error = 0, SharedRequest* r = nullptr) {
    printf("{\"Code\":\"%s\",\"Win32Error\":%lu,\"Candidates\":%u,\"Queued\":%u,\"ScannedBytes\":%u}\n",
        code, error, r ? r->candidates : 0, r ? r->queued : 0, r ? r->scannedBytes : 0);
    return r && r->queued ? 0 : 2;
}
static std::wstring OwnerSddl() {
    HANDLE token{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return L"";
    DWORD length = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &length);
    std::string storage(length, '\0');
    if (!GetTokenInformation(token, TokenUser, storage.data(), length, &length)) { CloseHandle(token); return L""; }
    LPWSTR sid{}; ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid, &sid);
    std::wstring result = sid ? L"D:P(A;;GA;;;SY)(A;;GA;;;" + std::wstring(sid) + L")" : L"";
    if (sid) LocalFree(sid); CloseHandle(token); return result;
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 5 || wcscmp(argv[1], L"--pid") || wcscmp(argv[3], L"--hwnd")) return Output("invalid_arguments");
    DWORD pid = wcstoul(argv[2], nullptr, 10);
    HWND hwnd = reinterpret_cast<HWND>(static_cast<uintptr_t>(_wcstoui64(argv[4], nullptr, 16)));
    DWORD owner = 0; DWORD tid = GetWindowThreadProcessId(hwnd, &owner);
    if (!pid || owner != pid || !tid) return Output("window_mismatch", ERROR_INVALID_WINDOW_HANDLE);
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return Output("access_denied", GetLastError());
    BOOL selfWow = FALSE, targetWow = FALSE;
    bool queryOk = IsWow64Process(GetCurrentProcess(), &selfWow) && IsWow64Process(process, &targetWow);
    CloseHandle(process);
    if (!queryOk || selfWow != targetWow) return Output("architecture_mismatch", ERROR_BAD_EXE_FORMAT);
    _setmode(_fileno(stdin), _O_BINARY);
    std::string script;
    script.reserve(8192);
    char buffer[4096];
    while (std::cin.read(buffer, sizeof(buffer)) || std::cin.gcount()) {
        script.append(buffer, static_cast<size_t>(std::cin.gcount()));
        if (script.size() >= C30_SCRIPT_CAPACITY) return Output("script_too_large", ERROR_BUFFER_OVERFLOW);
    }
    if (script.empty()) return Output("empty_script");
    wchar_t location[MAX_PATH]{}; GetModuleFileNameW(nullptr, location, MAX_PATH);
    wchar_t* end = wcsrchr(location, L'\\'); if (!end) return Output("helper_path_error");
    wcscpy_s(end + 1, MAX_PATH - (end + 1 - location), L"C30Agent.dll");
    HMODULE agent = LoadLibraryExW(location, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!agent) return Output("agent_load_failed", GetLastError());
    auto hookProc = reinterpret_cast<HOOKPROC>(GetProcAddress(agent, "_C30HookProc@12"));
    if (!hookProc) hookProc = reinterpret_cast<HOOKPROC>(GetProcAddress(agent, "C30HookProc"));
    if (!hookProc) { FreeLibrary(agent); return Output("agent_export_missing"); }
    GUID guid{}; CoCreateGuid(&guid); wchar_t guidText[40]{}; StringFromGUID2(guid, guidText, 40);
    std::wstring name = L"Local\\C30SuperLogin-" + std::to_wstring(pid) + L"-" + guidText;
    PSECURITY_DESCRIPTOR descriptor{};
    auto sddl = OwnerSddl();
    if (sddl.empty() || !ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) {
        FreeLibrary(agent); return Output("mapping_security_error", GetLastError());
    }
    SECURITY_ATTRIBUTES attributes{ sizeof(attributes), descriptor, FALSE };
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, sizeof(SharedRequest), name.c_str());
    LocalFree(descriptor);
    if (!mapping) { FreeLibrary(agent); return Output("mapping_failed", GetLastError()); }
    auto request = static_cast<SharedRequest*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedRequest)));
    if (!request) { DWORD error = GetLastError(); CloseHandle(mapping); FreeLibrary(agent); return Output("mapping_failed", error); }
    ZeroMemory(request, sizeof(*request)); request->magic = C30_MAGIC; request->size = sizeof(*request);
    request->pid = pid; request->tid = tid; request->hwnd = reinterpret_cast<uint32_t>(hwnd);
    request->scriptBytes = static_cast<uint32_t>(script.size()); memcpy(request->script, script.data(), script.size());
    SecureZeroMemory(script.data(), script.size()); script.clear();
    HHOOK hook = SetWindowsHookExW(WH_CALLWNDPROC, hookProc, agent, tid);
    int result;
    if (!hook) result = Output("hook_failed", GetLastError());
    else {
        COPYDATASTRUCT data{ C30_COPYDATA, static_cast<DWORD>((name.size() + 1) * sizeof(wchar_t)), const_cast<wchar_t*>(name.c_str()) };
        DWORD_PTR ignored{};
        SetLastError(ERROR_SUCCESS);
        LRESULT delivered = SendMessageTimeoutW(hwnd, WM_COPYDATA, 0, reinterpret_cast<LPARAM>(&data), SMTO_ABORTIFHUNG | SMTO_BLOCK, 3000, &ignored);
        DWORD error = delivered ? 0 : GetLastError();
        UnhookWindowsHookEx(hook);
        const char* state = !delivered ? (error == ERROR_ACCESS_DENIED ? "access_denied" : error == ERROR_TIMEOUT || error == ERROR_SUCCESS ? "hook_timeout" : "send_failed") : request->state == Queued ? "queued" : request->state == ModuleMissing ? "module_missing" :
            request->state == ExportMissing ? "export_missing" : request->state == ScanLimit ? "scan_limit" : request->state == Pending ? "hook_not_executed" : "no_control";
        result = Output(state, error, request);
    }
    SecureZeroMemory(request, sizeof(*request)); UnmapViewOfFile(request); CloseHandle(mapping); FreeLibrary(agent);
    return result;
}
