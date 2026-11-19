using System.Reflection.Metadata;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;

// Read-only offset probe for Aion2.Updater.exe. Reports where the strings the rebrand patches
// live (obfuscated XOR string blob, UTF-16 metadata strings, BAML) without modifying anything.

internal static class Probe
{
    public static void Run(byte[] data, bool direct)
    {
        Console.WriteLine($"image: {data.Length} bytes");
        Report(data, "utf16 https://user.aion.onl", Encoding.Unicode.GetBytes("https://user.aion.onl"));
        Report(data, "utf16 announcements API", Encoding.Unicode.GetBytes("https://user.aion.onl/api/announcements/public/recent"));
        Report(data, "utf16 t.me", Encoding.Unicode.GetBytes("https://t.me/aionOnl"));
        Report(data, "ascii t.me (BAML)", Encoding.UTF8.GetBytes("https://t.me/aionOnl"));
        Report(data, "utf16 auth.aion.mom/api (kept)", Encoding.Unicode.GetBytes("https://auth.aion.mom/api"));
        Report(data, "utf16 download.aion.onl (kept)", Encoding.Unicode.GetBytes("download.aion.onl"));
        var blobBase = FindBlobBase(data);
        Console.WriteLine(blobBase > 0
            ? $"XOR string blob base: {blobBase} (notice URL at {blobBase + 3304}, community URL at {blobBase + 3325})"
            : "XOR string blob base: not found");
        Console.WriteLine($"AION.ONL plain title occurrences: {Count(data, Encoding.Unicode.GetBytes("AION.ONL"))}");
        Console.WriteLine($"AION.ONL utf8 title occurrences: {Count(data, Encoding.UTF8.GetBytes("AION.ONL"))}");
        ListFields(data);
    }

