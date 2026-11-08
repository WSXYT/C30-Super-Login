#include <windows.h>
#include <stdio.h>
#include <string.h>
// Let MSVC emit a real multiply-inherited vftable with the observed decorated name.
namespace DuiLib {
    class CControlUI { public: virtual void Control() {} };
    class OtherBase { public: virtual void Other() {} };
    class __declspec(dllexport) CCefBrowserUI : public CControlUI, public OtherBase {
    public: void Control() override {} void Other() override {}
    };
}
static DuiLib::CCefBrowserUI tableSource;
struct Fake { void* vtable; HWND hwnd; wchar_t receipt[MAX_PATH]; };
extern "C" void* __cdecl CreateFixture(HWND hwnd, const wchar_t* receipt) {
    auto object = static_cast<Fake*>(VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    object->vtable = *reinterpret_cast<void**>(&tableSource); object->hwnd = hwnd; wcscpy_s(object->receipt, receipt); return object;
}
extern "C" void __fastcall FixtureExecJS(Fake* object, void*, const char* script) {
    FILE* output{}; _wfopen_s(&output, object->receipt, L"wb");
    if (output) {
        DWORD owner; DWORD thread = GetWindowThreadProcessId(object->hwnd, &owner);
        fprintf(output, "{\"agent_loaded\":%s,\"correct_ui_thread\":%s,\"script_bytes\":%u,\"script_marker\":%s}",
            GetModuleHandleW(L"C30Agent.dll") ? "true" : "false", thread == GetCurrentThreadId() ? "true" : "false",
            static_cast<unsigned>(strlen(script)), strstr(script, "C30SuperLogin") ? "true" : "false"); fclose(output);
    }
}
extern "C" bool __fastcall FixtureReady(Fake*, void*) { return true; }
extern "C" HWND __fastcall FixtureHost(Fake* object, void*) { return object->hwnd; }
