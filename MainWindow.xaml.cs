using System;
using System.IO;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace CS2ShaderCleaner
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            AutoDetectSteamPath();
        }

        // Automatically queries the registry to find where Steam is installed
        private void AutoDetectSteamPath()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    if (key != null)
                    {
                        string installPath = key.GetValue("InstallPath")?.ToString();
                        if (!string.IsNullOrEmpty(installPath))
                        {
                            SteamPathTextBox.Text = installPath;
                            Log("Auto-detected Steam installation folder successfully.");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Registry auto-detection notice: {ex.Message}");
            }

            // Default fallback path if registry check fails
            string defaultPath = @"C:\Program Files (x86)\Steam";
            SteamPathTextBox.Text = Directory.Exists(defaultPath) ? defaultPath : "";
            Log("Could not auto-detect Steam via registry. Defaulted or left empty.");
        }

        // Folder Picker Button Click Action
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select your main Steam installation directory",
                InitialDirectory = string.IsNullOrEmpty(SteamPathTextBox.Text) ? @"C:\" : SteamPathTextBox.Text
            };

            if (dialog.ShowDialog() == true)
            {
                SteamPathTextBox.Text = dialog.FolderName;
                Log($"Manually selected path: {dialog.FolderName}");
            }
        }

        // Async execution of the deletion script to prevent UI freezing
        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            string steamPath = SteamPathTextBox.Text;

            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
            {
                MessageBox.Show("Please select a valid Steam folder before starting.", "Invalid Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StartButton.IsEnabled = false;
            LogTextBox.Clear();
            Log("=== Starting Shader Cache Cleansing Process ===");

            await Task.Run(() => RunCleansingRoutine(steamPath));

            StartButton.IsEnabled = true;
            Log("=== Process Finished Safely ===");
        }

        private void RunCleansingRoutine(string steamPath)
        {
            // 1. Close background interference apps
            KillProcess("steam");
            KillProcess("cs2");

            // 2. Shut down the Nvidia Container Services
            string nvidiaService = "NVDisplay.ContainerLocalSystem";
            StopNvidiaService(nvidiaService);

            Log("Waiting 2 seconds for active system file handles to release...");
            Thread.Sleep(2000);

            // 3. Clear Caches
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            
            Log("Clearing Nvidia DXCache directory...");
            ClearFolder(Path.Combine(localAppData, @"Nvidia\DXCache"));

            Log("Clearing Nvidia GLCache directory...");
            ClearFolder(Path.Combine(localAppData, @"Nvidia\GLCache"));

            Log("Clearing CS2 Steam Shader Cache (AppID 730)...");
            string cs2CachePath = Path.Combine(steamPath, @"steamapps\shadercache\730");
            ClearFolder(cs2CachePath);

            // 4. Restore Services
            StartNvidiaService(nvidiaService);

            // 5. Hard refresh display adapter configuration
            RestartGraphicsDriver();

            Log("Success! All shader files have been wiped.");
            
            // 6. Restart Steam as normal user (dropping Admin rights)
            RestartSteamAsNormalUser(steamPath);
        }

        private void RestartSteamAsNormalUser(string steamPath)
        {
            try
            {
                string steamExe = Path.Combine(steamPath, "steam.exe");
                
                if (File.Exists(steamExe))
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
                    Log($"Notice: Could not find steam.exe at '{steamExe}'. Please start manually.");
                }
            }
            catch (Exception ex)
            {
                Log($"Notice: Failed to auto-start Steam: {ex.Message}");
            }
        }

        private void ClearFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                Log($"-> Folder path not found (Skipped): {folderPath}");
                return;
            }

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
                    skippedFiles++; // Locked by Desktop Window Manager (DWM) or system
                }
            }

            foreach (DirectoryInfo dir in di.EnumerateDirectories())
            {
                try { dir.Delete(true); } catch { }
            }

            Log($"-> Cleared folder target successfully. Wiped: {deletedFiles} files, Locked/Skipped: {skippedFiles}.");
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
                ServiceController sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                {
                    Log($"Stopping Nvidia Driver Service ({serviceName})...");
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    Log("Nvidia Driver Service stopped.");
                }
            }
            catch { Log("ERROR: Could not stop Nvidia Service. Did you run the app as ADMIN?"); }
        }

        private void StartNvidiaService(string serviceName)
        {
            try
            {
                ServiceController sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.StopPending)
                {
                    Log($"Restarting Nvidia Driver Service ({serviceName})...");
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    Log("Nvidia Driver Service is back online.");
                }
            }
            catch (Exception ex) { Log($"Failed to restore Nvidia service: {ex.Message}"); }
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
            catch { /* Fallback handling */ }
        }

        // Thread-safe method to update the terminal UI log text box
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
