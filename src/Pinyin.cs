using System.Collections.Generic;
using System.Text;

namespace ChineseCanFly.ItemFinder
{
    internal static class Pinyin
    {
        private static readonly Encoding Gb;
        private static readonly Dictionary<char, char> Fallback = new Dictionary<char, char>();

        static Pinyin()
        {
            try { Gb = Encoding.GetEncoding(936); }
            catch { Gb = null; }

            // Small fallback used only when the GB2312 code page is unavailable.
            string[] pairs =
            {
                "铁t","锭d","矿k","木m","石s","皮p","肉r","骨g","线x","布b","银y","铜t","锡x","金j","黑h","青q",
                "食s","材c","料l","箭j","弓g","剑j","斧f","锤c","盾d","甲j","药y","麦m","米m","种z","血x","龙l",
                "狼l","熊x","猪z","鸡j","蛋d","鱼y","虾x","蟹x","果g","莓m","菇g","蜜m","酒j","面m","包b","油y",
                "盐y","糖t","香x","玉y","晶j","碎s","片p","头t","盔k","裤k","靴x","手s","披p","风f","火h","冰b",
                "毒d","灵l","魔m","法f","杖z","石s","砖z","板b","条t","钉d","锁s","钥y","箱x","柜g","桶t","瓶p"
            };
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Length == 2) Fallback[pairs[i][0]] = pairs[i][1];
            }
        }

        public static string Initials(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 128)
                {
                    if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                    continue;
                }

                char initial = Resolve(c);
                if (initial != '\0') sb.Append(initial);
            }
            return sb.ToString();
        }

        private static char Resolve(char c)
        {
            if (Gb != null)
            {
                try
                {
                    byte[] bytes = Gb.GetBytes(new[] { c });
                    if (bytes.Length == 2 && bytes[0] >= 0xB0)
                    {
                        char mapped = FromGbRange(bytes[0], bytes[1]);
                        if (mapped != '\0') return mapped;
                    }
                }
                catch
                {
                    // fall through to the small table
                }
            }

            char hit;
            if (Fallback.TryGetValue(c, out hit)) return hit;
            return '\0';
        }

        private static char FromGbRange(byte high, byte low)
        {
            int code = (high << 8) | low;
            if (code >= 0xB0A1 && code <= 0xB0C4) return 'a';
            if (code >= 0xB0C5 && code <= 0xB2C0) return 'b';
            if (code >= 0xB2C1 && code <= 0xB4ED) return 'c';
            if (code >= 0xB4EE && code <= 0xB6E9) return 'd';
            if (code >= 0xB6EA && code <= 0xB7A1) return 'e';
            if (code >= 0xB7A2 && code <= 0xB8C0) return 'f';
            if (code >= 0xB8C1 && code <= 0xB9FD) return 'g';
            if (code >= 0xB9FE && code <= 0xBBF6) return 'h';
            if (code >= 0xBBF7 && code <= 0xBFA5) return 'j';
            if (code >= 0xBFA6 && code <= 0xC0AB) return 'k';
            if (code >= 0xC0AC && code <= 0xC2E7) return 'l';
            if (code >= 0xC2E8 && code <= 0xC4C2) return 'm';
            if (code >= 0xC4C3 && code <= 0xC5B5) return 'n';
            if (code >= 0xC5B6 && code <= 0xC5BD) return 'o';
            if (code >= 0xC5BE && code <= 0xC6D9) return 'p';
            if (code >= 0xC6DA && code <= 0xC8BA) return 'q';
            if (code >= 0xC8BB && code <= 0xC8F5) return 'r';
            if (code >= 0xC8F6 && code <= 0xCBF9) return 's';
            if (code >= 0xCBFA && code <= 0xCDD9) return 't';
            if (code >= 0xCDDA && code <= 0xCEF3) return 'w';
            if (code >= 0xCEF4 && code <= 0xD1B8) return 'x';
            if (code >= 0xD1B9 && code <= 0xD4D0) return 'y';
            if (code >= 0xD4D1 && code <= 0xD7F9) return 'z';
            return '\0';
        }
    }
}
