using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelegramClient.Background;
using TelegramClient.Configuration;
using TeleSharp.TL;
using TeleSharp.TL.Messages;
using TLSharp.Core.Utils;
using TLRequestReadHistory = TeleSharp.TL.Channels.TLRequestReadHistory;

//Везде стоят Task.Delay т.к. телега может дропнуть приложение за излишнюю активность!
namespace TelegramClient.Telegram
{
    public class TelegramCore : ScopedProcessor
    {
        private readonly AuthConfiguration _authConfiguration;
        private readonly KeywordsConfiguration _keywordsConfiguration;
        private readonly MyChannelConfiguration _myChannelConfiguration;

        public TelegramCore(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json");

            var config = builder.Build();

            _authConfiguration = config
                .GetSection(nameof(AuthConfiguration))
                .Get<AuthConfiguration>();
            _keywordsConfiguration = config
                .GetSection(nameof(KeywordsConfiguration))
                .Get<KeywordsConfiguration>();
            _myChannelConfiguration = config
                .GetSection(nameof(MyChannelConfiguration))
                .Get<MyChannelConfiguration>();
        }

        private TLSharp.Core.TelegramClient NewClient() =>
            new TLSharp.Core.TelegramClient(_authConfiguration.ApiId, _authConfiguration.ApiHash, null,
                _authConfiguration.SessionUserId);

        private async Task CheckConnect()
        {
            using var telegram = NewClient();
            await telegram.ConnectAsync();

            try
            {
                await telegram.GetUserDialogsAsync();
                Console.WriteLine("Авторизация прошла успешно!");
            }
            catch (Exception e)
            {
                var hash = await telegram.SendCodeRequestAsync(_authConfiguration.PhoneNumber);
                Console.WriteLine("Введите проверочный код: ");
                var code = Console.ReadLine();
                await telegram.MakeAuthAsync(_authConfiguration.PhoneNumber, hash, code);
                Console.WriteLine("Авторизация прошла успешно! Перезапустите приложение.");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }

            telegram.Dispose();
        }

        private async Task Run()
        {
            await CheckConnect();
            while (true)
            {
                await Task.Delay(100000);
                try
                {
                    await RunTelegram();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    Console.WriteLine(e.StackTrace);
                    Console.WriteLine(e.Source);
                    await Task.Delay(90000);
                }
            }
        }

        private async Task RunTelegram()
        {
            using var telegram = NewClient();
            await telegram.ConnectAsync();

            var dialogs = new TLDialogs();
            dialogs = (TLDialogs) await telegram.GetUserDialogsAsync();

            var randomChat =
                dialogs.Dialogs.FirstOrDefault(x => x.Peer is TLPeerChannel && x.UnreadCount > 0 && x.UnreadCount < 50);

            var peer = randomChat.Peer as TLPeerChannel;
            var chat = dialogs.Chats.OfType<TLChannel>().First(x => x.Id == peer?.ChannelId);
            var target = new TLInputPeerChannel {ChannelId = chat.Id, AccessHash = chat.AccessHash.Value};
            var hist = await telegram.GetHistoryAsync(target, 0, 0, 0, randomChat.UnreadCount) as TLChannelMessages;

            if (hist == null)
                return;
            if (chat.Title == _myChannelConfiguration.ChannelHeapName ||
                chat.Title == _myChannelConfiguration.ChannelWithKeywordsName)
                return;

            var messages = hist.Messages.OfType<TLMessage>().Reverse();
            // await ForwardMessagesToChannelHeap(dialogs, messages, target, telegram);
            await ForwardMessagesToChannelWithKeywords(dialogs, messages, target, telegram);
            await MarkAsRead(target, telegram);
            Console.WriteLine($"Сообщения канала {chat.Title} прочитаны");
            telegram.Dispose();
        }

        private async Task MarkAsRead(TLInputPeerChannel target, TLSharp.Core.TelegramClient telegram)
        {
            var ch = new TLInputChannel() {ChannelId = target.ChannelId, AccessHash = target.AccessHash};
            var request = new TLRequestReadHistory()
            {
                Channel = ch,
            };
            await telegram.SendRequestAsync<bool>(request);
        }

        private async Task ForwardMessagesToChannelHeap(TLDialogs dialogs, IEnumerable<TLMessage> messages,
            TLInputPeerChannel target, TLSharp.Core.TelegramClient telegram)
        {
            var channel = dialogs.Chats
                .OfType<TLChannel>()
                .FirstOrDefault(c => c.Title == _myChannelConfiguration.ChannelHeapName);
            var mgs = new TLRequestForwardMessages()
            {
                Id = new TLVector<int>(messages.Select(p => p.Id)),
                ToPeer = new TLInputPeerChannel() {ChannelId = channel.Id, AccessHash = channel.AccessHash.Value},
                FromPeer = new TLInputPeerChannel() {ChannelId = target.ChannelId, AccessHash = target.AccessHash},
                RandomId = new TLVector<long>(messages.Select(p => Helpers.GenerateRandomLong()))
            };
            await Task.Delay(10000);
            await telegram.SendRequestAsync<TLUpdates>(mgs);
        }

        private async Task ForwardMessagesToChannelWithKeywords(TLDialogs dialogs, IEnumerable<TLMessage> messages,
            TLInputPeerChannel target, TLSharp.Core.TelegramClient telegram)
        {
            var messagesFiltered = messages
                .Where(
                    p => Regex.IsMatch(
                        p.Message.ToLower(),
                        string.Join("|", _keywordsConfiguration.Keywords),
                        RegexOptions.IgnoreCase
                    )
                );

            if (messagesFiltered.ToList().Count < 1)
                return;

            var channel = dialogs.Chats
                .OfType<TLChannel>()
                .FirstOrDefault(c => c.Title == _myChannelConfiguration.ChannelWithKeywordsName);
            var mgs = new TLRequestForwardMessages()
            {
                Id = new TLVector<int>(messagesFiltered.Select(p => p.Id)),
                ToPeer = new TLInputPeerChannel()
                {
                    ChannelId = channel.Id,
                    AccessHash = channel.AccessHash.Value
                },
                FromPeer = new TLInputPeerChannel()
                {
                    ChannelId = target.ChannelId,
                    AccessHash = target.AccessHash
                },
                RandomId = new TLVector<long>(messagesFiltered.Select(p => Helpers.GenerateRandomLong()))
            };
            await Task.Delay(10000);
            await telegram.SendRequestAsync<TLUpdates>(mgs);
        }

        public override async Task ProcessInScope(IServiceProvider serviceProvider)
        {
            await Run();
        }
    }
}