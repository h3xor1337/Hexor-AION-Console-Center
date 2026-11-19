import re, os, sys
base = sys.argv[1]
fn = os.path.join(base, "-PrivateImplementationDetails--2C156813-0E04-4980-B46F-31C67DAFBBEE-", "43260435-BBC4-44D4-82BF-591798E602C8.cs")
src = open(fn, encoding='utf-8').read()
m = re.search(r"internal static byte\[\] _4 = new byte\[\d+\]\s*\{(.*?)\};", src, re.S)
blob = bytes(int(x) for x in re.findall(r"\d+", m.group(1)))
blob = bytes((b ^ (i ^ 0xAA)) & 0xFF for i, b in enumerate(blob))
entries = re.findall(r"public static string (\S+)\(\)\s*\{\s*return _5\[(\d+)\] \?\? _6\(\d+, (\d+), (\d+)\);", src)
print("methods:", len(entries), "unique:", len({e[0] for e in entries}))
strs = {}
for name, idx, off, ln in entries:
    try:
        strs[name] = blob[int(off):int(off)+int(ln)].decode('utf-8')
    except Exception as e:
        strs[name] = "<ERR %s>" % e
callre = re.compile(r"_43260435_002DBBC4_002D44D4_002D82BF_002D591798E602C8\.(\S+?)\(\)")
count = 0
BS = chr(92)
def rep(mo):
    global count
    s = strs.get(mo.group(1))
    if s is None:
        return mo.group(0)
    count += 1
    esc = s.replace(BS, BS*2).replace('"', BS+'"').replace(chr(10), BS+'n').replace(chr(13), BS+'r').replace(chr(9), BS+'t')
    return '"' + esc + '"'
for root, dirs, files in os.walk(base):
    for f in files:
        if not f.endswith('.cs') or f == "43260435-BBC4-44D4-82BF-591798E602C8.cs":
            continue
        p = os.path.join(root, f)
        t = open(p, encoding='utf-8').read()
        nt = callre.sub(rep, t)
        if nt != t:
            open(p, 'w', encoding='utf-8').write(nt)
print("replaced calls:", count)
