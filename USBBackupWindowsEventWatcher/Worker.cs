using System.Management;
using Microsoft.Extensions.Options;

namespace USBBackupWindowsEventWatcher
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private ManagementEventWatcher? _watcher;
        private readonly BackupOptions _backupOptions;

        private int _totalFilesToCopy = 0;
        private int _filesCopiedCount = 0;

        private static readonly IReadOnlyList<string> ExcludeDirNames = new List<string>
        {
            "node_modules",
            ".git",
            ".svn",
            "bin",
            "obj"
        };

        public Worker(ILogger<Worker> logger, IOptions<BackupOptions> backupOptions)
        {
            _logger = logger;
            _backupOptions = backupOptions.Value;
            _logger.LogInformation("Configuration loaded: From='{From}', To='{To}', TargetDrive='{TargetDrive}'",
                                   _backupOptions.From, _backupOptions.To, _backupOptions.TargetDrive);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("USB Event Watcher service is starting.");

            try
            {
                var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2");

                _watcher = new ManagementEventWatcher(query);

                _watcher.EventArrived += Watcher_EventArrived;

                _watcher.Start();

                _logger.LogInformation("WMI event watching started successfully. Waiting for USB drive insertion...");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the WMI watcher.");
            }

            return Task.CompletedTask;
        }

        private async void Watcher_EventArrived(object sender, EventArrivedEventArgs e)
        {
            if (e.NewEvent.Properties["DriveName"]?.Value is string detectedDriveName)
            {
                string detectedDriveLetter = detectedDriveName.TrimEnd(':');

                if (detectedDriveLetter.Equals(_backupOptions.TargetDrive, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("******************************************************************");
                    _logger.LogInformation("🥳 TARGET USB Drive Detected! Drive Letter: {DriveLetter}", detectedDriveLetter);

                    int delaySeconds = 5;
                    _logger.LogInformation("Starting backup countdown: {Seconds} seconds.", delaySeconds);

                    for (int i = delaySeconds; i > 0; i--)
                    {
                        _logger.LogInformation("Time until copy starts: {Seconds}...", i);
                        await Task.Delay(1000);
                    }

                    _logger.LogInformation("Countdown complete. Proceeding with file operations.");

                    try
                    {
                        if (Directory.Exists(_backupOptions.To))
                        {
                            _logger.LogWarning("Clearing read-only attributes before deletion.");
                            SetNormalAttributes(_backupOptions.To);

                            _logger.LogWarning("Deleting existing destination folder: {Path}", _backupOptions.To);
                            Directory.Delete(_backupOptions.To, true);
                        }

                        Directory.CreateDirectory(_backupOptions.To);
                        _logger.LogInformation("Destination folder recreated successfully.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete or recreate the destination folder: {Path}. Aborting backup.", _backupOptions.To);
                        return;
                    }

                    string sourceDriveRoot = Path.GetPathRoot(_backupOptions.From) ?? string.Empty;

                    if (Directory.Exists(sourceDriveRoot))
                    {
                        try
                        {
                            _totalFilesToCopy = CountTotalFiles(_backupOptions.From);
                            _filesCopiedCount = 0;

                            _logger.LogInformation("Found {Count} files to copy (excluding: {Exclusions}). Starting backup process.",
                                _totalFilesToCopy, string.Join(", ", ExcludeDirNames));

                            CopyAll(_backupOptions.From, _backupOptions.To, _logger);

                            _logger.LogInformation("Backup process completed successfully.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Backup process failed during file copying.");
                        }
                    }
                    else
                    {
                        _logger.LogError("Source Drive Root ({Root}) is not accessible. Cannot start backup.", sourceDriveRoot);
                    }

                    _logger.LogInformation("******************************************************************");
                }
                else
                {
                    _logger.LogInformation("USB Drive Detected, but not the target. Detected: {DetectedDrive}, Target: {TargetDrive}",
                        detectedDriveLetter, _backupOptions.TargetDrive);
                }
            }
        }

        public override Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("USB Event Watcher service is stopping.");

            if (_watcher != null)
            {
                _watcher.Stop();
                _watcher.Dispose();
                _watcher = null;
                _logger.LogInformation("WMI event watching stopped and resources disposed.");
            }

            return base.StopAsync(stoppingToken);
        }

        private static int CountTotalFiles(string sourceDir)
        {
            if (!Directory.Exists(sourceDir))
                return 0;

            int fileCount = 0;

            try
            {
                fileCount += Directory.GetFiles(sourceDir).Length;

                foreach (var subDir in Directory.GetDirectories(sourceDir))
                {
                    string dirName = Path.GetFileName(subDir);

                    if (ExcludeDirNames.Any(e => e.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    fileCount += CountTotalFiles(subDir);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore directories we can't access during counting
            }
            catch (DirectoryNotFoundException)
            {
                // Ignore directories that might vanish
            }

            return fileCount;
        }

        private void CopyAll(string sourceDir, string destinationDir, ILogger logger)
        {
            var dir = new DirectoryInfo(sourceDir);

            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            Directory.CreateDirectory(destinationDir);

            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);

                try
                {
                    file.CopyTo(targetFilePath, true);

                    Interlocked.Increment(ref _filesCopiedCount);

                    LogProgress(file.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to copy file: {FileName}", file.FullName);
                }
            }

            DirectoryInfo[] subDirs = dir.GetDirectories();
            foreach (DirectoryInfo subDir in subDirs)
            {
                string dirName = subDir.Name;

                if (ExcludeDirNames.Any(e => e.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                { 
                    continue;
                }

                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyAll(subDir.FullName, newDestinationDir, logger);
            }
        }

        private void SetNormalAttributes(string path)
        {
            if (!Directory.Exists(path)) return;

            foreach (string file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not set normal attributes on file {File}: {Error}", file, ex.Message);
                }
            }
        }

        private void LogProgress(string currentFileName)
        {
            if (_totalFilesToCopy == 0) return;

            double percentage = (double)_filesCopiedCount / _totalFilesToCopy;
            int barLength = 20;
            int completedChars = (int)Math.Round(percentage * barLength);

            string bar = $"[{new string('#', completedChars)}{new string('.', barLength - completedChars)}]";

            _logger.LogInformation("{Bar} {Percent:P0} | Copying {FileName} ({Copied}/{Total})",
                bar, percentage, currentFileName, _filesCopiedCount, _totalFilesToCopy);
        }
    }
}