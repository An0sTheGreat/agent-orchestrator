using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentOrchestrator;

// A program in a Windows pseudo console (ConPTY): what it writes comes back as text with its VT colour codes (Output, on a
// background thread), and keys typed in the terminal go to it as text (Write). The terminal panel runs PowerShell in one.
internal sealed class ConPty : IDisposable
{
    private IntPtr _console, _process, _thread;
    private readonly FileStream _input, _output;
    public event Action<string>? Output;
    public event Action? Exited;

    public ConPty(string commandLine, string folder, short cols, short rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) || !CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        var hr = CreatePseudoConsole(new Coord(cols, rows), inRead, outWrite, 0, out _console);
        inRead.Dispose(); // the console holds its own copies
        outWrite.Dispose();
        if (hr != 0)
            throw new Win32Exception(hr);
        _input = new FileStream(inWrite, FileAccess.Write);
        _output = new FileStream(outRead, FileAccess.Read);

        var info = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = 0x100 /* STARTF_USESTDHANDLES: none, so a parent console is not inherited */ } };
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        info.lpAttributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(info.lpAttributeList, 1, 0, ref size) ||
                !UpdateProcThreadAttribute(info.lpAttributeList, 0, (IntPtr)0x20016 /* PSEUDOCONSOLE */, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero) ||
                !CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x80000 /* EXTENDED_STARTUPINFO_PRESENT */ | 0x400 /* UNICODE_ENVIRONMENT */,
                    IntPtr.Zero, folder, ref info, out var pi))
                throw new Win32Exception();
            (_process, _thread) = (pi.hProcess, pi.hThread);
        }
        finally
        {
            DeleteProcThreadAttributeList(info.lpAttributeList);
            Marshal.FreeHGlobal(info.lpAttributeList);
        }
    }

    // Output starts flowing: after the handlers are on, so nothing the program writes first is lost.
    public void Start() => new Thread(Read) { IsBackground = true, Name = "ConPty output" }.Start();

    private void Read()
    {
        var decoder = Encoding.UTF8.GetDecoder(); // keeps a character split across two reads whole
        var bytes = new byte[16384];
        var chars = new char[16384 + 4];
        try
        {
            int n;
            while ((n = _output.Read(bytes, 0, bytes.Length)) > 0)
                if (decoder.GetChars(bytes, 0, n, chars, 0) is var count and > 0)
                    Output?.Invoke(new string(chars, 0, count));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        Exited?.Invoke();
    }

    public void Write(string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public void Resize(short cols, short rows)
    {
        if (_console != IntPtr.Zero && cols > 0 && rows > 0)
            ResizePseudoConsole(_console, new Coord(cols, rows));
    }

    // Closing the console ends the program and its children (the read loop then ends).
    public void Dispose()
    {
        if (_console != IntPtr.Zero)
            ClosePseudoConsole(_console);
        _console = IntPtr.Zero;
        _input.Dispose();
        foreach (var handle in new[] { _process, _thread })
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        (_process, _thread) = (IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit,
        uint flags, IntPtr environment, string? folder, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