    private static void ListFields(byte[] data)
    {
        var (bestStart, bestEnd) = (-1, -1);
        for (var i = 0; i + 0x40 + 0x400 < data.Length; i++)
        {
            if (data[i] != (byte)'M' || data[i + 1] != (byte)'Z') continue;
            var lfanew = BitConverter.ToInt32(data, i + 0x3c);
            if (lfanew is <= 0 or >= 0x400 || i + lfanew + 4 > data.Length) continue;
            if (data[i + lfanew] != (byte)'P' || data[i + lfanew + 1] != (byte)'E') continue;
            var opt = i + lfanew + 24;
            if (BitConverter.ToUInt16(data, opt) != 0x20b) continue;
            if (BitConverter.ToUInt32(data, opt + 16) != 0) continue;
            var corRva = BitConverter.ToUInt32(data, opt + 112 + 14 * 8);
            if (corRva == 0) continue;
            var nsec = BitConverter.ToUInt16(data, i + lfanew + 6);
            var secoff = opt + 240;
            var end = i;
            for (var s = 0; s < nsec; s++)
            {
                end = Math.Max(end, i + BitConverter.ToInt32(data, secoff + 20) + BitConverter.ToInt32(data, secoff + 16));
                secoff += 40;
            }
            if (bestStart < 0 || end - i > bestEnd - bestStart) (bestStart, bestEnd) = (i, end);
        }
        if (bestStart < 0) { Console.WriteLine("no embedded managed DLL found"); return; }
        {
            var i = bestStart;
            using var slice = new MemoryStream(data, i, bestEnd - i, writable: false);
            PEReader? pe = null;
            try { pe = new PEReader(slice); } catch { return; }
            using (pe)
            {
                var reader = pe.GetMetadataReader();
                int envTarget;
                if (Environment.GetEnvironmentVariable("BRANCH_OFFSET") is string t && int.TryParse(t, out envTarget))
                {
                    var rvaBase0 = RvaBase(pe);
                    foreach (var mh in reader.MethodDefinitions)
                    {
                        var m = reader.GetMethodDefinition(mh);
                        if (m.RelativeVirtualAddress == 0) continue;
                        var body = pe.GetMethodBody(m.RelativeVirtualAddress);
                        var start = rvaBase0 + m.RelativeVirtualAddress;
                        var ilLen = body.GetILBytes().Length;
                        var headerSize = (data[i + start] & 3) == 2 ? data[i + start] >> 2 : 12;
                        var ilStart = start + headerSize;
                        if (envTarget >= i + ilStart && envTarget < i + ilStart + ilLen)
                        {
                            var declaring = reader.GetString(reader.GetTypeDefinition(m.GetDeclaringType()).Name);
                            Console.WriteLine($"offset {envTarget} is in method rva={m.RelativeVirtualAddress} type={declaring} ilIndex={envTarget - i - ilStart}");
                        }
                    }
                    return;
                }
                var typeFields = reader.TypeDefinitions
                    .Select(h => reader.GetTypeDefinition(h))
                    .SelectMany(t => t.GetFields())
                    .Select(fh => reader.GetFieldDefinition(fh))
                    .Select(f => reader.GetString(f.Name))
                    .Where(n => n.Contains("2000") || n.Contains("1680") )
                    .Distinct().Take(60).ToList();
                var seen = new Dictionary<int, List<int>>();
                foreach (var mh in reader.MethodDefinitions)
                {
                    var m = reader.GetMethodDefinition(mh);
                    if (m.RelativeVirtualAddress == 0) continue;
                    byte[] il;
                    try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
                    for (var x = 0; x + 7 <= il.Length; x++)
                    {
                        if (il[x] != 0x02 || (il[x + 1] != 0x16 && il[x + 1] != 0x18) || il[x + 2] != 0x7E) continue;
                        var tok = BitConverter.ToInt32(il, x + 3);
                        if (!seen.TryGetValue(tok, out var list)) seen[tok] = list = new List<int>();
                        list.Add(il[x + 1]);
                    }
                }
                foreach (var pair in seen)
                {
                    var field = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(pair.Key & 0xFFFFFF));
                    var name = reader.GetString(field.Name);
                    var escaped = string.Concat(name.Select(c => c < 0x20 || c > 0x7e ? "u" + ((int)c).ToString("x4") : c.ToString()));
                    Console.WriteLine("stfld 0x" + pair.Key.ToString("X8") + " name=" + escaped + " values=[" + string.Join(",", pair.Value) + "]");
                }
                var methods = reader.MethodDefinitions.Count();
                Console.WriteLine($"method count: {methods}");
                return;
            }
        }
        Console.WriteLine("no embedded managed DLL found");
    }

    private static int RvaBase(PEReader pe)
    {
        foreach (var section in pe.PEHeaders.SectionHeaders)
            if (section.VirtualAddress <= 0x2000)
                return section.PointerToRawData - section.VirtualAddress;
        return 0;
    }

    private static int FindBlobBase(byte[] data)
    {
        var notice = Encoding.UTF8.GetBytes("https://user.aion.onl");
        const int blobOffset = 3304;
        for (var candidate = 0; candidate + blobOffset + notice.Length < data.Length; candidate++)
        {
            var ok = true;
            for (var k = 0; k < notice.Length; k++)
            {
                if (data[candidate + blobOffset + k] != (byte)(notice[k] ^ ((blobOffset + k) & 0xFF) ^ 0xAA)) { ok = false; break; }
            }
            if (ok) return candidate;
        }
        return -1;
    }

    private static void Report(byte[] data, string label, byte[] needle)
    {
        var offsets = new List<int>();
        var index = 0;
        while ((index = IndexOf(data, needle, index)) >= 0) { offsets.Add(index); index += needle.Length; }
        Console.WriteLine($"{label}: {(offsets.Count == 0 ? "none" : string.Join(", ", offsets))}");
    }

    private static int Count(byte[] data, byte[] needle)
    {
        var count = 0;
        var index = 0;
        while ((index = IndexOf(data, needle, index)) >= 0) { count++; index += needle.Length; }
        return count;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
