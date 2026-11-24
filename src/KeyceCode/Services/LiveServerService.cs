using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KeyceCode.Services;

/// <summary>Loopback-only static web server with automatic browser refresh.</summary>
public sealed class LiveServerService : IAsyncDisposable
{
    private const string ReloadScript = "<script>(function(){let v='';setInterval(async()=>{try{const r=await fetch('/__keyce_version',{cache:'no-store'});const n=await r.text();if(v&&v!==n)location.reload();v=n;}catch{}},900)})();</script>";
    private static readonly IReadOnlyDictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8", [".xml"] = "application/xml; charset=utf-8",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".ico"] = "image/x-icon",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".map"] = "application/json"
    };

    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _serverTask;
    private FileSystemWatcher? _watcher;
    private Process? _phpProcess;
    private long _contentVersion;
    private string? _root;

    public string? Url { get; private set; }
    public bool IsRunning => _listener is not null || _phpProcess is { HasExited: false };
    public event EventHandler<string>? OutputReceived;

    public async Task<string> StartAsync(string rootDirectory, string? entryFile = null, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        _root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Le dossier du site n’existe pas.");

        var port = GetAvailablePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _contentVersion = DateTime.UtcNow.Ticks;
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnContentChanged;
        _watcher.Created += OnContentChanged;
        _watcher.Deleted += OnContentChanged;
        _watcher.Renamed += OnContentRenamed;
        Url = $"http://127.0.0.1:{port}/";
        _serverTask = AcceptRequestsAsync(listener, _cancellation.Token);

        if (!string.IsNullOrWhiteSpace(entryFile))
        {
            var relative = Path.GetRelativePath(_root, Path.GetFullPath(entryFile));
            Url += string.Join('/', relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
        }
        return Url;
    }

    public async Task<string> StartPhpAsync(string phpExecutable, string rootDirectory, string? entryFile = null)
    {
        await StopAsync();
        _root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Le dossier du site PHP n’existe pas.");
        var port = GetAvailablePort();
        var startInfo = new ProcessStartInfo
        {
            FileName = phpExecutable,
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add($"127.0.0.1:{port}");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(_root);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PHP n’a pas pu démarrer le serveur local.");
        _phpProcess = process;
        process.OutputDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) OutputReceived?.Invoke(this, args.Data); };
        process.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) OutputReceived?.Invoke(this, args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var ready = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (process.HasExited) break;
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                ready = true;
                break;
            }
            catch (SocketException) { await Task.Delay(100); }
        }
        if (!ready)
        {
            var exitCode = process.HasExited ? process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "délai dépassé";
            await StopAsync();
            throw new InvalidOperationException($"Le serveur PHP n’a pas démarré ({exitCode}). Vérifie l’installation de PHP et sa disponibilité dans le PATH.");
        }
        Url = $"http://127.0.0.1:{port}/";
        if (!string.IsNullOrWhiteSpace(entryFile))
        {
            var relative = Path.GetRelativePath(_root, Path.GetFullPath(entryFile));
            Url += string.Join('/', relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
        }
        return Url;
    }

    public async Task StopAsync()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync();
            _cancellation.Dispose();
            _cancellation = null;
        }
        _listener?.Stop();
        _listener = null;
        if (_serverTask is not null)
        {
            try { await _serverTask; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
            _serverTask = null;
        }
        Url = null;
        if (_phpProcess is { } php)
        {
            try
            {
                if (!php.HasExited) php.Kill(entireProcessTree: true);
                await php.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
            php.Dispose();
            _phpProcess = null;
        }
        _root = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void OnContentChanged(object sender, FileSystemEventArgs e) => Interlocked.Increment(ref _contentVersion);
    private void OnContentRenamed(object sender, RenamedEventArgs e) => Interlocked.Increment(ref _contentVersion);

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task AcceptRequestsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }
            _ = HandleRequestAsync(client, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { await WriteResponseAsync(stream, 400, "Bad Request", "Requête HTTP invalide."u8.ToArray(), "text/plain; charset=utf-8", cancellationToken); return; }
                string line;
                do { line = await reader.ReadLineAsync(cancellationToken) ?? string.Empty; } while (line.Length > 0);
                var headOnly = parts[0].Equals("HEAD", StringComparison.OrdinalIgnoreCase);
                if (!parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase) && !headOnly)
                {
                    await WriteResponseAsync(stream, 405, "Method Not Allowed", "Méthode non prise en charge."u8.ToArray(), "text/plain; charset=utf-8", cancellationToken);
                    return;
                }

                var requestPath = Uri.UnescapeDataString(parts[1].Split('?', 2)[0]);
                if (requestPath.Equals("/__keyce_version", StringComparison.Ordinal))
                {
                    var version = Encoding.ASCII.GetBytes(Interlocked.Read(ref _contentVersion).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await WriteResponseAsync(stream, 200, "OK", version, "text/plain; charset=utf-8", cancellationToken, headOnly);
                    return;
                }

                var root = _root;
                if (root is null) return;
                var relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(root, relative));
                var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 403, "Forbidden", "Accès interdit."u8.ToArray(), "text/plain; charset=utf-8", cancellationToken, headOnly);
                    return;
                }
                if (Directory.Exists(fullPath))
                {
                    var index = new[] { "index.html", "index.htm" }.Select(name => Path.Combine(fullPath, name)).FirstOrDefault(File.Exists);
                    if (index is null) { await WriteResponseAsync(stream, 404, "Not Found", "Aucun fichier index.html dans ce dossier."u8.ToArray(), "text/plain; charset=utf-8", cancellationToken, headOnly); return; }
                    fullPath = index;
                }
                if (!File.Exists(fullPath) || Path.GetExtension(fullPath).Equals(".php", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", "Fichier introuvable."u8.ToArray(), "text/plain; charset=utf-8", cancellationToken, headOnly);
                    return;
                }

                var body = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                var extension = Path.GetExtension(fullPath);
                if (extension is ".html" or ".htm")
                {
                    var html = Encoding.UTF8.GetString(body);
                    var closeBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    html = closeBody >= 0 ? html.Insert(closeBody, ReloadScript) : html + ReloadScript;
                    body = Encoding.UTF8.GetBytes(html);
                }
                var contentType = MimeTypes.TryGetValue(extension, out var mime) ? mime : "application/octet-stream";
                await WriteResponseAsync(stream, 200, "OK", body, contentType, cancellationToken, headOnly);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException or ArgumentException)
            {
                // Client disconnects and files changing during a request are normal for a local preview server.
            }
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, byte[] body, string contentType, CancellationToken cancellationToken, bool headOnly = false)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        if (!headOnly && body.Length > 0) await stream.WriteAsync(body, cancellationToken);
    }
}
