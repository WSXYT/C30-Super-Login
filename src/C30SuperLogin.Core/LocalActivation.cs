using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace C30SuperLogin.Core;

internal static class LocalActivation
{
    private const string TeachCoreSha256 = "C52038C228176991684BD5F1E8A4F9BB9F3BA5D8A496C5C18F5E6D9FA0B5E153";
    private const uint SnapshotModule = 0x00000008;
    private const uint SnapshotModule32 = 0x00000010;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint PageExecuteReadWrite = 0x40;
    private static readonly IntPtr InvalidHandle = new(-1);

    private sealed record Patch(int Rva, byte[] Original, byte[] Active);
    private sealed record ModulePatch(string Module, string Sha256, Patch[] Patches);
    private static readonly Patch[] TeachCorePatches =
    [
        new(0x42FB5, [0x8A, 0x85, 0x52, 0xFF, 0xFF, 0xFF], [0xB0, 0x01, 0x90, 0x90, 0x90, 0x90]),
        new(0x42FEE, [0x8A, 0x85, 0x48, 0xFF, 0xFF, 0xFF], [0xB0, 0x01, 0x90, 0x90, 0x90, 0x90])
    ];
    private const string UiBaseSha256 = "D2A1E5AFA8BFBF78773E1F6E4760D603D474C79BDD94B9BE4138B91060C809CF";
    private static readonly Patch[] UiBasePatches =
    [
        // DT_CheckRegister: always take the registered branch that writes is_registed=1.
        new(0xD42E0, [0x75, 0x09], [0x90, 0x90]),
        // DT_IsRegisted: return true for any caller that reads the exported helper.
        new(0xD4580, [0x55, 0x8B, 0xEC, 0xA0, 0x24], [0x55, 0x8B, 0xEC, 0xB0, 0x01])
    ];
    private static readonly ModulePatch[] ModulePatches =
    [
        new("teach_core.dll", TeachCoreSha256, TeachCorePatches),
        new("ui_base.dll", UiBaseSha256, UiBasePatches)
    ];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry
    {
        public uint Size;
        public uint ModuleId;
        public uint ProcessId;
        public uint GlobalUsage;
        public uint ProcessUsage;
        public IntPtr BaseAddress;
        public uint BaseSize;
        public IntPtr Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public static void Apply(TargetProcess target)
    {
        if (target.Version != "1.3.1610.0") throw new NotSupportedException("unsupported_version");
        foreach (var module in ModulePatches)
        {
            string path = Path.Combine(target.Directory, module.Module);
            if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != module.Sha256)
                throw new NotSupportedException("unsupported_module");
        }

        IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, (uint)target.Pid);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
        try
        {
            foreach (var module in ModulePatches)
            {
                IntPtr baseAddress = FindModule((uint)target.Pid, module.Module);
                var state = module.Patches.Select(patch => (Patch: patch, Address: IntPtr.Add(baseAddress, patch.Rva))).ToArray();
                foreach (var item in state)
                {
                    byte[] before = new byte[item.Patch.Original.Length];
                    IntPtr address = item.Address;
                    if (!ReadProcessMemory(process, address, before, before.Length, out var read) || read.ToInt64() != before.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadProcessMemory");
                    if (!before.SequenceEqual(item.Patch.Original) && !before.SequenceEqual(item.Patch.Active))
                        throw new InvalidDataException($"unexpected_patch_bytes_{module.Module}_0x{item.Patch.Rva:X}");
                }
                foreach (var item in state)
                {
                    if (item.Patch.Active.Length == 0) continue;
                    if (!VirtualProtectEx(process, item.Address, (UIntPtr)item.Patch.Active.Length, PageExecuteReadWrite, out uint oldProtection))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtectEx");
                    try
                    {
                        if (!WriteProcessMemory(process, item.Address, item.Patch.Active, item.Patch.Active.Length, out var written) || written.ToInt64() != item.Patch.Active.Length)
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory");
                        if (!FlushInstructionCache(process, item.Address, (UIntPtr)item.Patch.Active.Length))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "FlushInstructionCache");
                    }
                    finally { VirtualProtectEx(process, item.Address, (UIntPtr)item.Patch.Active.Length, oldProtection, out _); }

                    byte[] after = new byte[item.Patch.Active.Length];
                    if (!ReadProcessMemory(process, item.Address, after, after.Length, out var read2) || !after.SequenceEqual(item.Patch.Active))
                        throw new IOException($"patch_verify_failed_{module.Module}_0x{item.Patch.Rva:X}");
                }
            }
        }
        finally { CloseHandle(process); }
    }

    private static IntPtr FindModule(uint pid, string name)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotModule | SnapshotModule32, pid);
        if (snapshot == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot");
        try
        {
            var entry = new ModuleEntry { Size = (uint)Marshal.SizeOf<ModuleEntry>() };
            if (!Module32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Module32FirstW");
            do
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry.BaseAddress;
                entry.Size = (uint)Marshal.SizeOf<ModuleEntry>();
            } while (Module32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        throw new FileNotFoundException("module_not_loaded", name);
    }
}
