#pragma once
#include <windows.h>
#include <stdint.h>
constexpr ULONG_PTR C30_COPYDATA = 0x43333053;
constexpr uint32_t C30_MAGIC = 0x31534C43;
constexpr size_t C30_SCRIPT_CAPACITY = 49152;
struct SharedRequest {
    uint32_t magic, size, pid, tid, hwnd, scriptBytes;
    LONG state;
    uint32_t error, candidates, queued, scannedBytes;
    char script[C30_SCRIPT_CAPACITY];
};
enum State : LONG { Pending = 0, Queued = 1, NoControl = 2, ModuleMissing = 3, ExportMissing = 4, Fault = 5, ScanLimit = 6 };
