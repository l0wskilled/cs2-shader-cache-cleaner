using System;
using System.IO;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Linq;

namespace CS2ShaderCleaner
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            AutoDetectCS2Path();
        }

        private void AutoDetectCS2Path()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ?? 
                                         Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                {
                    if (key != null)
                    {
                        string installPath = key.GetValue("InstallPath")?.ToString();
                        if (!string.IsNullOrEmpty(installPath))
                        {
                            string cs2Guess = Path.Combine(installPath, @"steamapps\common\Counter-Strike Global Offensive");
                            if (Directory.Exists(cs2Guess))
                            {
                                CS2PathTextBox.Text = cs2Guess;
                                Log("Auto-detected CS2 installation folder successfully.");
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Registry auto-detection notice: {ex.Message}");
            }

            string defaultPath = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive";
            CS2PathTextBox.Text = Directory.Exists(defaultPath) ? defaultPath : "";
            Log("Could not auto-detect CS2 path. Defaulted or left empty.");
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = @"Select your CS2 folder (e.g. steamapps\common\Counter-Strike Global Offensive)",
                InitialDirectory = string.IsNullOrEmpty(CS2PathTextBox.Text) ? @"C:\" : CS2PathTextBox.Text
            };

            if (dialog.ShowDialog() == true)
            {
                CS2PathTextBox.Text = dialog.FolderName;
                Log($"Manually selected path: {dialog.FolderName}");
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            string cs2Path = CS2PathTextBox.Text;

            if (string.IsNullOrEmpty(cs2Path) || !Directory.Exists(cs2Path))
            {
                MessageBox.Show("Please select a valid CS2 folder before starting.", "Invalid Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SteamWarningText.Visibility = Visibility.Collapsed;
            StartButton.IsEnabled = false;
            LogTextBox.Clear();
            Log("=== Starting CS2 Shader Cache Cleansing Process ===");

            await Task.Run(() => RunCleansingRoutine(cs2Path));

            StartButton.IsEnabled = true;
            Log("=== Process Finished Safely ===");
        }

        private void RunCleansingRoutine(string cs2Path)
        {
            KillProcess("steam");
            KillProcess("cs2");

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string nvidiaService = "NVDisplay.ContainerLocalSystem";

            Log("Stopping background display services...");
            StopNvidiaService(nvidiaService);
            Thread.Sleep(2000); // Give file locks a moment to release

            Log("--- Cleaning NVIDIA Caches ---");
            ClearFolder(Path.Combine(localAppData, @"NVIDIA\DXCache"), "NVIDIA DXCache");
            ClearFolder(Path.Combine(localAppData, @"NVIDIA\GLCache"), "NVIDIA GLCache");

            Log("--- Cleaning AMD Caches ---");
            ClearFolder(Path.Combine(localAppData, @"AMD\DxCache"), "AMD DxCache");
            ClearFolder(Path.Combine(localAppData, @"AMD\DxcCache"), "AMD DxcCache");
            ClearFolder(Path.Combine(localAppData, @"AMD\OglCache"), "AMD OglCache");
            ClearFolder(Path.Combine(localAppData, @"AMD\VkCache"), "AMD VkCache");

            Log("--- Cleaning Windows & CS2 Caches ---");
            ClearFolder(Path.Combine(localAppData, "D3DSCache"), "DirectX D3DSCache");

            // Navigate up to steamapps folder
            try
            {
                DirectoryInfo steamappsDir = Directory.GetParent(cs2Path)?.Parent;
                if (steamappsDir != null && steamappsDir.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    string shaderCachePath = Path.Combine(steamappsDir.FullName, @"shadercache\730");
                    ClearFolder(shaderCachePath, "CS2 Steam Shader Cache");
                }
                else
                {
                    Log("-> Error: Selected path doesn't seem to be inside a 'steamapps' structure. CS2 Cache skipped.");
                }
            }
            catch (Exception ex)
            {
                Log($"-> Error traversing directories: {ex.Message}");
            }

            Log("Restoring display services...");
            StartNvidiaService(nvidiaService);

            RestartGraphicsDriver();

            Log("Success! Relevant CS2 shader files have been wiped.");
            RestartSteamAsNormalUser(cs2Path);
        }

        private void RestartSteamAsNormalUser(string cs2Path)
        {
            try
            {
                // Go up from ...\steamapps\common\Counter-Strike Global Offensive to the steam root directory
                DirectoryInfo steamappsDir = Directory.GetParent(cs2Path)?.Parent;
                string steamRoot = steamappsDir?.Parent?.FullName;
                string steamExe = steamRoot != null ? Path.Combine(steamRoot, "steam.exe") : null;
                
                if (steamExe != null && File.Exists(steamExe))
                {
                    Log("Restarting Steam with standard user privileges...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{steamExe}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Log("Notice: Could not find steam.exe automatically. Please start manually.");
                    // Show warning text safely on the UI thread
                    Dispatcher.Invoke(() => 
                    {
                        SteamWarningText.Visibility = Visibility.Visible;
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"Notice: Failed to auto-start Steam: {ex.Message}");
            }
        }

        private void ClearFolder(string folderPath, string displayName)
        {
            if (!Directory.Exists(folderPath))
            {
                Log($"-> {displayName} folder not found. Skipped.");
                return;
            }

            Log($"-> {displayName} folder found. Cleaning...");

            DirectoryInfo di = new DirectoryInfo(folderPath);
            int deletedFiles = 0;
            int skippedFiles = 0;

            foreach (FileInfo file in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    file.Delete();
                    deletedFiles++;
                }
                catch (Exception)
                {
                    skippedFiles++; 
                }
            }

            foreach (DirectoryInfo dir in di.EnumerateDirectories())
            {
                try { dir.Delete(true); } catch { }
            }

            Log($"   Cleaned {deletedFiles} files (Locked/Skipped: {skippedFiles}).");
        }

        private void KillProcess(string processName)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                if (processes.Length > 0)
                {
                    Log($"Closing running instances of {processName}...");
                    foreach (var p in processes)
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
            }
            catch (Exception ex) { Log($"Notice shutting down {processName}: {ex.Message}"); }
        }

        private void StopNvidiaService(string serviceName)
        {
            try
            {
                if (!ServiceController.GetServices().Any(s => s.ServiceName == serviceName))
                    return;

                ServiceController sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            catch { } // Silently fail if permissions are missing or service hangs
        }

        private void StartNvidiaService(string serviceName)
        {
            try
            {
                if (!ServiceController.GetServices().Any(s => s.ServiceName == serviceName))
                    return;

                ServiceController sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.StopPending)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }
            }
            catch { }
        }

        private void RestartGraphicsDriver()
        {
            try
            {
                Log("Refreshing active graphics driver device nodes...");
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c pnputil /restart-device \"DISPLAY\\*\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
            }
            catch { }
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }
    }
}
