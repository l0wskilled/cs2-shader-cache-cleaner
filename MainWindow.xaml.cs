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
        private enum GpuVendor { Nvidia, Amd, Unknown }

        public MainWindow()
        {
            InitializeComponent();
            AutoDetectSteamPath();
        }

        private GpuVendor DetectGpuVendor()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (key != null)
                    {
                        foreach (string subKeyName in key.GetSubKeyNames())
                        {
                            if (subKeyName.Length == 4 && int.TryParse(subKeyName, out _))
                            {
                                using (RegistryKey subKey = key.OpenSubKey(subKeyName))
                                {
                                    string driverDesc = subKey?.GetValue("DriverDesc")?.ToString().ToLower();
                                    string providerName = subKey?.GetValue("ProviderName")?.ToString().ToLower();

                                    if (!string.IsNullOrEmpty(driverDesc))
                                    {
                                        if (driverDesc.Contains("nvidia") || (providerName != null && providerName.Contains("nvidia")))
                                            return GpuVendor.Nvidia;

                                        if (driverDesc.Contains("amd") || driverDesc.Contains("radeon") || (providerName != null && providerName.Contains("advanced micro devices")))
                                            return GpuVendor.Amd;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GPU Detection notice: {ex.Message}");
            }

            return GpuVendor.Unknown;
        }

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

            string defaultPath = @"C:\Program Files (x86)\Steam";
            SteamPathTextBox.Text = Directory.Exists(defaultPath) ? defaultPath : "";
            Log("Could not auto-detect Steam via registry. Defaulted or left empty.");
        }

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
            KillProcess("steam");
            KillProcess("cs2");

            GpuVendor vendor = DetectGpuVendor();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string nvidiaService = "NVDisplay.ContainerLocalSystem";

            switch (vendor)
            {
                case GpuVendor.Nvidia:
                    Log("Hardware Detection: NVIDIA GPU found.");
                    StopNvidiaService(nvidiaService);
                    
                    Log("Waiting 2 seconds for active system file handles to release...");
                    Thread.Sleep(2000);
                    
                    Log("Clearing NVIDIA Caches...");
                    ClearFolder(Path.Combine(localAppData, @"NVIDIA\DXCache"));
                    ClearFolder(Path.Combine(localAppData, @"NVIDIA\GLCache"));
                    
                    StartNvidiaService(nvidiaService);
                    break;

                case GpuVendor.Amd:
                    Log("Hardware Detection: AMD GPU found.");
                    Log("Clearing AMD Caches...");
                    ClearFolder(Path.Combine(localAppData, @"AMD\DxCache"));
                    ClearFolder(Path.Combine(localAppData, @"AMD\GLCache"));
                    ClearFolder(Path.Combine(localAppData, @"AMD\VkCache"));
                    break;

                case GpuVendor.Unknown:
                default:
                    Log("Hardware Detection: Could not clearly identify GPU. Running universal wipe fallback.");
                    StopNvidiaService(nvidiaService);
                    
                    Thread.Sleep(2000);
                    
                    ClearFolder(Path.Combine(localAppData, @"NVIDIA\DXCache"));
                    ClearFolder(Path.Combine(localAppData, @"NVIDIA\GLCache"));
                    ClearFolder(Path.Combine(localAppData, @"AMD\DxCache"));
                    ClearFolder(Path.Combine(localAppData, @"AMD\GLCache"));
                    ClearFolder(Path.Combine(localAppData, @"AMD\VkCache"));
                    
                    StartNvidiaService(nvidiaService);
                    break;
            }

            Log("Checking general Windows DirectX Cache...");
            ClearFolder(Path.Combine(localAppData, "D3DSCache"));

            Log("Clearing CS2 Steam Shader Cache (AppID 730)...");
            string cs2CachePath = Path.Combine(steamPath, @"steamapps\shadercache\730");
            ClearFolder(cs2CachePath);

            RestartGraphicsDriver();

            Log("Success! All shader files have been wiped.");
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
                    skippedFiles++; 
                }
            }

            foreach (DirectoryInfo dir in di.EnumerateDirectories())
            {
                try { dir.Delete(true); } catch { }
            }

            Log($"-> Cleared target successfully. Wiped: {deletedFiles} files, Locked/Skipped: {skippedFiles}.");
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
                if (!ServiceController.GetServices().Any(s => s.ServiceName == serviceName))
                    return;

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
