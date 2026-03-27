using AllLive.Core.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AllLive.Core.Helper;
using WebSocketSharp;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace AllLive.UWP.Helper
{
    public enum LiveSite
    {
        Bilibili=0,
        Douyu=1,
        Huya=2,
        Douyin=3,
        Unknown=99,
    }
    public class SiteParser
    {
        private const int MaxRedirectDepth = 5;

        public static async Task<(LiveSite, string)> ParseUrl(string url)
        {
            return await ParseUrl(url, 0);
        }

        private static async Task<(LiveSite, string)> ParseUrl(string url, int redirectDepth)
        {
            LiveSite site= LiveSite.Unknown;
            var roomId = "";
            if (string.IsNullOrWhiteSpace(url))
            {
                return (LiveSite.Unknown, "");
            }
            if (redirectDepth > MaxRedirectDepth)
            {
                Trace.WriteLine($"[SiteParser] 短链跳转层数超过上限，url={url}");
                return (LiveSite.Unknown, "");
            }
            if (url.Contains("bilibili.com"))
            {
                roomId = url.MatchText(@"bilibili\.com/([\d\w]+)", "");
                site = LiveSite.Bilibili;
            }
            if (url.Contains("b23.tv"))
            {
                var btvReg = new Regex("https?:\\/\\/b23.tv\\/[0-9a-z-A-Z]+");
                var u = btvReg.Match(url)?.Value;
                if (string.IsNullOrEmpty(u))
                {
                    return (LiveSite.Unknown, "");
                }
                var location = await GetLocation(u);
                if (string.IsNullOrEmpty(location) || location == u)
                {
                    Trace.WriteLine($"[SiteParser] b23.tv 短链展开失败，url={u}");
                    return (LiveSite.Unknown, "");
                }
                return await ParseUrl(location, redirectDepth + 1);
            }

            if (url.Contains("douyu.com"))
            {
                roomId = url.MatchText(@"douyu\.com/([\d\w]+)", "");
                site = LiveSite.Douyu;
            }
            if (url.Contains("huya.com"))
            {
                roomId = url.MatchText(@"huya\.com/([\d\w]+)", "");
                site = LiveSite.Huya;
            }
            if (url.Contains("live.douyin.com"))
            {
                roomId = url.MatchText(@"live\.douyin\.com/([\d\w]+)", "");
                site = LiveSite.Douyin;
            }
            if (url.Contains("webcast.amemv.com"))
            {
                roomId = url.MatchText(@"reflow/(\d+)", "");
                site = LiveSite.Douyin;
            }
            if (url.Contains("v.douyin.com"))
            {
                var regex = new Regex("http.?://v.douyin.com/[\\d\\w]+/");
                var u = regex.Match(url)?.Value;
                if (string.IsNullOrEmpty(u))
                {
                    return (LiveSite.Unknown, "");
                }
                var location = await GetLocation(u);
                if (string.IsNullOrEmpty(location) || location == u)
                {
                    Trace.WriteLine($"[SiteParser] v.douyin.com 短链展开失败，url={u}");
                    return (LiveSite.Unknown, "");
                }
                return await ParseUrl(location, redirectDepth + 1);
            }


            return (site, roomId);
        }


        private static async Task<string> GetLocation(string url)
        {
            try
            {
                var headResp = await HttpUtil.Head(url);
                if (headResp.Headers.Location != null)
                {
                    return headResp.Headers.Location.ToString();
                }
              
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message);
            }
            return "";
        }

    }
}
