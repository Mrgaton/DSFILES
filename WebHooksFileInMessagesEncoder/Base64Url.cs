using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DSFiles
{
    public static class Base64Url
    {
        public static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).Trim('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string data) => Convert.FromBase64String(data.Replace('_', '/').Replace('-', '+').PadRight(data.Length + (4 - data.Length % 4) % 4, '='));
    }
}