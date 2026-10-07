using System.Text;
using System.Text.RegularExpressions;

// usage: StringScan <file> <regex> [minLen] [maxResults]
if (args.Length < 2)
{
    Console.WriteLine("usage: StringScan <file> <regex> [minLen=6] [maxResults=400]");
    return 2;
}

string path = args[0];
var rx = new Regex(args[1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
int minLen = args.Length > 2 ? int.Parse(args[2]) : 6;
int maxResults = args.Length > 3 ? int.Parse(args[3]) : 400;

var hits = new Dictionary<string, int>(StringComparer.Ordinal);

const int Chunk = 8 * 1024 * 1024;
const int Overlap = 8192;
byte[] buf = new byte[Chunk + Overlap];
int carry = 0;
long offset = 0;

using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

while (true)
{
    int read = fs.Read(buf, carry, Chunk);
    if (read <= 0) break;
    int n = read + carry;
    long baseOffset = offset;

    // ---- ASCII runs ----
    int i = 0;
    while (i < n)
    {
        if (buf[i] >= 0x20 && buf[i] < 0x7F)
        {
            int start = i;
            while (i < n && buf[i] >= 0x20 && buf[i] < 0x7F) i++;
            int len = i - start;
            if (len >= minLen && start + len <= n - Overlap || (len >= minLen && baseOffset + start + len <= fs.Length))
            {
                var s = Encoding.ASCII.GetString(buf, start, len);
                foreach (Match m in rx.Matches(s))
                    Add(m.Value);
            }
        }
        else i++;
    }

    // ---- UTF-16LE runs ----
    i = 0;
    while (i + 1 < n)
    {
        if (buf[i] >= 0x20 && buf[i] < 0x7F && buf[i + 1] == 0)
        {
            int start = i;
            var sb = new StringBuilder();
            while (i + 1 < n && buf[i] >= 0x20 && buf[i] < 0x7F && buf[i + 1] == 0)
            {
                sb.Append((char)buf[i]);
                i += 2;
            }
            if (sb.Length >= minLen)
            {
                var s = sb.ToString();
                foreach (Match m in rx.Matches(s))
                    Add(m.Value);
            }
        }
        else i++;
    }

    offset += read;
    Array.Copy(buf, n - Math.Min(Overlap, n), buf, 0, Math.Min(Overlap, n));
    carry = Math.Min(Overlap, n);
}

Console.WriteLine($"# {path}: {hits.Count} unique matches (regex /{args[1]}/i, minLen={minLen})");
foreach (var kv in hits.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(maxResults))
    Console.WriteLine($"{kv.Value,6}  {kv.Key}");
return 0;

void Add(string s)
{
    s = s.Trim();
    if (s.Length < minLen) return;
    if (s.Length > 200) s = s[..200];
    hits.TryGetValue(s, out int c);
    hits[s] = c + 1;
}
