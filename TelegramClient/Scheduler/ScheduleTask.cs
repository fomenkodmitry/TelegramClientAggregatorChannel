using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NPOI.XWPF.UserModel;
using TelegramClient.Configuration;
using TeleSharp.TL;
using TeleSharp.TL.Messages;

namespace TelegramClient.Scheduler
{
    public class ScheduleTask : ScheduledProcessor
    {
        private readonly AuthConfiguration _authConfiguration;
        private readonly MyChannelConfiguration _myChannelConfiguration;
        private readonly ReportConfiguration _reportConfiguration;

        public ScheduleTask(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json");

            var config = builder.Build();

            _authConfiguration = config
                .GetSection(nameof(AuthConfiguration))
                .Get<AuthConfiguration>();
            _myChannelConfiguration = config
                .GetSection(nameof(MyChannelConfiguration))
                .Get<MyChannelConfiguration>();
            _reportConfiguration = config
                .GetSection(nameof(ReportConfiguration))
                .Get<ReportConfiguration>();
        }

        protected override string Schedule => "0 3 * * *";
        public override async Task ProcessInScope(IServiceProvider serviceProvider)
        {
            try
            {
                await ExportMessagesToWord();
            }
            catch (Exception e)
            {
                var path = Path.Combine(
                    _reportConfiguration.FullPath,
                    $"{_reportConfiguration.Name}-{DateTime.Now:MM-dd-yyyy}.error.txt"
                );
                await using var file = File.CreateText(path);
                await file.WriteLineAsync(e.Message);
            }
        }

        private async Task ExportMessagesToWord()
        {
            using var telegram = new TLSharp.Core.TelegramClient(_authConfiguration.ApiId, _authConfiguration.ApiHash,
                null,
                _authConfiguration.SessionUserId);
            await telegram.ConnectAsync();

            await Task.Delay(10000);
            var dialogs = (TLDialogs) await telegram.GetUserDialogsAsync();
            var channel = dialogs.Chats
                .OfType<TLChannel>()
                .FirstOrDefault(c => c.Title == _myChannelConfiguration.ChannelHeapName);
            var inputPeer = new TLInputPeerChannel()
            {
                ChannelId = channel.Id,
                AccessHash = (long) channel.AccessHash
            };

            var offset = 0;
            var sig = false;

            await using var fs = new FileStream(
                Path.Combine(
                    _reportConfiguration.FullPath,
                    $"{_reportConfiguration.Name}-{DateTime.Now:MM-dd-yyyy}.docx"
                ),
                FileMode.Create,
                FileAccess.Write
            );
            var doc = new XWPFDocument();
            var paragraph = doc.CreateParagraph();
            var run = paragraph.CreateRun();
            while (true)
            {
                if (sig)
                    break;

                await Task.Delay(10000);
                var res = await telegram.SendRequestAsync<TLChannelMessages>(
                    new TLRequestGetHistory()
                    {
                        Peer = inputPeer,
                        Limit = 100,
                        AddOffset = offset,
                        OffsetId = 0
                    }
                );
                var msgs = res.Messages;

                if (res.Count <= offset)
                    break;
                offset += msgs.Count;

                foreach (var msg in msgs)
                {
                    if (!(msg is TLMessage message)) continue;

                    if (string.IsNullOrEmpty(message.Message))
                        continue;

                    if (message.Date > (int) DateTimeOffset.Parse(DateTime.UtcNow.ToString("MM/dd/yyyy"))
                        .ToUnixTimeSeconds())
                        continue;

                    if (message.Date < (int) DateTimeOffset.Parse(DateTime.UtcNow.AddDays(-1).ToString("MM/dd/yyyy"))
                        .ToUnixTimeSeconds())
                    {
                        sig = true;
                        break;
                    }

                    await Task.Delay(1000);
                    var ch = dialogs.Chats
                        .OfType<TLChannel>()
                        .FirstOrDefault(c => c.Id == message.FwdFrom.ChannelId);

                    if (ch == null)
                        continue;
                    run.AppendText(@$"Канал: {ch.Title}");
                    run.AddCarriageReturn();
                    run.AppendText(
                        @$"Дата: {new DateTime(1970, 1, 1, 0, 0, 0, 0).AddSeconds(message.Date):d-M-yyyy HH:mm:ss}"
                    );
                    run.AddCarriageReturn();
                    run.AppendText(
                        @$"Сообщение: {message.Message.Replace("\n", string.Empty).Replace("\r", string.Empty).Replace("\t", string.Empty)}"
                    );
                    run.AddCarriageReturn();
                    run.AddCarriageReturn();
                    run.AddCarriageReturn();
                }
            }

            doc.Write(fs);
        }
    }
}