using USBBackupWindowsEventWatcher;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .ConfigureServices((hostContext, services) =>
    {
        services.Configure<BackupOptions>(
            hostContext.Configuration.GetSection(BackupOptions.OptionsSection));

        services.AddHostedService<Worker>();
    })
    .Build();

host.Run();