using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Il2CppInterop.Runtime.CatHelpers
{
    internal class Hashing
    {
        public static string CCPath = Path.Combine(Directory.GetCurrentDirectory(), "CCData");
        public static string CachedFileHash => File.Exists(Path.Combine(CCPath, "Hash.txt")) ? File.ReadAllText(Path.Combine(CCPath, "Hash.txt")) : string.Empty;
        public static string GetMD5Hash(string filename)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(filename))
                {
                    return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
            }
        }
    }
}
