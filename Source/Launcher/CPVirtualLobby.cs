kn5}ŒﬁuŒûs«ﬂkMÙ˜u›ª}æ\”∑tÒ≠∂// COPYRIGHT 2026 by CP Virtual contributors.
// Browser-first launcher for CP Virtual.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Launcher
{
    internal enum CPVirtualRole
    {
        Classic,
        Server,
        Driver,
        Dispatcher
    }

    internal sealed class CPVirtualLaunchSelection
    {
        public CPVirtualRole Role;
        public string Host;
        public string Service;
        public string Post;
    }

    /// <summary>
    /// Serves the initial CP Virtual page before RunActivity exists. A small
    /// loopback TCP listener is used instead of HttpListener so no Windows URL
    /// reservation or administrator permission is required.
    /// </summary>
    internal sealed class CPVirtualLobby : IDisposable
    {
        private const int Port = 2150;
        private readonly string programPath;
        private TcpListener listener;

        public CPVirtualLobby(string programPath)
        {
            this.programPath = programPath;
        }

        public CPVirtualLaunchSelection Run()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
            }
            catch (SocketException)
            {
                // A running simulator already owns the CP Virtual endpoint.
                OpenBrowser("http://localhost:2150/CPVirtual/");
                return null;
            }

            OpenBrowser("http://localhost:2150/CPVirtual/");
            while (true)
            {
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true))
                {
                    var requestLine = reader.ReadLine();
                    if (String.IsNullOrWhiteSpace(requestLine))
                        continue;

                    string ignored;
                    while (!String.IsNullOrEmpty(ignored = reader.ReadLine())) { }

                    var parts = requestLine.Split(' ');
                    var requestTarget = parts.Length > 1 ? parts[1] : "/";
                    var uri = new Uri("http://localhost:" + Port + requestTarget);

                    if (uri.AbsolutePath.Equals("/select", StringComparison.OrdinalIgnoreCase))
                    {
                        var selection = ReadSelection(uri.Query);
                        if (selection == null)
                        {
                            WriteText(stream, "400 Bad Request", "Escolha inv√°lida.");
                            continue;
                        }

                        if (selection.Role == CPVirtualRole.Dispatcher)
                        {
                            SaveSelection(selection);
                            var host = String.IsNullOrWhiteSpace(selection.Host) ? "localhost" : selection.Host;
                            WriteRedirect(stream, "http://" + host + ":2150/CPVirtual/posto.html");
                            return selection;
                        }

                        WriteHtml(stream, ConfirmationPage(selection));
                        return selection;
                    }

                    if (uri.AbsolutePath.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteText(stream, "404 Not Found", String.Empty);
                        continue;
                    }

                    WriteHtml(stream, LoadLobbyPage());
                }
            }
        }

        private string LoadLobbyPage()
        {
            var file = Path.Combine(programPath, "Content", "Web", "CPVirtual", "lobby.html");
            if (File.Exists(file))
                return File.ReadAllText(file, Encoding.UTF8);
            return "<html><body><h1>CP Virtual</h1><p>Falta Content/Web/CPVirtual/lobby.html.</p>" +
                "<p><a href='/select?role=classic'>Abrir menu cl√°ssico</a></p></body></html>";
        }

        private static CPVirtualLaunchSelection ReadSelection(string query)
        {
            var values = ParseQuery(query);
            string roleValue;
            if (!values.TryGetValue("role", out roleValue))
                return null;

            CPVirtualRole role;
            if (!Enum.TryParse(roleValue, true, out role))
                return null;

            string host;
            string service;
            string post;
            values.TryGetValue("host", out host);
            values.TryGetValue("service", out service);
            values.TryGetValue("post", out post);
            return new CPVirtualLaunchSelection
            {
                Role = role,
                Host = CleanHost(host),
                Service = (service ?? String.Empty).Trim(),
                Post = (post ?? String.Empty).Trim()
            };
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in (query ?? String.Empty).TrimStart('?').Split('&'))
            {
                if (String.IsNullOrWhiteSpace(pair))
                    continue;
                var fields = pair.Split(new[] { '=' }, 2);
                output[Uri.UnescapeDataString(fields[0].Replace('+', ' '))] =
                    fields.Length > 1 ? Uri.UnescapeDataString(fields[1].Replace('+', ' ')) : String.Empty;
            }
            return output;
        }

        private static string CleanHost(string host)
        {
            host = (host ?? String.Empty).Trim();
            Uri uri;
            if (Uri.TryCreate(host, UriKind.Absolute, out uri))
                host = uri.Host;
            var separator = host.LastIndexOf(':');
            if (separator > 0 && host.IndexOf(':') == separator)
                host = host.Substring(0, separator);
            return host;
        }

        private static string ConfirmationPage(CPVirtualLaunchSelection selection)
        {
            return "<!doctype html><meta charset='utf-8'><title>CP Virtual</title>" +
                "<style>body{background:#091116;color:#dce7ed;font:16px Segoe UI,sans-serif;padding:3rem}" +
                ".card{max-width:620px;margin:auto;background:#172229;border:1px solid #40515b;padding:2rem;border-radius:12px}" +
                "b{color:#77e4a5}</style><div class='card'><h1>CP Virtual</h1><p><b>Escolha registada.</b></p>" +
                "<p>O Open Rails vai preparar o modo " + WebUtility.HtmlEncode(selection.Role.ToString()) +
                ". A p√°gina liga-se automaticamente ao posto quando o motor estiver pronto.</p></div>" +
                "<script>setInterval(function(){fetch('/CPVirtual/',{cache:'no-store'}).then(function(r){if(r.ok)location.replace('/CPVirtual/');}).catch(function(){});},1000);</script>";
        }

        public static void SaveSelection(CPVirtualLaunchSelection selection)
        {
            if (selection == null)
                return;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Open Rails");
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "CPVirtual.launch"), new[]
            {
                "role=" + selection.Role.ToString().ToLowerInvariant(),
                "host=" + (selection.Host ?? String.Empty),
                "service=" + (selection.Service ?? String.Empty),
                "post=" + (selection.Post ?? String.Empty)
            });
        }

        private static void OpenBrowser(string address)
        {
            Process.Start(new ProcessStartInfo { FileName = address, UseShellExecute = true });
        }

        private static void WriteHtml(Stream stream, string html)
        {
            WriteResponse(stream, "200 OK", "text/html; charset=utf-8", html);
        }

        private static void WriteText(Stream stream, string status, string text)
        {
            WriteResponse(stream, status, "text/plain; charset=utf-8", text);
        }

        private static void WriteRedirect(Stream stream, string address)
        {
            var bytes = Encoding.UTF8.GetBytes("A abrir o posto CP Virtual...");
            var header = "HTTP/1.1 302 Found\r\nLocation: " + address + "\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteResponse(Stream stream, string status, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? String.Empty);
            var header = "HTTP/1.1 " + status + "\r\nContent-Type: " + contentType + "\r\nCache-Control: no-store\r\n" +
                "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            if (listener != null)
                listener.Stop();
        }
    }
}
