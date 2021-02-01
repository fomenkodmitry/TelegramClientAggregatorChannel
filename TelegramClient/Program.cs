using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TelegramClient.Scheduler;
using TelegramClient.Telegram;

namespace TelegramClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await CreateHostBuilder();
        }
        
        private static async Task CreateHostBuilder() =>
            await new HostBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    // services.AddHostedService<ScheduleTask>();
                    services.AddHostedService<TelegramCore>();
                })
                .RunConsoleAsync();
    }
}