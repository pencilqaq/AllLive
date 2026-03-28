using AllLive.Core.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp;
using AllLive.Core.Danmaku.Proto;
using ProtoBuf;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AllLive.Core.Danmaku
{
    public class DouyinDanmakuArgs
    {
        public string WebRid { get; set; }
        public string RoomId { get; set; }
        public string UserId { get; set; }
        public string Cookie { get; set; }
    }
    public class DouyinDanmaku : ILiveDanmaku
    {
        public int HeartbeatTime => 10 * 1000;

        public event EventHandler<LiveMessage> NewMessage;
        public event EventHandler<string> OnClose;
        private string baseUrl = "wss://webcast3-ws-web-lq.douyin.com/webcast/im/push/v2/";

        System.Timers.Timer timer;
        WebSocket ws;
        DouyinDanmakuArgs danmakuArgs;
        private string ServerUrl { get; set; }
        private string BackupUrl { get; set; }

        private const int MaxReconnectAttempts = 5;
        private int reconnectAttempts;
        private bool isStopping;
        private bool useBackupEndpoint;
        private CancellationTokenSource reconnectTokenSource;
        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);
        private Func<string, string, Task<string>> signatureProvider;

        public DouyinDanmaku()
        {
            signatureProvider = DefaultSignatureProvider;
        }

        public void SetSignatureProvider(Func<string, string, Task<string>> provider)
        {
            signatureProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public async Task Start(object args)
        {
            danmakuArgs = args as DouyinDanmakuArgs ?? throw new ArgumentException("args must be DouyinDanmakuArgs", nameof(args));
            
            Trace.WriteLine($"========== DouyinDanmaku.Start ==========");
            Trace.WriteLine($"[Danmaku] RoomId={danmakuArgs.RoomId}");
            Trace.WriteLine($"[Danmaku] WebRid={danmakuArgs.WebRid}");
            Trace.WriteLine($"[Danmaku] UserId={danmakuArgs.UserId}");
            Trace.WriteLine($"[Danmaku] Cookie={danmakuArgs.Cookie?.Substring(0, Math.Min(80, danmakuArgs.Cookie?.Length ?? 0))}...");
            Trace.WriteLine($"[Danmaku] isStopping(before)={isStopping}");
            Trace.WriteLine($"[Danmaku] reconnectAttempts(before)={reconnectAttempts}");
            Trace.WriteLine($"[Danmaku] ws==null(before)={ws == null}");
            
            isStopping = false;
            reconnectAttempts = 0;
            useBackupEndpoint = false;
            CancelReconnect();
            Trace.WriteLine($"[Danmaku] State reset: isStopping={isStopping}, reconnectAttempts={reconnectAttempts}");
            
            var ts = Utils.GetTimestampMs();
            var query = new Dictionary<string, string>()
            {
                { "app_name", "douyin_web" },
                { "version_code", "180800" },
                { "webcast_sdk_version", "1.3.0" },
                { "update_version_code", "1.3.0" },
                { "compress", "gzip" },
                { "cursor", $"h-1_t-{ts}_r-1_d-1_u-1" },
                { "host", "https://live.douyin.com" },
                { "aid", "6383" },
                { "live_id", "1" },
                { "did_rule", "3" },
                { "debug", "false" },
                { "maxCacheMessageNumber", "20" },
                { "endpoint", "live_pc" },
                { "support_wrds", "1" },
                { "im_path", "/webcast/im/fetch/" },
                { "user_unique_id", danmakuArgs.UserId },
                { "device_platform", "web" },
                { "cookie_enabled", "true" },
                { "screen_width", "1920" },
                { "screen_height", "1080" },
                { "browser_language", "zh-CN" },
                { "browser_platform", "Win32" },
                { "browser_name", "Mozilla" },
                { "browser_version", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0" },
                { "browser_online", "true" },
                { "tz_name", "Asia/Shanghai" },
                { "identity", "audience" },
                { "room_id", danmakuArgs.RoomId },
                { "heartbeatDuration", "0" },
            };

            var sign = await signatureProvider(danmakuArgs.RoomId, danmakuArgs.UserId);
            Trace.WriteLine($"[Danmaku] Signature: {sign}");
            query.Add("signature", sign);

            var url = $"{baseUrl}?{Utils.BuildQueryString(query)}";
            ServerUrl = url;
            BackupUrl = url.Replace("webcast3-ws-web-lq", "webcast5-ws-web-lf");
            Trace.WriteLine($"[Danmaku] WebSocket URL: {url.Substring(0, Math.Min(150, url.Length))}...");
            Trace.WriteLine($"[Danmaku] Connecting WebSocket...");
            await ConnectAsync(useBackup: false);
        }

        private async void Ws_OnOpen(object sender, EventArgs e)
        {
            try
            {
                Trace.WriteLine($"[DouyinDanmaku.Ws_OnOpen] WebSocket connected!");
                reconnectAttempts = 0;
                useBackupEndpoint = false;
                CancelReconnect();
                await SendHeartBeatDataAsync().ConfigureAwait(false);
                timer?.Start();
                Trace.WriteLine($"[DouyinDanmaku.Ws_OnOpen] Heartbeat timer started");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.Ws_OnOpen] error: {ex.Message}");
            }
        }

        private async void Ws_OnMessage(object sender, MessageEventArgs e)
        {
            try
            {
                reconnectAttempts = 0;
                var message = e.RawData;
                var wssPackage = DeserializeProto<PushFrame>(message);
                var logId = wssPackage.logId;
                var decompressed = GzipDecompress(wssPackage.Payload);
                var payloadPackage = DeserializeProto<Response>(decompressed);
                if (payloadPackage.needAck ?? false)
                {
                    await SendACKDataAsync(logId ?? 0, payloadPackage.internalExt).ConfigureAwait(false);
                }

                foreach (var msg in payloadPackage.messagesLists)
                {
                    if (msg.Method == "WebcastChatMessage")
                    {
                        UnPackWebcastChatMessage(msg.Payload);
                    }
                    else if (msg.Method == "WebcastRoomUserSeqMessage")
                    {
                        UnPackWebcastRoomUserSeqMessage(msg.Payload);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.Ws_OnMessage] error: {ex.Message}");
            }
        }

        private void UnPackWebcastChatMessage(byte[] payload)
        {
            try
            {
                var chatMessage = DeserializeProto<ChatMessage>(payload);
                NewMessage?.Invoke(this, new LiveMessage()
                {
                    Type = LiveMessageType.Chat,
                    Color = DanmakuColor.White,
                    Message = chatMessage.Content,
                    UserName = chatMessage.User.nickName,
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.UnPackWebcastChatMessage] error: {ex.Message}");
            }
        }

        void UnPackWebcastRoomUserSeqMessage(byte[] payload)
        {
            try
            {
                var roomUserSeqMessage = DeserializeProto<RoomUserSeqMessage>(payload);
                NewMessage?.Invoke(this, new LiveMessage()
                {
                    Type = LiveMessageType.Online,
                    Data = roomUserSeqMessage.totalUser,
                    Color = DanmakuColor.White,
                    Message = "",
                    UserName = "",
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.UnPackWebcastRoomUserSeqMessage] error: {ex.Message}");
            }
        }

        private void Ws_OnClose(object sender, CloseEventArgs e)
        {
            try
            {
                Trace.WriteLine($"[DouyinDanmaku.Ws_OnClose] WebSocket closed: Code={e.Code}, Reason={e.Reason}");
                if (isStopping)
                {
                    Trace.WriteLine($"[DouyinDanmaku.Ws_OnClose] Stopping, ignore close event");
                    return;
                }
                HandleConnectionFailure(string.IsNullOrEmpty(e.Reason) ? "Danmaku server closed" : e.Reason);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.Ws_OnClose] error: {ex.Message}");
            }
        }

        private void Ws_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            try
            {
                Trace.WriteLine($"[DouyinDanmaku.Ws_OnError] WebSocket error: {e.Message}");
                if (isStopping)
                {
                    Trace.WriteLine($"[DouyinDanmaku.Ws_OnError] Stopping, ignore error event");
                    return;
                }
                HandleConnectionFailure(e.Message);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.Ws_OnError] error: {ex.Message}");
            }
        }

        private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            Heartbeat();
        }

        public void Heartbeat()
        {
            try
            {
                _ = SendHeartBeatDataAsync();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.Heartbeat] error: {ex.Message}");
            }
        }

        public async Task Stop()
        {
            Trace.WriteLine($"========== DouyinDanmaku.Stop ==========");
            Trace.WriteLine($"[DouyinDanmaku.Stop] Setting isStopping=true");
            isStopping = true;
            CancelReconnect();
            await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                Trace.WriteLine($"[DouyinDanmaku.Stop] Cleaning WebSocket...");
                reconnectAttempts = 0;
                useBackupEndpoint = false;
                CleanupWebSocket();
                Trace.WriteLine($"[DouyinDanmaku.Stop] WebSocket cleaned");
            }
            finally
            {
                _connectionSemaphore.Release();
            }
            Trace.WriteLine($"========== DouyinDanmaku.Stop Done ==========");
        }

        private async Task SendHeartBeatDataAsync()
        {
            var obj = new PushFrame();
            obj.payloadType = "hb";
            await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                ws?.Send(SerializeProto(obj));
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        private async Task SendACKDataAsync(ulong logId, string internalExt)
        {
            if (string.IsNullOrEmpty(internalExt))
            {
                return;
            }
            var obj = new PushFrame
            {
                logId = logId,
                payloadType = internalExt
            };
            await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                ws?.Send(SerializeProto(obj));
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        public static byte[] GzipDecompress(byte[] bytes)
        {
            using (var memoryStream = new MemoryStream(bytes))
            {
                using (var outputStream = new MemoryStream())
                {
                    using (var decompressStream = new GZipStream(memoryStream, CompressionMode.Decompress))
                    {
                        decompressStream.CopyTo(outputStream);
                    }
                    return outputStream.ToArray();
                }
            }
        }

        private static byte[] SerializeProto(object obj)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    Serializer.Serialize(ms, obj);
                    var buffer = ms.GetBuffer();
                    var dataBuffer = new byte[ms.Length];
                    Array.Copy(buffer, dataBuffer, ms.Length);
                    ms.Dispose();
                    return dataBuffer;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.SerializeProto] error: {ex.Message}");
                return null;
            }
        }

        private static T DeserializeProto<T>(byte[] bufferData)
        {
            using (MemoryStream ms = new MemoryStream(bufferData))
            {
                return Serializer.Deserialize<T>(ms);
            }
        }

        /// <summary>
        /// Get Websocket signature
        /// </summary>
        private async Task<string> DefaultSignatureProvider(string roomId, string uniqueId)
        {
            var signature = await DouyinSignHelper.GetSignatureAsync(roomId, uniqueId);
            Trace.WriteLine("DouyinDanmaku signature result: " + signature);
            if (!string.IsNullOrEmpty(signature) && signature != "00000000")
            {
                return signature;
            }

            var fallback = await GetSign(roomId, uniqueId);
            Trace.WriteLine("DouyinDanmaku fallback signature result: " + fallback);
            return fallback;
        }

        private async Task ConnectAsync(bool useBackup)
        {
            var targetUrl = useBackup && !string.IsNullOrEmpty(BackupUrl) ? BackupUrl : ServerUrl;
            Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] useBackup={useBackup}");
            Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] Target URL: {targetUrl?.Substring(0, Math.Min(100, targetUrl?.Length ?? 0))}...");

            await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] Acquired semaphore");
                Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] Cleaning old connection...");
                CleanupWebSocket();

                Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] Creating new WebSocket...");
                ws = new WebSocket(targetUrl);
                ws.CustomHeaders = new Dictionary<string, string>()
                {
                    {"Origin","https://live.douyin.com" },
                    {"Cookie", danmakuArgs.Cookie},
                    {"User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0" }
                };
                ws.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;

                ws.OnOpen += Ws_OnOpen;
                ws.OnError += Ws_OnError;
                ws.OnMessage += Ws_OnMessage;
                ws.OnClose += Ws_OnClose;

                timer?.Stop();
                timer?.Dispose();
                timer = new System.Timers.Timer(HeartbeatTime)
                {
                    AutoReset = true
                };
                timer.Elapsed += Timer_Elapsed;

                Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] Calling ws.Connect()...");
                ws.Connect();
                Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] ws.Connect() returned, ws.ReadyState={ws.ReadyState}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] exception: {ex.Message}");
                Trace.WriteLine($"[DouyinDanmaku.ConnectAsync] StackTrace: {ex.StackTrace}");
                CleanupWebSocket();
                HandleConnectionFailure(ex.Message);
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        private void CleanupWebSocket()
        {
            Trace.WriteLine($"[DouyinDanmaku.CleanupWebSocket] ws==null={ws == null}, timer==null={timer == null}");
            if (ws != null)
            {
                ws.OnOpen -= Ws_OnOpen;
                ws.OnError -= Ws_OnError;
                ws.OnMessage -= Ws_OnMessage;
                ws.OnClose -= Ws_OnClose;
                try
                {
                    Trace.WriteLine($"[DouyinDanmaku.CleanupWebSocket] Closing WebSocket, ReadyState={ws.ReadyState}");
                    ws.Close();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[DouyinDanmaku.CleanupWebSocket] Close exception (ignored): {ex.Message}");
                }
                ws = null;
            }

            if (timer != null)
            {
                timer.Elapsed -= Timer_Elapsed;
                timer.Stop();
                timer.Dispose();
                timer = null;
                Trace.WriteLine($"[DouyinDanmaku.CleanupWebSocket] Timer cleaned");
            }
        }

        private void HandleConnectionFailure(string reason)
        {
            Trace.WriteLine($"[DouyinDanmaku.HandleConnectionFailure] reason={reason}");
            
            if (reconnectTokenSource != null)
            {
                Trace.WriteLine($"[DouyinDanmaku.HandleConnectionFailure] Reconnect already in progress, skip");
                return;
            }

            reconnectAttempts++;
            Trace.WriteLine($"[DouyinDanmaku.HandleConnectionFailure] Reconnect attempt: {reconnectAttempts}/{MaxReconnectAttempts}");
            
            if (reconnectAttempts > MaxReconnectAttempts)
            {
                Trace.WriteLine($"[DouyinDanmaku.HandleConnectionFailure] Max reconnect attempts reached");
                CancelReconnect();
                OnClose?.Invoke(this, string.IsNullOrEmpty(reason) ? "Reconnect failed" : reason);
                return;
            }

            OnClose?.Invoke(this, $"Connection lost, reconnecting ({reconnectAttempts}/{MaxReconnectAttempts})");
            useBackupEndpoint = !useBackupEndpoint && !string.IsNullOrEmpty(BackupUrl);
            Trace.WriteLine($"[DouyinDanmaku.HandleConnectionFailure] Use backup endpoint: {useBackupEndpoint}");
            ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            CancelReconnect();
            reconnectTokenSource = new CancellationTokenSource();
            var token = reconnectTokenSource.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    if (!token.IsCancellationRequested)
                    {
                        await ConnectAsync(useBackupEndpoint);
                    }
                }
                catch (TaskCanceledException)
                {
                    // ignored
                }
            }, token);
        }

        private void CancelReconnect()
        {
            if (reconnectTokenSource != null)
            {
                reconnectTokenSource.Cancel();
                reconnectTokenSource.Dispose();
                reconnectTokenSource = null;
            }
        }

        private async Task<string> GetSign(string roomId, string uniqueId)
        {
            try
            {
                var body = JsonConvert.SerializeObject(new { roomId, uniqueId });
                var result = await HttpUtil.PostJsonString("https://dy.nsapps.cn/signature", body);
                var json = JObject.Parse(result);
                return json["data"]?["signature"]?.ToString() ?? "00000000";
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DouyinDanmaku.GetSign] error: {ex.Message}");
                return "00000000";
            }
        }
    }
}
