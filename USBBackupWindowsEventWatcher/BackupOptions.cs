namespace USBBackupWindowsEventWatcher
{
    public class BackupOptions
    {
        public const string OptionsSection = "Options";

        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string TargetDrive { get; set; } = string.Empty;
    }
}