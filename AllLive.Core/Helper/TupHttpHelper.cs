using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading.Tasks;
using Tup;

namespace AllLive.Core.Helper
{
    public class TupHttpHelper
    {
        private static readonly HttpClient sharedHttpClient = new HttpClient();

        private readonly string baseUrl = "";
        private readonly string servantName = "";
        private readonly string userAgent;
        private readonly Dictionary<string, string> customHeaders;

        public TupHttpHelper(string baseUrl, string servantName, string userAgent = null, Dictionary<string, string> headers = null)
        {
            this.baseUrl = baseUrl;
            this.servantName = servantName;
            this.userAgent = userAgent;
            this.customHeaders = headers;
        }

        public async Task<Resp> GetAsync<Req, Resp>(Req req, string function, Resp proxy)
        {
            Resp result = proxy;
            try
            {
                TarsUniPacket uniPacket = new TarsUniPacket();
                uniPacket.RequestId = 0;
                uniPacket.ServantName = servantName;
                uniPacket.FuncName = function;
                uniPacket.setTarsVersion(Const.PACKET_TYPE_TUP3);
                uniPacket.setTarsPacketType(Const.PACKET_TYPE_TARSNORMAL);
                uniPacket.Put("tReq", req);
                byte[] array = uniPacket.Encode();

                System.Diagnostics.Trace.WriteLine($"[TupHttpHelper.GetAsync] sending request to {baseUrl}, function: {function}, size: {array.Length}");

                using (var reqMsg = new HttpRequestMessage(HttpMethod.Post, baseUrl))
                {
                    if (!string.IsNullOrEmpty(userAgent))
                    {
                        reqMsg.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                    }
                    if (customHeaders != null)
                    {
                        foreach (var kv in customHeaders)
                        {
                            reqMsg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }
                    }
                    var reqContent = new ByteArrayContent(array);
                    reqMsg.Content = reqContent;
                    reqContent.Headers.Add("Content-Type", "application/x-wup");
                    reqContent.Headers.Add("Content-Length", array.Length.ToString());

                    using (var response = await sharedHttpClient.SendAsync(reqMsg).ConfigureAwait(false))
                    {
                        System.Diagnostics.Trace.WriteLine($"[TupHttpHelper.GetAsync] response status: {response.StatusCode}");

                        var responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                        System.Diagnostics.Trace.WriteLine($"[TupHttpHelper.GetAsync] response size: {responseBytes.Length}");

                        TarsUniPacket respPack = new TarsUniPacket();
                        respPack.Decode(responseBytes);
                        var code = respPack.Get("", 0);

                        System.Diagnostics.Trace.WriteLine($"[TupHttpHelper.GetAsync] response code: {code}");

                        result = respPack.Get<Resp>("tRsp", result);
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[TupHttpHelper.GetAsync] error: {ex.Message}");
                return result;
            }
        }




    }
}
