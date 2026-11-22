using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace KeyceCode.Services;

/// <summary>
/// Hosts an interactive Windows console process through ConPTY. Output is decoded as UTF-8 and retains
/// virtual-terminal sequences. Callers are responsible for rendering those sequences in a terminal UI.
/// </summary>
public sealed class ConPtySession : IAsyncDisposable
{
    private const int MinimumWindowsBuild = 17763;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint ErrorBrokenPipe = 109;
    private const uint ErrorOperationAborted = 995;
    private const uint WaitObject0 = 0;
    private const uint Infinite = 0xFFFFFFFF;
    private const int QueueCapacity = 128;

    private readonly SafeProcessHandle _processHandle;
    private readonly SafeFileHandle _inputPipe;
    private readonly SafeFileHandle _outputPipe;
    private readonly BlockingCollection<byte[]> _inputQueue = new(QueueCapacity);
    private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _pseudoConsoleClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _startCancellationToken;
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleWriter = true,
        AllowSynchronousContinuations = false
    });
    private CancellationTokenRegistration _cancellationRegistration;
    private IntPtr _pseudoConsole;
    private Task _inputTask = Task.CompletedTask;
    private Task _outputTask = Task.CompletedTask;
    private Task _processTask = Task.CompletedTask;
    private int _stopRequested;
    private int _disposed;
    private int _pseudoConsoleCloseStarted;

    /// <summary>Raised on the output pump thread. Text is UTF-8 decoded; VT escape sequences are preserved.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised for an unexpected native I/O or process-wait failure.</summary>
    public event Action<Exception>? Faulted;

    public int ProcessId { get; }
    /// <summary>Buffered terminal text stream; consume it continuously to avoid unbounded queued output.</summary>
    public ChannelReader<string> Output => _output.Reader;
    public Task<int> Completion => _completion.Task;
    public int? ExitCode => _completion.Task.IsCompletedSuccessfully ? _completion.Task.Result : null;

    private ConPtySession(SafeProcessHandle processHandle, SafeFileHandle inputPipe, SafeFileHandle outputPipe,
        IntPtr pseudoConsole, int processId, CancellationToken cancellationToken)
    {
        _processHandle = processHandle;
        _inputPipe = inputPipe;
        _outputPipe = outputPipe;
        _pseudoConsole = pseudoConsole;
        ProcessId = processId;
        _startCancellationToken = cancellationToken;
    }

    /// <summary>
    /// Starts an interactive console application. Executable path, argument tokens and working directory
    /// are supplied separately; arguments are quoted with Windows command-line escaping rules.
    /// </summary>
    public static Task<ConPtySession> StartAsync(string executablePath, IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null, int columns = 120, int rows = 30, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild))
            throw new PlatformNotSupportedException("ConPTY requiert Windows 10 version 1809 (build 17763) ou Windows Server 2019.");
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (columns is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));

        var appPath = Path.IsPathRooted(executablePath) ? Path.GetFullPath(executablePath) : executablePath;
        if (Path.IsPathRooted(appPath) && !File.Exists(appPath)) throw new FileNotFoundException("Console executable not found.", appPath);
        var currentDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        if (!Directory.Exists(currentDirectory)) throw new DirectoryNotFoundException(currentDirectory);
        var commandLine = BuildCommandLine(appPath, arguments);

        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafeProcessHandle? processHandle = null;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        bool attributeListInitialized = false;
        PROCESS_INFORMATION processInfo = default;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0)) ThrowLastWin32Error("CreatePipe(input)");
            if (!CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) ThrowLastWin32Error("CreatePipe(output)");
            if (!SetHandleInformation(inputWrite!, HandleFlagInherit, 0)) ThrowLastWin32Error("SetHandleInformation(input)");
            if (!SetHandleInformation(outputRead!, HandleFlagInherit, 0)) ThrowLastWin32Error("SetHandleInformation(output)");

            var size = new COORD((short)columns, (short)rows);
            var hresult = CreatePseudoConsole(size, inputRead!, outputWrite!, 0, out pseudoConsole);
            if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);

            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            if (attributeListSize == 0) ThrowLastWin32Error("InitializeProcThreadAttributeList(size)");
            attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                ThrowLastWin32Error("InitializeProcThreadAttributeList");
            attributeListInitialized = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole,
                    pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                ThrowLastWin32Error("UpdateProcThreadAttribute(PSEUDOCONSOLE)");

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>(), dwFlags = StartfUseStdHandles },
                lpAttributeList = attributeList
            };
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, currentDirectory, ref startupInfo, out processInfo))
                ThrowLastWin32Error("CreateProcessW");

            _ = CloseHandle(processInfo.hThread);
            processInfo.hThread = IntPtr.Zero;
            processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
            processInfo.hProcess = IntPtr.Zero;

            var session = new ConPtySession(processHandle, inputWrite, outputRead, pseudoConsole,
                processInfo.dwProcessId, cancellationToken);
            processHandle = null;
            inputWrite = null;
            outputRead = null;
            pseudoConsole = IntPtr.Zero;
            session.StartPumps();
            return Task.FromResult(session);
        }
        finally
        {
            if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (processInfo.hThread != IntPtr.Zero) _ = CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero) _ = CloseHandle(processInfo.hProcess);
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
            processHandle?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
        }
    }

    /// <summary>Queues UTF-8 text or VT key sequences for the hosted console.</summary>
    public Task SendInputAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ThrowIfUnavailable();
        var bytes = Encoding.UTF8.GetBytes(text);
        return Task.Run(() => _inputQueue.Add(bytes, cancellationToken), cancellationToken);
    }

    /// <summary>Sends the terminal ETX byte; the console decides how the active process handles Ctrl-C.</summary>
    public Task SendCtrlCAsync(CancellationToken cancellationToken = default) => SendInputAsync("\u0003", cancellationToken);

    /// <summary>Resizes the pseudoconsole in character cells.</summary>
    public void Resize(int columns, int rows)
    {
        ThrowIfUnavailable();
        if (columns is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
        var hresult = ResizePseudoConsole(Volatile.Read(ref _pseudoConsole), new COORD((short)columns, (short)rows));
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    /// <summary>Closes ConPTY, which terminates the hosted console process tree, then drains both I/O pumps.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
        {
            try { _inputQueue.CompleteAdding(); } catch (InvalidOperationException) { }
        }
        var closeTask = ClosePseudoConsoleOnceAsync();

        try
        {
            await _completion.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!_processHandle.IsInvalid && !_processHandle.IsClosed)
                _ = TerminateProcess(_processHandle, 0xC000013A);
            await _completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception) { /* ProcessLoop reported the native error; the remaining pumps still need draining. */ }

        await closeTask.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Task.WhenAll(_inputTask, _outputTask, _processTask).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _cancellationRegistration.Dispose();
            _inputPipe.Dispose();
            _outputPipe.Dispose();
            _processHandle.Dispose();
            _inputQueue.Dispose();
        }
    }

    private void StartPumps()
    {
        _outputTask = Task.Factory.StartNew(OutputLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _inputTask = Task.Factory.StartNew(InputLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _processTask = Task.Factory.StartNew(ProcessLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _cancellationRegistration = _startCancellationToken.Register(static state => _ = ((ConPtySession)state!).StopAsync(), this);
    }

    private void OutputLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            while (true)
            {
                if (!ReadFile(_outputPipe, bytes, (uint)bytes.Length, out var read, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is (int)ErrorBrokenPipe or (int)ErrorOperationAborted) break;
                    throw new Win32Exception(error, "ReadFile(ConPTY output)");
                }
                if (read == 0) break;
                var count = decoder.GetChars(bytes, 0, (int)read, chars, 0, flush: false);
                if (count > 0) RaiseOutput(new string(chars, 0, count));
            }
            var finalCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
            if (finalCount > 0) RaiseOutput(new string(chars, 0, finalCount));
        }
        catch (Exception ex) { RaiseFault(ex); }
        finally { _output.Writer.TryComplete(); }
    }

    private void InputLoop()
    {
        try
        {
            foreach (var bytes in _inputQueue.GetConsumingEnumerable())
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var remaining = offset == 0 ? bytes : bytes[offset..];
                    if (!WriteFile(_inputPipe, remaining, (uint)remaining.Length, out var written, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error is (int)ErrorBrokenPipe or (int)ErrorOperationAborted) return;
                        throw new Win32Exception(error, "WriteFile(ConPTY input)");
                    }
                    if (written == 0) return;
                    offset += (int)written;
                }
            }
        }
        catch (Exception ex) { RaiseFault(ex); }
    }

    private void ProcessLoop()
    {
        try
        {
            var result = WaitForSingleObject(_processHandle, Infinite);
            if (result != WaitObject0) throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject(ConPTY process)");
            if (!GetExitCodeProcess(_processHandle, out var exitCode)) ThrowLastWin32Error("GetExitCodeProcess");
            _completion.TrySetResult(unchecked((int)exitCode));
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
            RaiseFault(ex);
        }
        finally
        {
            try { _inputQueue.CompleteAdding(); } catch (InvalidOperationException) { }
            ClosePseudoConsoleOnceOnProcessThread();
        }
    }

    private Task ClosePseudoConsoleOnceAsync()
    {
        if (Interlocked.CompareExchange(ref _pseudoConsoleCloseStarted, 1, 0) == 0)
        {
            var pseudoConsole = Interlocked.Exchange(ref _pseudoConsole, IntPtr.Zero);
            if (pseudoConsole == IntPtr.Zero) _pseudoConsoleClosed.TrySetResult(true);
            else _ = Task.Factory.StartNew(() =>
            {
                try { ClosePseudoConsole(pseudoConsole); }
                finally { _pseudoConsoleClosed.TrySetResult(true); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        return _pseudoConsoleClosed.Task;
    }

    private void ClosePseudoConsoleOnceOnProcessThread()
    {
        if (Interlocked.CompareExchange(ref _pseudoConsoleCloseStarted, 1, 0) != 0) return;
        var pseudoConsole = Interlocked.Exchange(ref _pseudoConsole, IntPtr.Zero);
        try { if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole); }
        finally { _pseudoConsoleClosed.TrySetResult(true); }
    }

    private void RaiseOutput(string output)
    {
        _output.Writer.TryWrite(output);
        try { OutputReceived?.Invoke(output); } catch { /* A view callback must not stop the pipe pump. */ }
    }

    private void RaiseFault(Exception exception)
    {
        try { Faulted?.Invoke(exception); } catch { }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _stopRequested) != 0 || Volatile.Read(ref _pseudoConsole) == IntPtr.Zero)
            throw new InvalidOperationException("La session ConPTY est arrêtée.");
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string>? arguments)
    {
        var builder = new StringBuilder(QuoteArgument(executable));
        if (arguments is not null)
            foreach (var argument in arguments)
            {
                ArgumentNullException.ThrowIfNull(argument);
                builder.Append(' ').Append(QuoteArgument(argument));
            }
        return builder.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var result = new StringBuilder(argument.Length + 2).Append('"');
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static void ThrowLastWin32Error(string operation) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct COORD(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, nuint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
