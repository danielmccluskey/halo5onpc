using System.Runtime.InteropServices;
using System.Text;

namespace H5SoloLauncher.Worker;

internal static class Native
{
    internal const uint WaitObject0 = 0, WaitTimeout = 258, WaitFailed = 0xFFFFFFFF;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern int GetPackagePathByFullName(string fullName, ref uint length, StringBuilder? path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern int GetPackageFullName(nint process, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsWow64Process2(nint process, out ushort machine, out ushort native);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] internal static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetModuleHandleEx(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetModuleFileName(nint module, StringBuilder path, int length);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteProcessMemory(nint process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] bytes, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint CreateRemoteThread(nint process, nint attributes, nuint stack, nint start, nint parameter, uint flags, out uint id);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeThread(nint thread, out uint exit);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
}
