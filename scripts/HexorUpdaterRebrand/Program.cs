using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.Text;

// Rebrands the installed Aion2.Updater.exe to Hexor in place:
//   1. Language resources and BAML display strings: every "aion.onl" brand string is replaced with
//      a Hexor/Hexor Services string of identical byte length (in-place, length-preserving), so
//      bundle offsets and sizes stay valid. Connections, endpoints, and the card-key auth flow are
//      not touched.
//   2. aion.onl web links: every visible user.aion.onl / t.me URL (XOR string blob, UTF-16
//      metadata strings, BAML) is replaced in place with https://hexor.wtf links of identical
//      byte length. Functional endpoints (auth.aion.mom card auth, download.aion.onl CDN) are
//      intentionally kept so updates and card verification keep working.
//   3. Self-replacement disable: the startup block that compares the updater's own file against the
//      server version and replaces its own EXE is gated by `if (!Debugger.IsAttached)`. The branch
//      after that call is made unconditional so the block is always skipped. The post-login game
//      component update flow is a different code path and keeps working.
//   4. Auto-close after update: the completion path that reveals the vendor launch panel
//      (this._2000.Visibility = Visibility.Visible) instead closes the updater window, so once
//      an update is done the updater exits instead of leading into LegionCommander.
// The patcher only edits bytes inside the embedded managed DLL of the single-file bundle and never
// changes any file or frame length.

internal static class Program
{
    private static int failed;

