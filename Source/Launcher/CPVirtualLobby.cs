// COPYRIGHT 2026 by CP Virtual contributors.
// Browser-first launcher for CP Virtual.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ORTS.Menu;
using ORTS.Settings;

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
        public string OperatorName;
        public string Service;
        public string Post;
        public string TimetableFile;
        public string Timetable;
        public string SeedTrain;
        public int Day;
        public int Season;
        public int Weather;
        public bool RealisticVisuals;
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
        private readonly Dictionary<string, CPVirtualLaunchSelection> hostProfiles = new Dictionary<string, CPVirtualLaunchSelection>();
        private TcpListener listener;
        private string selectionError;

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

                    if (uri.AbsolutePath.Equals("/driver", StringComparison.OrdinalIgnoreCase))
                    {
                        var values = ParseQuery(uri.Query);
                        string host;
                        string operatorName;
                        string visual;
                        values.TryGetValue("host", out host);
                        values.TryGetValue("name", out operatorName);
                        values.TryGetValue("visual", out visual);
                        host = CleanHost(host);
                        operatorName = NormalizeOperatorName(operatorName);
                        if (String.IsNullOrEmpty(host) || String.IsNullOrEmpty(operatorName))
                        {
                            WriteText(stream, "400 Bad Request", "Indica o servidor e um nome válido de 4 a 10 caracteres.");
                            continue;
                        }
                        WriteHtml(stream, DriverRadioPage(host, operatorName, visual, String.Empty));
                        continue;
                    }

                    if (uri.AbsolutePath.Equals("/select", StringComparison.OrdinalIgnoreCase))
                    {
                        var selection = ReadSelection(uri.Query);
                        if (selection == null)
                        {
                            var values = ParseQuery(uri.Query);
                            string roleValue;
                            values.TryGetValue("role", out roleValue);
                            if (String.Equals(roleValue, "driver", StringComparison.OrdinalIgnoreCase))
                            {
                                string host;
                                string operatorName;
                                string visual;
                                values.TryGetValue("host", out host);
                                values.TryGetValue("name", out operatorName);
                                values.TryGetValue("visual", out visual);
                                WriteHtml(stream, DriverRadioPage(CleanHost(host), NormalizeOperatorName(operatorName), visual, selectionError ?? "Serviço inválido."));
                            }
                            else
                                WriteText(stream, "400 Bad Request", selectionError ?? "Escolha inválida.");
                            continue;
                        }

                        if (selection.Role == CPVirtualRole.Dispatcher)
                        {
                            SaveSelection(selection);
                            var host = String.IsNullOrWhiteSpace(selection.Host) ? "localhost" : selection.Host;
                            WriteRedirect(stream, "http://" + host + ":2150/CPVirtual/?role=dispatcher&name=" + Uri.EscapeDataString(selection.OperatorName ?? String.Empty));
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
            var file = System.IO.Path.Combine(programPath, "Content", "Web", "CPVirtual", "lobby.html");
            if (File.Exists(file))
                return File.ReadAllText(file, Encoding.UTF8).Replace("{{CPV_HOST_PROFILES}}", BuildHostProfiles());
            return "<html><body><h1>CP Virtual</h1><p>Falta Content/Web/CPVirtual/lobby.html.</p>" +
                "<p><a href='/select?role=classic'>Abrir menu clássico</a></p></body></html>";
        }

        private CPVirtualLaunchSelection ReadSelection(string query)
        {
            selectionError = null;
            var values = ParseQuery(query);
            string roleValue;
            if (!values.TryGetValue("role", out roleValue))
                return null;

            CPVirtualRole role;
            if (!Enum.TryParse(roleValue, true, out role))
                return null;

            string visual;
            values.TryGetValue("visual", out visual);

            if (role == CPVirtualRole.Server)
            {
                string profileKey;
                CPVirtualLaunchSelection profile;
                if (!values.TryGetValue("profile", out profileKey) || !hostProfiles.TryGetValue(profileKey, out profile))
                    return null;
                profile.RealisticVisuals = String.Equals(visual, "realistic", StringComparison.OrdinalIgnoreCase);
                return profile;
            }

            string host;
            string operatorName;
            string service;
            string post;
            values.TryGetValue("host", out host);
            values.TryGetValue("name", out operatorName);
            values.TryGetValue("service", out service);
            values.TryGetValue("post", out post);
            values.TryGetValue("visual", out visual);
            operatorName = NormalizeOperatorName(operatorName);
            if ((role == CPVirtualRole.Driver || role == CPVirtualRole.Dispatcher) && String.IsNullOrEmpty(operatorName))
            {
                selectionError = "Indica um nome ou indicativo válido.";
                return null;
            }
            if (role == CPVirtualRole.Driver)
            {
                var driverService = FindDriverService(service);
                if (driverService == null)
                {
                    selectionError = "O serviço " + WebUtility.HtmlEncode(service) + " não existe nos horários instalados neste computador.";
                    return null;
                }

                string availabilityError;
                if (!ServiceIsAvailable(CleanHost(host), service, out availabilityError))
                {
                    selectionError = availabilityError;
                    return null;
                }

                driverService.Role = role;
                driverService.Host = CleanHost(host);
                driverService.OperatorName = operatorName;
                driverService.Service = (service ?? String.Empty).Trim();
                driverService.RealisticVisuals = String.Equals(visual, "realistic", StringComparison.OrdinalIgnoreCase);
                return driverService;
            }

            return new CPVirtualLaunchSelection
            {
                Role = role,
                Host = CleanHost(host),
                OperatorName = operatorName,
                Service = (service ?? String.Empty).Trim(),
                Post = (post ?? String.Empty).Trim(),
                RealisticVisuals = String.Equals(visual, "realistic", StringComparison.OrdinalIgnoreCase)
            };
        }

        private static bool ServiceIsAvailable(string host, string service, out string error)
        {
            error = String.Empty;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                using (var response = client.GetAsync("http://" + host + ":2150/API/CPV/STATE").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        error = "O servidor CP Virtual não respondeu. Confirma o IP e a porta 2150.";
                        return false;
                    }
                    var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    using (var document = JsonDocument.Parse(json))
                    {
                        JsonElement trains;
                        if (!document.RootElement.TryGetProperty("Trains", out trains))
                        {
                            error = "O servidor não devolveu a lista de serviços.";
                            return false;
                        }
                        foreach (var train in trains.EnumerateArray())
                        {
                            var number = train.TryGetProperty("ServiceNumber", out var serviceNumber) ? serviceNumber.GetInt32() : train.GetProperty("Number").GetInt32();
                            var name = train.TryGetProperty("Name", out var trainName) ? trainName.GetString() : String.Empty;
                            if (!String.Equals(number.ToString(), service, StringComparison.OrdinalIgnoreCase) && !String.Equals(name, service, StringComparison.OrdinalIgnoreCase))
                                continue;
                            var occupied = train.TryGetProperty("Occupied", out var occupiedValue) && occupiedValue.GetBoolean();
                            if (occupied)
                            {
                                var driver = train.TryGetProperty("DriverName", out var driverName) ? driverName.GetString() : String.Empty;
                                error = "Serviço ocupado" + (String.IsNullOrWhiteSpace(driver) ? ". Escolhe outro serviço." : " por " + WebUtility.HtmlEncode(driver) + ". Escolhe outro serviço.");
                                return false;
                            }
                            return true;
                        }
                    }
                }
                error = "O serviço " + WebUtility.HtmlEncode(service) + " não está ativo neste servidor.";
                return false;
            }
            catch (Exception exception)
            {
                Trace.WriteLine("CP Virtual service availability: " + exception);
                error = "Não foi possível contactar o servidor em " + WebUtility.HtmlEncode(host) + ":2150.";
                return false;
            }
        }

        private static string NormalizeOperatorName(string value)
        {
            value = Regex.Replace((value ?? String.Empty).Trim(), "[^A-Za-z0-9_]", String.Empty);
            if (String.IsNullOrEmpty(value))
                return String.Empty;
            if (Char.IsDigit(value[0]))
                value = "M" + value;
            if (value.Length > 10)
                value = value.Substring(0, 10);
            while (value.Length < 4)
                value += "_";
            return value;
        }

        private static string DriverRadioPage(string host, string operatorName, string visual, string error)
        {
            return "<!doctype html><html lang='pt'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>" +
                "<title>CP Virtual · Rádio Solo–Comboio</title><style>body{margin:0;background:#081014;color:#edf2f4;font:15px Segoe UI,Arial}header{padding:16px 24px;background:#182126;border-bottom:3px solid #e12830}header b{color:#e12830;font-size:23px;font-style:italic}.radio{max-width:720px;margin:45px auto;background:#748188;border:5px solid #111;border-radius:28px;padding:25px;color:#101619;box-shadow:0 15px 40px #0008}.screen{background:#365c4d;border:5px solid #0a1012;border-radius:8px;padding:18px;color:#78eca0;font:20px Consolas;margin-bottom:20px}.keys{background:#657278;border:4px solid #182126;border-radius:22px;padding:22px}label{font-weight:800}input{box-sizing:border-box;width:100%;margin:8px 0 15px;padding:13px;background:#10191d;color:white;border:1px solid #39474e;border-radius:5px;font-size:18px}button{width:100%;padding:13px;border:2px solid #4f4b38;border-radius:7px;background:#e7c55d;font-weight:900;cursor:pointer}.error{margin:0 0 15px;padding:10px;background:#4b171b;color:#ffc2c5;border-radius:5px}.hint{font-size:12px;color:#dfe7e9;margin-top:12px}</style>" +
                "<header><b>CP</b> VIRTUAL · RÁDIO SOLO–COMBOIO</header><main class='radio'><div class='screen'>SERVIDOR " + WebUtility.HtmlEncode(host) + "<br>MAQUINISTA " + WebUtility.HtmlEncode(operatorName) + "<br>INTRODUZ O SERVIÇO</div><div class='keys'>" +
                (String.IsNullOrEmpty(error) ? String.Empty : "<div class='error'>" + error + "</div>") +
                "<form action='/select' method='get'><input type='hidden' name='role' value='driver'><input type='hidden' name='host' value='" + WebUtility.HtmlEncode(host) + "'><input type='hidden' name='name' value='" + WebUtility.HtmlEncode(operatorName) + "'><input type='hidden' name='visual' value='" + WebUtility.HtmlEncode(visual) + "'><label>CÓDIGO DO SERVIÇO</label><input name='service' required autofocus pattern='[A-Za-z0-9_-]+' placeholder='Ex.: 4333'><button type='submit'>CONFIRMAR E ABRIR O JOGO</button></form><div class='hint'>O servidor confirma se o serviço existe e está livre antes de iniciar o Open Rails.</div></div></main></html>";
        }

        /// <summary>
        /// High-quality visual profile intended for modern GPUs. It changes
        /// only local graphics settings; route weather remains authoritative.
        /// It is deliberately opt-in from the CP Virtual lobby.
        /// </summary>
        public static void ApplyRealisticVisualProfile(CPVirtualLaunchSelection selection)
        {
            if (selection == null || !selection.RealisticVisuals)
                return;

            var settings = new UserSettings(new string[0]);
            settings.DynamicShadows = true;
            settings.ShadowAllShapes = true;
            settings.ShadowMapBlur = true;
            settings.ShadowMapCount = 4;
            settings.ShadowMapResolution = 2048;
            settings.ShadowMapDistance = 5000;
            settings.ViewingDistance = Math.Max(settings.ViewingDistance, 8000);
            settings.DistantMountains = true;
            settings.DistantMountainsViewingDistance = Math.Max(settings.DistantMountainsViewingDistance, 40000);
            settings.LODViewingExtension = true;
            settings.WorldObjectDensity = 99;
            settings.DayAmbientLight = 20;
            settings.AntiAliasing = (int)UserSettings.AntiAliasingMethod.MSAA8x;
            settings.SignalLightGlow = true;
            settings.Save();
        }

        private string BuildHostProfiles()
        {
            hostProfiles.Clear();
            var html = new StringBuilder();
            var settings = new UserSettings(new string[0]);
            var key = 0;

            try
            {
                foreach (var folder in Folder.GetFolders(settings))
                foreach (var route in Route.GetRoutes(folder))
                foreach (var timetableSet in TimetableInfo.GetTimetableInfo(folder, route))
                foreach (var timetable in timetableSet.ORTTList)
                {
                    if (timetable.Trains.Count == 0)
                        continue;
                    var profileKey = (++key).ToString();
                    hostProfiles[profileKey] = new CPVirtualLaunchSelection
                    {
                        Role = CPVirtualRole.Server,
                        TimetableFile = timetableSet.fileName,
                        Timetable = timetable.Description,
                        // The timetable reader still requires a selected train
                        // syntactically. The dedicated server converts it to AI.
                        SeedTrain = timetable.Trains[0].Train,
                        Day = timetableSet.Day,
                        Season = timetableSet.Season,
                        Weather = timetableSet.Weather
                    };
                    var label = route.Name + " · " + timetable.Description;
                    html.Append("<option value='").Append(profileKey).Append("'>")
                        .Append(WebUtility.HtmlEncode(label)).Append("</option>");
                }
            }
            catch (Exception error)
            {
                Trace.WriteLine("CP Virtual timetable discovery: " + error);
            }

            if (html.Length == 0)
                return "<option value=''>Nenhum horário Open Rails encontrado</option>";
            return html.ToString();
        }

        private CPVirtualLaunchSelection FindDriverService(string service)
        {
            service = (service ?? String.Empty).Trim();
            if (String.IsNullOrEmpty(service))
                return null;

            var settings = new UserSettings(new string[0]);
            foreach (var folder in Folder.GetFolders(settings))
            foreach (var route in Route.GetRoutes(folder))
            foreach (var timetableSet in TimetableInfo.GetTimetableInfo(folder, route))
            foreach (var timetable in timetableSet.ORTTList)
            foreach (var train in timetable.Trains)
            {
                if (!String.Equals(train.Train, service, StringComparison.OrdinalIgnoreCase))
                    continue;

                return new CPVirtualLaunchSelection
                {
                    TimetableFile = timetableSet.fileName,
                    Timetable = timetable.Description,
                    SeedTrain = train.Train,
                    Day = timetableSet.Day,
                    Season = timetableSet.Season,
                    Weather = timetableSet.Weather
                };
            }
            return null;
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
                ". A página liga-se automaticamente ao posto quando o motor estiver pronto.</p></div>" +
                "<script>setInterval(function(){fetch('/CPVirtual/',{cache:'no-store'}).then(function(r){if(r.ok)location.replace('/CPVirtual/');}).catch(function(){});},1000);</script>";
        }

        public static void SaveSelection(CPVirtualLaunchSelection selection)
        {
            if (selection == null)
                return;
            var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Open Rails");
            Directory.CreateDirectory(directory);
            File.WriteAllLines(System.IO.Path.Combine(directory, "CPVirtual.launch"), new[]
            {
                "role=" + selection.Role.ToString().ToLowerInvariant(),
                "host=" + (selection.Host ?? String.Empty),
                "name=" + (selection.OperatorName ?? String.Empty),
                "service=" + (selection.Service ?? String.Empty),
                "post=" + (selection.Post ?? String.Empty),
                "visual=" + (selection.RealisticVisuals ? "realistic" : String.Empty)
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
