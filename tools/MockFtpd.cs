// Shared by tools/SelfTest (Windows) and tools/XTest (macOS / Linux).
/// <summary>
/// A stand-in for a console running Checkpoint's FTP server: serves a folder as the SD card root with ftpd's exact replies
/// ("220 Hello!", "230 OK" without a password, "227 Entering Passive Mode (h,h,h,h,p,p).", "150 Ready", "226 OK",
/// MLSD facts "Type=dir;Size=..;Modify=..; name"). Records every command so the test can prove nothing was written.
/// </summary>
sealed class MockFtpd : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _l = new(System.Net.IPAddress.Loopback, 0);
    private readonly string _root;
    public int Port { get; }
    public System.Collections.Concurrent.ConcurrentQueue<string> Commands { get; } = new();

    public MockFtpd(string root)
    {
        _root = root;
        _l.Start();
        Port = ((System.Net.IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => Session(c));
            }
        });
    }

    private void Session(System.Net.Sockets.TcpClient c)
    {
        using var _ = c;
        var s = c.GetStream();
        var r = new StreamReader(s, new System.Text.UTF8Encoding(false));
        void Send(string line) { var b = System.Text.Encoding.UTF8.GetBytes(line + "\r\n"); s.Write(b, 0, b.Length); }
        System.Net.Sockets.TcpListener? pasv = null;
        try
        {
            Send("220 Hello!");
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var sp = line.IndexOf(' ');
                var verb = (sp > 0 ? line[..sp] : line).ToUpperInvariant();
                var arg = sp > 0 ? line[(sp + 1)..] : "";
                Commands.Enqueue(verb);
                var local = Path.Combine(_root, arg.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                switch (verb)
                {
                    case "USER": Send("230 OK"); break;
                    case "OPTS": Send("200 OK"); break;
                    case "TYPE": Send("200 OK"); break;
                    case "QUIT": Send("221 Disconnecting"); return;
                    case "PASV":
                        pasv = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                        pasv.Start();
                        var p = ((System.Net.IPEndPoint)pasv.LocalEndpoint).Port;
                        Send($"227 Entering Passive Mode (127,0,0,1,{p >> 8},{p & 0xFF}).");
                        break;
                    case "MLSD" when Directory.Exists(local) && pasv != null:
                    {
                        using var d = pasv.AcceptTcpClient(); pasv.Stop(); pasv = null;
                        Send("150 Ready");
                        var sb = new System.Text.StringBuilder();
                        foreach (var e in new DirectoryInfo(local).EnumerateFileSystemInfos())
                            sb.Append($"Type={(e is DirectoryInfo ? "dir" : "file")};Size={(e is FileInfo f ? f.Length : 0)};Modify={e.LastWriteTimeUtc:yyyyMMddHHmmss}; {e.Name}\r\n");
                        var b = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                        d.GetStream().Write(b, 0, b.Length);
                        d.Close();
                        Send("226 OK");
                        break;
                    }
                    case "RETR" when File.Exists(local) && pasv != null:
                    {
                        using var d = pasv.AcceptTcpClient(); pasv.Stop(); pasv = null;
                        Send("150 Ready");
                        using (var fs = File.OpenRead(local)) fs.CopyTo(d.GetStream());
                        d.Close();
                        Send("226 OK");
                        break;
                    }
                    case "MLSD": case "RETR": pasv?.Stop(); pasv = null; Send("550 No such file or directory"); break;
                    default: Send("502 Command not implemented"); break;
                }
            }
        }
        catch { /* client went away */ }
        finally { pasv?.Stop(); }
    }

    public void Dispose() => _l.Stop();
}