    private static bool Check(bool ok, string message)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + message);
        if (!ok) failed++;
        return ok;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: HexorUpdaterRebrand <updater.exe> <output.exe>");
            return 2;
        }
        var source = args[0];
        var target = args[1];
        var data = File.ReadAllBytes(source);
        var patched = (byte[])data.Clone();

        Probe.Run(data, direct: args.Length > 2 && args[2] == "--direct");
        ApplyStringPatches(patched);
        ApplyLinkPatches(patched);
        DisableSelfReplacement(patched, out var dllBase, out var dllEnd);
        AutoCloseAfterUpdate(patched, dllBase, dllEnd);
        // ShowExceptionInCatch(patched);  // debug-only

        Check(patched.Length == data.Length, "Patched image keeps the original size");
        File.WriteAllBytes(target, patched);
        Console.WriteLine($"Wrote {target}");
        return failed == 0 ? 0 : 1;
    }

    // ---- string rebranding ---------------------------------------------------------------

    private static readonly (byte[] From, byte[] To, string Label)[] Replacements =
    [
        (Utf8("AION.ONL UPDATER"), Utf8("HEXOR UPDATER   "), "window title / BAML / en Title"),
        (Utf8("AION.ONL 更新器"), Utf8("Hexor 更新器   "), "zh Title"),
        (Utf8("AION.ONL 업데이트 도구"), Utf8("Hexor 업데이트 도구 "), "ko Title"),
        (Utf8("Get free 3-hour trial card"), Utf8("Hexor Services notices    "), "en announcement button"),
        (Utf8("免费领3小时测试卡"), Utf8("Hexor 公告中心       "), "zh announcement button"),
        (Utf8("3시간 무료 테스트 카드 받기"), Utf8("Hexor 공지                          "), "ko announcement button"),
        (Utf8("Official group"), Utf8("Hexor Services"), "en community button"),
        (Utf8("官方交流群"), Utf8("Hexor          "), "zh community button"),
        (Utf8("공식 커뮤니티"), Utf8("Hexor             "), "ko community button"),
    ];

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static void ApplyStringPatches(byte[] image)
    {
        foreach (var (from, rawTo, label) in Replacements)
        {
            var to = rawTo.Length < from.Length ? rawTo.Concat(Enumerable.Repeat((byte)0x20, from.Length - rawTo.Length)).ToArray() : rawTo;
            if (!Check(to.Length == from.Length, $"{label}: replacement keeps {from.Length} bytes")) { Console.WriteLine($"SKIP {label}: length mismatch would corrupt the image"); continue; }
            int count = 0;
            int index = 0;
            while ((index = IndexOf(image, from, index)) >= 0)
            {
                Array.Copy(to, 0, image, index, to.Length);
                count++;
                index += to.Length;
            }
            Check(count > 0, $"{label}: {count} occurrence(s) patched");
        }
        // No unpatched brand strings remain in plain storage.
        Check(IndexOf(image, Utf8("AION.ONL"), 0) < 0, "No plain AION.ONL title strings remain");
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    // ---- aion.onl link removal -------------------------------------------------------------

    // The obfuscated string blob XOR-encodes each byte with (blobOffset ^ 0xAA). The notice and
    // community URLs live at blob offsets 3304 (https://user.aion.onl, 21 chars) and 3325
    // (https://t.me/aionOnl, 20 chars). Replacements are the same length, so the accessors that
    // read fixed (offset, length) windows keep decoding valid URLs.
    private const int BlobNoticeOffset = 3304;
    private const string NoticeUrlPlain = "https://user.aion.onl";
    private const string NoticeUrlHexor = "https://hexor.wtf/?ab";
    private const string CommunityUrlPlain = "https://t.me/aionOnl";
    private const string CommunityUrlHexor = "https://hexor.wtf/?a";

    private static void ApplyLinkPatches(byte[] image)
    {
        // XOR blob: find the blob base by decoding candidates, then patch both windows.
        var notice = Utf8(NoticeUrlPlain);
        var baseOffset = -1;
        for (var candidate = 0; candidate + BlobNoticeOffset + notice.Length < image.Length; candidate++)
        {
            var ok = true;
            for (var k = 0; k < notice.Length; k++)
            {
                if (image[candidate + BlobNoticeOffset + k] != (byte)(notice[k] ^ ((BlobNoticeOffset + k) & 0xFF) ^ 0xAA)) { ok = false; break; }
            }
            if (ok) { baseOffset = candidate; break; }
        }
        Check(baseOffset > 0, $"Obfuscated string blob base located at {baseOffset}");
        if (baseOffset > 0)
        {
            PatchBlobWindow(image, baseOffset, BlobNoticeOffset, NoticeUrlHexor, "blob notice URL");
            PatchBlobWindow(image, baseOffset, BlobNoticeOffset + NoticeUrlPlain.Length, CommunityUrlHexor, "blob community URL");
        }

        // Plain UTF-16 strings (metadata US heap + resources).
        PatchUtf16(image, "https://user.aion.onl/api/announcements/public/recent", "https://hexor.wtf/api/announcements/public/recent?xyz", "announcements API URL");
        PatchUtf16(image, NoticeUrlPlain, NoticeUrlHexor, "standalone notice URL");
        PatchUtf16(image, CommunityUrlPlain, CommunityUrlHexor, "UTF-16 community URL");
        // BAML stores the community link as raw ASCII.
        PatchPlain(image, Utf8(CommunityUrlPlain), Utf8(CommunityUrlHexor), "BAML community URL");

        // No user-visible aion.onl traces remain in any storage form.
        Check(IndexOf(image, Utf16("user.aion.onl"), 0) < 0, "No UTF-16 user.aion.onl remains");
        Check(IndexOf(image, Utf8("user.aion.onl"), 0) < 0, "No ASCII user.aion.onl remains");
        Check(IndexOf(image, Utf8("t.me/aionOnl"), 0) < 0 && IndexOf(image, Utf16("t.me/aionOnl"), 0) < 0, "No t.me/aionOnl remains");
        Check(baseOffset <= 0 || DecodeBlobWindow(image, baseOffset, BlobNoticeOffset, NoticeUrlPlain.Length) == NoticeUrlHexor, "Blob notice URL reads the Hexor link after patching");
        Check(baseOffset <= 0 || DecodeBlobWindow(image, baseOffset, BlobNoticeOffset + NoticeUrlPlain.Length, CommunityUrlPlain.Length) == CommunityUrlHexor, "Blob community URL reads the Hexor link after patching");
    }

    private static void PatchBlobWindow(byte[] image, int blobBase, int blobOffset, string replacement, string label)
    {
        var bytes = Utf8(replacement);
        for (var k = 0; k < bytes.Length; k++)
            image[blobBase + blobOffset + k] = (byte)(bytes[k] ^ ((blobOffset + k) & 0xFF) ^ 0xAA);
        Check(true, $"{label}: patched at blob offset {blobOffset} ({bytes.Length} bytes)");
    }

    private static string DecodeBlobWindow(byte[] image, int blobBase, int blobOffset, int length)
    {
        var bytes = new byte[length];
        for (var k = 0; k < length; k++) bytes[k] = (byte)(image[blobBase + blobOffset + k] ^ ((blobOffset + k) & 0xFF) ^ 0xAA);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] Utf16(string value) => Encoding.Unicode.GetBytes(value);

    private static void PatchUtf16(byte[] image, string from, string to, string label)
    {
        var needle = Utf16(from);
        var replacement = Utf16(to);
        Check(needle.Length == replacement.Length, $"{label}: replacement keeps {needle.Length} bytes");
        var count = 0;
        var index = 0;
        while ((index = IndexOf(image, needle, index)) >= 0)
        {
            Array.Copy(replacement, 0, image, index, replacement.Length);
            count++;
            index += replacement.Length;
        }
        Check(count > 0, $"{label}: {count} occurrence(s) patched");
    }

    private static void PatchPlain(byte[] image, byte[] from, byte[] to, string label)
    {
        var count = 0;
        var index = 0;
        while ((index = IndexOf(image, from, index)) >= 0)
        {
            Array.Copy(to, 0, image, index, to.Length);
            count++;
            index += to.Length;
        }
        Check(count > 0, $"{label}: {count} occurrence(s) patched");
    }

    // ---- auto-close after update -------------------------------------------------------------

    // The completion path shows the vendor launch panel:
    //   this._1680.Visibility = Visibility.Collapsed;   // 02 18 6F <set_Visibility>
    //   this._2000.Visibility = Visibility.Visible;     // 02 16 6F <set_Visibility>
    //   ... DispatcherTimer.Start();
    // The Visible assignment is replaced with ldarg.0 + callvirt Window.Close (+nop pad), so
    // finishing an update closes the updater instead of revealing the panel that leads into
    // LegionCommander. Only methods that also start the completion DispatcherTimer are patched.
    private static void AutoCloseAfterUpdate(byte[] image, int dllBase, int dllEnd)
    {
        // Replace the completion reveal `ldfld panel; ldc.i4.0(Visible); callvirt set_Visibility`
        // with `dup; callvirt Window.Close; pop`. The window starts at the ldfld that pushes the
        // panel, so the stack below is untouched: the pusher instruction is left in place, dup
        // duplicates the MainWindow reference, Close consumes one copy and pop discards the other.
        if (dllBase < 0) { Check(false, "Auto-close skipped: no embedded DLL"); return; }
        using var slice = new MemoryStream(image, dllBase, dllEnd - dllBase, writable: false);
        using var pe = new PEReader(slice);
        var reader = pe.GetMetadataReader();

        var closeToken = FindMemberRef(reader, "System.Windows", "Window", "Close", instance: true);
        var startToken = FindMemberRef(reader, "System.Windows.Threading", "DispatcherTimer", "Start", instance: true);
        var visibilityToken = FindMemberRef(reader, "System.Windows", "", "set_Visibility", instance: true);
        Check(closeToken != 0 && startToken != 0 && visibilityToken != 0, "Window.Close, DispatcherTimer.Start and set_Visibility member references resolved");
        if (closeToken == 0 || startToken == 0 || visibilityToken == 0) return;

        var rvaBase = RvaToFileOffsetBase(pe);
        // dup; callvirt Window.Close; pop; nop*4  (11 bytes, replaces ldfld(5)+ldc.i4.0(1)+callvirt(5))
        var closeBytes = new byte[] { 0x25, 0x6F, (byte)(closeToken & 0xFF), (byte)((closeToken >> 8) & 0xFF), (byte)((closeToken >> 16) & 0xFF), (byte)((closeToken >> 24) & 0xFF), 0x26, 0x00, 0x00, 0x00, 0x00 };

        var patched = 0;
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILBytes();
            if (!ContainsCallToken(il, startToken)) continue;

            int? target = null;
            for (var d = 0; d + 5 <= il.Length; d++)
            {
                if (il[d] != 0x6F || BitConverter.ToInt32(il, d + 1) != visibilityToken) continue;
                if (d < 12) continue;
                if (il[d - 1] != 0x16 || il[d - 6] != 0x7B) continue; // Visible reveal on a loaded control field
                if (il[d + 5] == 0xDD) continue; // try-block progress update that leaves immediately
                if (!ContainsCallToken(il, startToken, from: d)) continue;
                var collapsedBefore = false;
                for (var c = Math.Max(0, d - 25); c + 5 <= d - 1; c++)
                    if (il[c] == 0x6F && BitConverter.ToInt32(il, c + 1) == visibilityToken && il[c - 1] == 0x18) { collapsedBefore = true; break; }
                if (!collapsedBefore) continue;
                target = d; // keep the last match: the end-of-method completion reveal
            }
            if (target is not int site) continue;
            var windowStart = site - 6;
            var fileOffset = FileOffsetFor(rvaBase, dllBase, method.RelativeVirtualAddress, image, windowStart, il.Length);
            for (var k = 0; k < closeBytes.Length; k++) image[fileOffset + k] = closeBytes[k];
            patched++;
        }
        Check(patched >= 1, $"Auto-close patched at {patched} completion site(s): the updater closes after an update instead of showing the launch panel");
    }

    private static bool ContainsAt(byte[] il, int offset, byte[] pattern)
    {
        if (offset + pattern.Length > il.Length) return false;
        for (var k = 0; k < pattern.Length; k++)
            if (il[offset + k] != pattern[k]) return false;
        return true;
    }

    // ---- self-replacement disable ---------------------------------------------------------

    // Locates the embedded x64 managed DLL (Aion2.Updater.dll) inside the single-file bundle.
    private static void DisableSelfReplacement(byte[] image, out int dllBase, out int dllEnd)
    {
        // NOTE: we no longer flip the guarded `brtrue` to `br`. The CLR refuses to JIT the
        // rewritten async state machine (InvalidProgramException), which surfaced as the
        // updater's "connection failing" retry loop. Instead we neutralise the self-update
        // block with two single-byte constant flips inside straight-line code:
        //   IL_006f  ldc.i4.1  (requireSuccessfulCheck)  -> ldc.i4.0   : a failed update check
        //                                                        returns null instead of throwing.
        //   IL_0143  ldc.i4.1  (md5 mismatch flag)       -> ldc.i4.0   : the updater never
        //                                                        downloads or replaces itself.
        dllBase = -1;
        dllEnd = -1;
        LocateEmbeddedDll(image, out dllBase, out dllEnd);
        if (dllBase < 0) { Check(false, "embedded DLL located"); return; }

        var isAttachedToken = FindMemberRef(ReadMetadata(image, dllBase, dllEnd), "System.Diagnostics", "Debugger", "get_IsAttached");
        var shutdownToken = FindMemberRef(ReadMetadata(image, dllBase, dllEnd), "System.Windows", "Application", "Shutdown", instance: true);
        Check(isAttachedToken != 0 && shutdownToken != 0, "member refs resolved");

        int branch = -1, methodRva = -1, headerSize = 12, ilBase = -1;
        foreach (var (exeOffset, rva) in FindGuardedBranches(image, dllBase, dllEnd, isAttachedToken, shutdownToken))
        {
            branch = exeOffset; methodRva = rva;
        }
        Check(branch > 0, $"guard branch located at {branch} (left unpatched)");
        if (branch < 0) { return; }
        ilBase = branch - 90; // ilIndex 90 is the branch opcode site

        // site 1: IL_006f ldc.i4.1 before callvirt CheckForUpdatesAsync -> ldc.i4.0
        int site1 = ilBase + 111;
        Check(image[site1] == 0x17 && image[site1 + 1] == 0x6F, $"requireSuccessfulCheck site bytes valid at {site1}");
        image[site1] = 0x16;
        // site 2: IL_0143 ldc.i4.1 / stloc.s 6 -> ldc.i4.0
        int site2 = ilBase + 323;
        Check(image[site2] == 0x17 && image[site2 + 1] == 0x13 && image[site2 + 2] == 0x06, $"md5 flag site bytes valid at {site2}");
        image[site2] = 0x16;
        Check(true, "Self-update neutralised (requireSuccessfulCheck=false, md5 flag=false)");
    }

    private static void LocateEmbeddedDll(byte[] image, out int dllBase, out int dllEnd)
    {
        dllBase = -1;
        dllEnd = -1;
        for (var i = 0; i + 0x40 + 0x400 < image.Length; i++)
        {
            if (image[i] != 'M' || image[i + 1] != 'Z') continue;
            var lfanew = BitConverter.ToInt32(image, i + 0x3c);
            if (lfanew <= 0 || lfanew > 0x400 || image[i + lfanew] != 'P' || image[i + lfanew + 1] != 'E') continue;
            var opt = i + lfanew + 24;
            if (BitConverter.ToUInt16(image, opt) != 0x20b || BitConverter.ToInt32(image, opt + 16) != 0) continue;
            if (BitConverter.ToInt32(image, opt + 112 + 14 * 8) == 0) continue;
            var nsec = BitConverter.ToUInt16(image, i + lfanew + 6);
            var secoff = opt + 240;
            var end = i;
            for (var s = 0; s < nsec; s++)
            {
                var rawSize = BitConverter.ToInt32(image, secoff + 16);
                var rawPtr = BitConverter.ToInt32(image, secoff + 20);
                end = Math.Max(end, i + rawPtr + rawSize);
                secoff += 40;
            }
            if (dllBase < 0 || end - i > dllEnd - dllBase) { dllBase = i; dllEnd = end; }
        }
    }

    private static MetadataReader ReadMetadata(byte[] image, int dllBase, int dllEnd)
    {
        var slice = new MemoryStream(image, dllBase, dllEnd - dllBase, writable: false);
        var pe = new PEReader(slice);
        return pe.GetMetadataReader();
    }

    private static List<(int ExeOffset, int MethodRva)> FindGuardedBranches(byte[] image, int dllBase, int dllEnd, int isAttachedToken, int shutdownToken)
    {
        var candidates = new List<(int, int)>();
        using var slice = new MemoryStream(image, dllBase, dllEnd - dllBase, writable: false);
        using var pe = new PEReader(slice);
        var reader = pe.GetMetadataReader();
        var rvaBase = RvaToFileOffsetBase(pe);
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILBytes();
            if (shutdownToken == 0 || !ContainsCallToken(il, shutdownToken)) continue;
            for (var i = 0; i + 6 <= il.Length; i++)
            {
                if (il[i] != 0x28 || BitConverter.ToInt32(il, i + 1) != isAttachedToken) continue;
                var branch = il[i + 5];
                if (branch is 0x2D or 0x3A)
                    candidates.Add((dllBase + rvaBase + method.RelativeVirtualAddress + BodyHeaderSize(image, dllBase + rvaBase + method.RelativeVirtualAddress) + i + 5, method.RelativeVirtualAddress));
            }
        }
        return candidates;
    }

    private static int BodyHeaderSize(byte[] image, int pos)
    {
        var b = image[pos];
        return (b & 3) == 2 ? b >> 2 : 12;
    }

    private static int RvaToFileOffsetBase(PEReader pe)
    {
        foreach (var section in pe.PEHeaders.SectionHeaders)
            if (section.VirtualAddress <= 0x2000)
                return section.PointerToRawData - section.VirtualAddress;
        return 0;
    }

    private static int FileOffsetFor(int rvaBase, int dllBase, int methodRva, byte[] image, int ilIndex, int ilLength)
    {
        // sliceOffset = rvaBase + methodRva + methodHeaderSize + ilIndex. The method body header is
        // read directly: bit0..1 == 2 marks a tiny header (size in the upper 6 bits), otherwise fat (12).
        var header = image[dllBase + rvaBase + methodRva];
        var headerSize = (header & 3) == 2 ? header >> 2 : 12;
        return dllBase + rvaBase + methodRva + headerSize + ilIndex;
    }

    private static bool ContainsCallToken(byte[] il, int token, int from = 0)
    {
        for (var i = from; i + 5 <= il.Length; i++)
            if ((il[i] is 0x28 or 0x6F) && BitConverter.ToInt32(il, i + 1) == token)
                return true;
        return false;
    }

    private static int FindMemberRef(MetadataReader reader, string typeNamespace, string typeName, string memberName, bool instance = false)
    {
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (reader.GetString(member.Name) != memberName) continue;
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            var ns = reader.GetString(type.Namespace);
            var name = reader.GetString(type.Name);
            var nsMatch = ns == typeNamespace || (typeNamespace.Contains('.') && ns == typeNamespace);
            var nameMatch = typeName.Length == 0 || name == typeName;
            if (nsMatch && nameMatch) return MetadataTokens.GetToken(handle);
        }
        return 0;
    }

    private static void ShowExceptionInCatch(byte[] image)
    {
        int dllBase = -1, dllEnd = -1;
        var data = image;
        for (var i = 0x40; i < data.Length - 0x440; i++)
        {
            if (data[i] != 0x4D || data[i + 1] != 0x5A) continue;
            int lfanew = BitConverter.ToInt32(data, i + 0x3c);
            if (lfanew <= 0 || lfanew > 0x400 || data[i + lfanew] != 'P' || data[i + lfanew + 1] != 'E') continue;
            int opt = i + lfanew + 24;
            if (BitConverter.ToUInt16(data, opt) != 0x20b || BitConverter.ToInt32(data, opt + 16) != 0) continue;
            int corRva = BitConverter.ToInt32(data, opt + 112 + 14 * 8);
            if (corRva == 0) continue;
            int nsec = BitConverter.ToUInt16(data, i + lfanew + 6);
            int secoff = opt + 240; int end = i;
            for (int s = 0; s < nsec; s++) { int rs = BitConverter.ToInt32(data, secoff + 16); int rp = BitConverter.ToInt32(data, secoff + 20); end = Math.Max(end, i + rp + rs); secoff += 40; }
            if (dllBase < 0 || end - i > dllEnd - dllBase) { dllBase = i; dllEnd = end; }
        }
        Check(dllBase > 0, $"DLL at {dllBase}..{dllEnd}");
        using var slice = new MemoryStream(data, dllBase, dllEnd - dllBase, writable: false);
        using var pe = new PEReader(slice);
        var reader = pe.GetMetadataReader();
        int drlToken = 0, excToken = 0;
        foreach (var h in reader.TypeReferences)
        {
            var t = reader.GetTypeReference(h);
            string ns = reader.GetString(t.Namespace), nm = reader.GetString(t.Name);
            if (nm == "DownloadRateLimitException" && ns.EndsWith("Services")) drlToken = MetadataTokens.GetToken(h);
            if (nm == "Exception" && ns == "System") excToken = MetadataTokens.GetToken(h);
        }
        foreach (var h in reader.TypeDefinitions)
        {
            var t = reader.GetTypeDefinition(h);
            string ns = reader.GetString(t.Namespace), nm = reader.GetString(t.Name);
            if (nm == "DownloadRateLimitException" && ns.EndsWith("Services")) drlToken = MetadataTokens.GetToken(h);
        }
        Check(drlToken != 0 && excToken != 0, $"tokens drl=0x{drlToken:x} exc=0x{excToken:x}");
        var rvaBase = RvaToFileOffsetBase(pe);
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILBytes();
            for (int k = 0; k + 5 <= il.Length; k++)
            {
                if (il[k] != 0x75 || BitConverter.ToInt32(il, k + 1) != drlToken) continue;
                int bodyStart = dllBase + rvaBase + method.RelativeVirtualAddress;
                var hb = image[bodyStart];
                int headerSize = (hb & 3) == 2 ? hb >> 2 : 12;
                int off = bodyStart + headerSize + k;
                BitConverter.GetBytes(excToken).CopyTo(image, off + 1);
                Check(image[off] == 0x75, $"isinst token patched at {off}");
                Console.WriteLine($"PASS isinst DownloadRateLimitException -> System.Exception at {off}");
            }
        }
    }

}
