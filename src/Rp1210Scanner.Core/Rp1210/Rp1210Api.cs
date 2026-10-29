using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Rp1210Scanner.Core.Rp1210;

/// <summary>RP1210 command numbers used with RP1210_SendCommand (TMC RP1210C).</summary>
public static class Rp1210Commands
{
    public const short SetAllFiltersStatesToPass = 3;
    public const short SetMessageReceive = 18;
    public const short ProtectJ1939Address = 19;
}

/// <summary>
/// Late-bound wrapper over a vendor RP1210 DLL. The DLL name comes from RP1210.ini, so the
/// exports are resolved with LoadLibrary/GetProcAddress instead of static DllImport.
/// Most vendor DLLs are 32-bit, so the app is built x86.
/// </summary>
public sealed class Rp1210Api : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate short ClientConnectFn(IntPtr hwnd, short deviceId, string protocol, int txBufferSize, int rcvBufferSize, short isAppPacketizing);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short ClientDisconnectFn(short clientId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short SendMessageFn(short clientId, byte[] message, short length, short notifyStatusOnTx, short blockOnSend);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short ReadMessageFn(short clientId, byte[] buffer, short bufferSize, short blockOnRead);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short SendCommandFn(short command, short clientId, byte[] buffer, short bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate short GetErrorMsgFn(short errorCode, StringBuilder message);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    private IntPtr _module;
    private readonly ClientConnectFn _connect;
    private readonly ClientDisconnectFn _disconnect;
    private readonly SendMessageFn _send;
    private readonly ReadMessageFn _read;
    private readonly SendCommandFn _command;
    private readonly GetErrorMsgFn? _errorMsg;

    public string DllName { get; }

    public Rp1210Api(string dllName)
    {
        DllName = dllName;
        _module = LoadLibrary(dllName);
        if (_module == IntPtr.Zero)
            throw new DllNotFoundException(
                $"Could not load {dllName} (Win32 error {Marshal.GetLastWin32Error()}). " +
                $"Check the driver is installed and that this app's bitness ({(Environment.Is64BitProcess ? "64" : "32")}-bit) matches the DLL.");

        _connect = Bind<ClientConnectFn>("RP1210_ClientConnect");
        _disconnect = Bind<ClientDisconnectFn>("RP1210_ClientDisconnect");
        _send = Bind<SendMessageFn>("RP1210_SendMessage");
        _read = Bind<ReadMessageFn>("RP1210_ReadMessage");
        _command = Bind<SendCommandFn>("RP1210_SendCommand");
        _errorMsg = TryBind<GetErrorMsgFn>("RP1210_GetErrorMsg");
    }

    private T Bind<T>(string name) where T : Delegate =>
        TryBind<T>(name) ?? throw new EntryPointNotFoundException($"{DllName} does not export {name}.");

    private T? TryBind<T>(string name) where T : Delegate
    {
        var p = GetProcAddress(_module, name);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    /// <summary>Returns a client id (0-127) or throws with the driver's error text.</summary>
    public short ClientConnect(short deviceId, string protocol)
    {
        var result = _connect(IntPtr.Zero, deviceId, protocol, 8192, 16384, 0);
        if (result < 0 || result > 127)
            throw new Rp1210Exception(result, ErrorText(result), $"RP1210_ClientConnect(device {deviceId}, \"{protocol}\")");
        return result;
    }

    public void ClientDisconnect(short clientId) => _disconnect(clientId);

    public short SendMessage(short clientId, byte[] message) =>
        _send(clientId, message, (short)message.Length, 0, 1);

    /// <summary>Non-blocking read. Returns bytes read, 0 when nothing is waiting, negative on error.</summary>
    public short ReadMessage(short clientId, byte[] buffer) =>
        _read(clientId, buffer, (short)buffer.Length, 0);

    public short SendCommand(short command, short clientId, byte[] buffer) =>
        _command(command, clientId, buffer, (short)buffer.Length);

    public string ErrorText(short code)
    {
        if (_errorMsg == null)
            return $"RP1210 error {code}";
        var sb = new StringBuilder(256);
        return _errorMsg(code, sb) == 0 ? $"{sb} ({code})" : $"RP1210 error {code}";
    }

    public void Dispose()
    {
        if (_module != IntPtr.Zero)
        {
            FreeLibrary(_module);
            _module = IntPtr.Zero;
        }
    }
}

public sealed class Rp1210Exception : Exception
{
    public short Code { get; }

    public Rp1210Exception(short code, string text, string call) : base($"{call} failed: {text}") => Code = code;
}
