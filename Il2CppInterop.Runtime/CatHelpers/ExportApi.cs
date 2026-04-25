using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.CatHelpers
{
    internal class ExportApi
    {
        private static Dictionary<string, string> _exports;

        private static string _exportPath = Path.Combine(Hashing.CCPath, "Exports.txt");
        public static bool CacheExists => File.Exists(_exportPath);
        public static void LoadOrFetchExportList()
        {
            var hash = Hashing.GetMD5Hash(Path.Combine(Directory.GetCurrentDirectory(), "GameAssembly.dll"));
            if (CacheExists)
            {
                bool needFetch = !Hashing.CachedFileHash.Contains(hash);
                if (!needFetch)
                {
                    _exports = ParseExportMap(File.ReadAllText(_exportPath));
                    return;
                }
            }

            string url = $"https://catnotadog.dev/catclient/exports/getfullmap?hash={hash}";
            try
            {
                using (HttpClient hc = new HttpClient())
                {
                    // ik this looks retarded but cat originally just did result which could lock up
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var map = hc.GetStringAsync(url, cts.Token).GetAwaiter().GetResult();
                    _exports = ParseExportMap(map);
                }
                File.WriteAllText(Path.Combine(Hashing.CCPath, "Hash.txt"), hash);
            }
            catch (Exception e)
            {
                throw new Exception($"failed to fetch export list for hash {hash}, server may not have exports for this version!\n" + e);
            }
        }

        public static Dictionary<string, string> ParseExportMap(string map)
        {
            var dict = new Dictionary<string, string>();
            foreach (var mapping in map.Split(',').Select(x => x.Split(':')))
            {
                dict.Add(mapping[1], mapping[0]);
            }
            return dict;
        }

        public static string GetExportName(string cleanName)
        {
            if (string.IsNullOrEmpty(cleanName) || !_exports.ContainsKey(cleanName))
            {
                return "";
            }
            return _exports[cleanName];
        }
    }
}
