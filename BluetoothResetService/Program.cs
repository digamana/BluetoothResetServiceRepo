using BluetoothResetService;
using Microsoft.Extensions.Hosting;

IHostBuilder hostBuilder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "BluetoothAutoResetService";
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService<BluetoothResetWorker>();
    });

await hostBuilder.Build().RunAsync();