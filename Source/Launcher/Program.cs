// COPYRIGHT 2009 - 2026 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

/* Open Rails Launcher
 *
 * This is the program which users launch. Its purpose is to check for the
 * required dependencies and Open Rails files before launching the menu.
 *
 * .NET checks for itself on launch
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using ORTS.Settings;

namespace Launcher
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();

            // Check for any missing components.
            var path = Path.GetDirectoryName(Application.ExecutablePath);

            List<string> missingORFiles = new List<string>();
            CheckOR(missingORFiles, path);
            if (missingORFiles.Count > 0)
            {
                MessageBox.Show($"{Application.ProductName} is missing the following:\n\n{string.Join("\n", missingORFiles.ToArray())}\n\nPlease re-install the software.", Application.ProductName);
                return;
            }
            // CP Virtual starts in its browser lobby. The classic Open Rails
            // menu remains available from the lobby for maintenance and for
            // content which has not yet been converted to a CPV service.
            CPVirtualLaunchSelection selection;
            using (var lobby = new CPVirtualLobby(path))
                selection = lobby.Run();

            if (selection == null || selection.Role == CPVirtualRole.Dispatcher)
                return;

            CPVirtualLobby.ApplyRealisticVisualProfile(selection);

            if (selection.Role == CPVirtualRole.Server)
            {
                CPVirtualLobby.SaveSelection(selection);
                var runStart = new ProcessStartInfo(Path.Combine(path, "RunActivity.exe"));
                runStart.UseShellExecute = false;
                runStart.WorkingDirectory = path;
                runStart.Environment["CPV_ROLE"] = "server";
                runStart.Arguments = "-multiplayerserver -timetable " + Quote(selection.TimetableFile) + " " +
                    Quote(selection.Timetable + ":" + selection.SeedTrain) + " 0 1 0";
                Process.Start(runStart);
                return;
            }

            if (selection.Role == CPVirtualRole.Driver)
            {
                // A driver selects a timetable service in the CP Virtual lobby;
                // open the multiplayer client directly in that cab instead of
                // making the operator repeat the selection in Menu.exe.
                var settings = new UserSettings(new string[0]);
                settings.Multiplayer_Host = selection.Host;
                settings.Multiplayer_Port = 30000;
                settings.Save();

                CPVirtualLobby.SaveSelection(selection);
                var runStart = new ProcessStartInfo(Path.Combine(path, "RunActivity.exe"));
                runStart.UseShellExecute = false;
                runStart.WorkingDirectory = path;
                runStart.Environment["CPV_ROLE"] = "driver";
                runStart.Environment["CPV_HOST"] = selection.Host ?? String.Empty;
                runStart.Environment["CPV_SERVICE"] = selection.Service ?? String.Empty;
                runStart.Arguments = "-multiplayerclient -timetable " + Quote(selection.TimetableFile) + " " +
                    Quote(selection.Timetable + ":" + selection.SeedTrain) + " " +
                    selection.Day + " " + selection.Season + " " + selection.Weather;
                Process.Start(runStart);
                return;
            }

            var menuStart = new ProcessStartInfo(Path.Combine(path, "Menu.exe"));
            menuStart.UseShellExecute = false;
            menuStart.Environment["CPV_ROLE"] = selection.Role.ToString().ToLowerInvariant();
            menuStart.Environment["CPV_HOST"] = selection.Host ?? String.Empty;
            menuStart.Environment["CPV_SERVICE"] = selection.Service ?? String.Empty;
            CPVirtualLobby.SaveSelection(selection);

            var process = Process.Start(menuStart);
            process.WaitForInputIdle();
        }

        static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }

        static void CheckOR(List<string> missingFiles, string path)
        {
            foreach (var file in new[] {
                // Required libraries:
                "GNU.Gettext.dll",
                "GNU.Gettext.WinForms.dll",
                @"Native/X86/OpenAL32.dll",
                @"Native/X64/OpenAL32.dll",
                // Programs:
                "Menu.exe",
                "RunActivity.exe",
            })
            {
                if (!File.Exists(Path.Combine(path, file)))
                    missingFiles.Add($"File '{file}'");
            }
        }
    }
}
