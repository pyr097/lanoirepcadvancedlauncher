#!/usr/bin/env python3
"""
lan_tex_tools.py - compare L.A. Noire PC textures with the PS4 remaster's and build a
local replacement pack. Read-only on your game files; writes only to the output folder.

How it works (all verified on real archives):
  * A .big.pc / .big.ps4 file is a chain of 'sges' pages. Each page holds raw-deflate
    streams (16-byte aligned). Pages with flag 0 start a new file, pages with flag 2
    continue it. A decoded file is a 'trM#' resource pack: a main part (a bytes), then a
    VRAM part (b bytes) containing the textures.
  * PC textures are DDS (DXT1/DXT3/DXT5). PS4 textures are GNF (BC1/BC3..., tile mode 13 =
    1D thin tiling, 8x8-block micro tiles). This tool detiles them exactly.
  * The same resource pack has the same entry IDs (CRC32 of the path) on both platforms, so
    packs are paired by their ID set. Inside a pack, textures are paired by picture content.
  * For every PC texture that has a larger PS4 counterpart, the PS4 texture is written as a
    DDS file named after the CRC32 of the PC texture's top mip. A manifest lists them. A
    D3D11 plugin can then swap textures by that hash when the game uploads them.

Requires Python 3.8+ and numpy (pip install numpy). Pillow is optional (previews).

Usage
  python lan_tex_tools.py scan  "D:\\Games\\L.A. Noire\\final\\pc\\vehicles.big.pc" [--max-files 5]
  python lan_tex_tools.py build --pc "D:\\Games\\L.A. Noire\\final\\pc" --ps4 "D:\\LANoire_PS4\\final\\ps4" --out hdpack [--archives vehicles,props] [--min-corr 0.85] [--fresh]
  Runs MERGE into an existing pack, so you can build archive by archive: --archives props, then --archives props.high, ...

Only use with your own legitimately owned copies. Do not redistribute the output pack:
it is derived from game assets.
"""
import argparse, mmap, os, re, struct, sys, time, zlib, json
import numpy as np

# ----------------------------------------------------------------------------- sges / trM#

def find_pages(mm):
    """Yield (offset, n_streams, flag) for every sges page header in the file."""
    p = mm.find(b'sges\x07\x00')
    size = len(mm)
    while p != -1:
        if p + 16 <= size:
            n = struct.unpack_from('<H', mm, p + 6)[0]
            flag = struct.unpack_from('<I', mm, p + 8)[0]
            if 0 < n < 4000 and flag in (0, 2):
                yield p, n, flag
        p = mm.find(b'sges\x07\x00', p + 4)

def _deflate_at(mm, pos):
    """Try a raw-deflate stream at pos. Returns (data, consumed) or None."""
    o = zlib.decompressobj(-15)
    try:
        chunk = o.decompress(mm[pos:pos + 0x40000])
    except zlib.error:
        return None
    if not o.eof or len(chunk) < 16:
        return None
    return chunk, len(mm[pos:pos + 0x40000]) - len(o.unused_data)

def decode_page(mm, p, max_streams=None, need=None):
    """Decode the raw-deflate streams of one page (each 16-byte aligned).
    The page header is 'sges', version, n, flag, then a table whose sizes are NOT reliable;
    continuation pages can carry extra descriptor words, so the first stream is located by
    probing header lengths 0, 16, 32, 48 and 64 bytes past the table."""
    n = struct.unpack_from('<H', mm, p + 6)[0]
    base = p + ((12 + 4 * n + 15) & ~15)
    first = None
    for extra in (0, 16, 32, 48, 64):
        first = _deflate_at(mm, base + extra)
        if first:
            pos = base + extra
            break
    if first is None:
        raise ValueError('no deflate stream found at the start of the page at %#x' % p)
    out = bytearray()
    count = n if max_streams is None else min(n, max_streams)
    for i in range(count):
        r = first if i == 0 else _deflate_at(mm, pos)
        if r is None:
            raise ValueError('deflate stream %d at %#x did not decode' % (i, pos))
        chunk, used = r
        out += chunk
        pos += (used + 15) & ~15
        if need is not None and len(out) >= need:
            break
    return bytes(out)

def trm_parse(buf):
    if buf[:4] != b'trM#':
        return None
    ver, a, b = struct.unpack_from('<III', buf, 4)
    c14 = struct.unpack_from('<I', buf, 0x14)[0]
    c18 = struct.unpack_from('<I', buf, 0x18)[0]
    base, cnt = (0x18, c14) if c14 else (0x1C, c18)
    if cnt > 100000 or len(buf) < base + 12 * cnt:
        return None
    ents = [struct.unpack_from('<III', buf, base + 12 * i) for i in range(cnt)]
    return a, b, ents

RAW = 'raw'    # marker: the whole archive file is one plain trM# pack (small DLC archives)

def group_files(mm):
    """List of files; each file = list of page offsets (first page has flag 0), or RAW."""
    if mm[:4] == b'trM#':
        return [RAW]
    files = []
    for off, n, flag in find_pages(mm):
        if flag == 0 or not files:
            files.append([off])
        else:
            files[-1].append(off)
    return files

def pack_key(mm, pages):
    """(key, a, b) from the first streams only: key = sorted tuple of entry IDs."""
    head = bytes(mm[:0x40000]) if pages == RAW else decode_page(mm, pages[0], max_streams=2)
    t = trm_parse(head)
    if t is None:
        return None
    a, b, ents = t
    return tuple(sorted(e[0] for e in ents)), a, b

def decode_file(mm, pages):
    """Decode a whole pack: first page, then continuation pages until main+VRAM (a+b bytes) are complete."""
    if pages == RAW:
        buf = bytes(mm[:])
        t = trm_parse(buf)
        if t is None:
            raise ValueError('not a trM# pack')
        a, b, ents = t
        return buf[:a], buf[a:a + b], ents
    out = bytearray(decode_page(mm, pages[0]))
    t = trm_parse(bytes(out))
    if t is None:
        raise ValueError('not a trM# pack')
    a, b, ents = t
    for p in pages[1:]:
        remaining = a + b - len(out)
        if remaining <= 0:
            break
        out += decode_page(mm, p, need=remaining)
    if len(out) < a + b:
        raise ValueError('pack incomplete: have %#x of %#x bytes' % (len(out), a + b))
    return bytes(out[:a]), bytes(out[a:a + b]), ents

# ----------------------------------------------------------------------------- texture formats

def morton8(x, y):
    return (x & 1) | ((y & 1) << 1) | ((x & 2) << 1) | ((y & 2) << 2) | ((x & 4) << 2) | ((y & 4) << 3)
MT = np.array([[morton8(x, y) for x in range(8)] for y in range(8)], dtype=np.int64)

def detile_1d_thin(data, bw, bh, bpe, offset=0):
    pw = (bw + 7) // 8 * 8
    ph = (bh + 7) // 8 * 8
    n = (pw // 8) * (ph // 8) * 64
    buf = np.frombuffer(data, dtype=np.uint8, count=n * bpe, offset=offset).reshape(n, bpe)
    ys, xs = np.mgrid[0:bh, 0:bw]
    idx = ((ys // 8) * (pw // 8) + (xs // 8)) * 64 + MT[ys % 8, xs % 8]
    return buf[idx].reshape(-1)

def mip_layout(w, h, mips, bpe):
    out = []
    off = 0
    for m in range(mips):
        mw, mh = max(1, w >> m), max(1, h >> m)
        bw, bh = (mw + 3) // 4, (mh + 3) // 4
        pw, ph = (bw + 7) // 8 * 8, (bh + 7) // 8 * 8
        out.append((m, mw, mh, bw, bh, off, pw * ph * bpe))
        off += pw * ph * bpe
    return out, off

def bc1_decode(raw, bw, bh):
    b = np.frombuffer(raw, dtype=np.uint8).reshape(bh, bw, 8).astype(np.int32)
    c0 = b[..., 0] | (b[..., 1] << 8)
    c1 = b[..., 2] | (b[..., 3] << 8)
    def rgb(c):
        return np.stack([((c >> 11) & 31) * 255 // 31, ((c >> 5) & 63) * 255 // 63, (c & 31) * 255 // 31], -1)
    p0, p1 = rgb(c0), rgb(c1)
    four = (c0 > c1)[..., None]
    p2 = np.where(four, (2 * p0 + p1) // 3, (p0 + p1) // 2)
    p3 = np.where(four, (p0 + 2 * p1) // 3, 0)
    pal = np.stack([p0, p1, p2, p3], -2)
    bits = b[..., 4] | (b[..., 5] << 8) | (b[..., 6] << 16) | (b[..., 7] << 24)
    img = np.zeros((bh * 4, bw * 4, 3), dtype=np.uint8)
    for py in range(4):
        for px in range(4):
            sel = (bits >> (2 * (py * 4 + px))) & 3
            img[py::4, px::4] = np.take_along_axis(pal, sel[..., None, None], axis=2)[..., 0, :]
    return img

def decode_color(raw, bw, bh, bpe):
    """BC1 (bpe 8) or BC2/BC3 (bpe 16): colour block is the last 8 bytes."""
    if bpe == 8:
        return bc1_decode(raw, bw, bh)
    b = np.frombuffer(raw, dtype=np.uint8).reshape(bh, bw, 16)
    return bc1_decode(np.ascontiguousarray(b[..., 8:16]).tobytes(), bw, bh)

DX_BPE = {b'DXT1': 8, b'DXT3': 16, b'DXT5': 16}
GNF_FMT = {0x23: ('BC1', 8), 0x24: ('BC2', 16), 0x25: ('BC3', 16)}
FOURCC_OF = {'BC1': b'DXT1', 'BC2': b'DXT3', 'BC3': b'DXT5'}
FMT_OF_FOURCC = {b'DXT1': 'BC1', b'DXT3': 'BC2', b'DXT5': 'BC3'}

def parse_dds_at(vram, p):
    if p < 0 or p + 128 > len(vram) or vram[p:p + 8] != b'DDS \x7c\x00\x00\x00':
        return None
    h, w = struct.unpack_from('<II', vram, p + 12)
    mips = struct.unpack_from('<I', vram, p + 28)[0]
    fc = vram[p + 84:p + 88]
    if fc not in DX_BPE or w < 4 or h < 4 or w > 16384 or h > 16384:
        return None
    bpe = DX_BPE[fc]
    n = ((w + 3) // 4) * ((h + 3) // 4) * bpe
    if p + 128 + n > len(vram):
        return None
    return dict(plat='pc', off=p, w=w, h=h, mips=max(1, mips), fmt=FMT_OF_FOURCC[fc], bpe=bpe, data=vram[p + 128:p + 128 + n])

def parse_gnf_at(vram, p):
    """Returns (texture dict or None, reason). Only BC1/BC2/BC3, 2D, tile mode 13 are supported."""
    if p < 0 or p + 0x30 > len(vram) or vram[p:p + 8] != b'GNF \xf8\x00\x00\x00':
        return None, 'no GNF header'
    ntex = vram[p + 9]
    stream = struct.unpack_from('<I', vram, p + 12)[0]
    t = struct.unpack_from('<8I', vram, p + 0x10)
    w, h = (t[2] & 0x3fff) + 1, ((t[2] >> 14) & 0x3fff) + 1
    df, nf = (t[1] >> 20) & 0x3f, (t[1] >> 26) & 0xf
    mips = ((t[3] >> 16) & 0xf) + 1
    tile, typ = (t[3] >> 20) & 0x1f, (t[3] >> 28) & 0xf
    if ntex != 1 or typ != 9:
        return None, 'not a single 2D texture'
    if tile != 13:
        return None, 'tile mode %d' % tile
    if df not in GNF_FMT:
        return None, 'format %#x' % df
    if w < 4 or h < 4 or p + stream > len(vram):
        return None, 'bad size'
    fmt, bpe = GNF_FMT[df]
    lay, total = mip_layout(w, h, mips, bpe)
    if total != stream - 0x100:
        return None, 'unexpected layout'
    return dict(plat='ps4', off=p, w=w, h=h, mips=mips, fmt=fmt, bpe=bpe, nfmt=nf, data=vram[p + 0x100:p + stream]), ''

def find_pc_textures(vram):
    out = []
    for m in re.finditer(b'DDS \x7c\x00\x00\x00', vram):
        t = parse_dds_at(vram, m.start())
        if t:
            out.append(t)
    return out

def find_ps4_textures(vram):
    out = []
    unsupported = 0
    for m in re.finditer(b'GNF \xf8\x00\x00\x00', vram):
        t, why = parse_gnf_at(vram, m.start())
        if t:
            out.append(t)
        else:
            unsupported += 1
    return out, unsupported

TEXTABLE_ID = 0x3960d5bf    # entry holding the texture table (same ID on both platforms)
BANK_ID = 0xf1c8c3ba        # entry holding the texture bank in VRAM

def pack_textures(main, vram, ents, plat):
    """Textures of a pack in table order, each with its texture ID. Returns (list, unsupported_reasons) or None
    when the pack has no texture table. PC records are 12 bytes (offset, 0, id), PS4 records 24 bytes (offset, ..., id)."""
    by = {e[0]: e for e in ents}
    if TEXTABLE_ID not in by or BANK_ID not in by:
        return None
    _, size, off = by[TEXTABLE_ID]
    if off & 1 or off + size > len(main) or size < 8:
        return None
    t = main[off:off + size]
    cnt = struct.unpack_from('<I', t, 4)[0]
    if cnt == 0 or (size - 8) % cnt:
        return None
    stride = (size - 8) // cnt
    idpos = stride * 2 // 3          # 12 -> 8, 24 -> 16
    if stride not in (12, 24) or (stride == 12) != (plat == 'pc'):
        return None
    bank = by[BANK_ID][2] & ~1
    out, bad, bad_ids = [], [], []
    for i in range(cnt):
        rec = 8 + i * stride
        rel = struct.unpack_from('<I', t, rec)[0]
        tid = struct.unpack_from('<I', t, rec + idpos)[0]
        if plat == 'pc':
            tex, why = parse_dds_at(vram, bank + rel), 'not a supported DDS'
        else:
            tex, why = parse_gnf_at(vram, bank + rel)
        if tex is None:
            bad.append(why)
            bad_ids.append(tid)
            continue
        tex['id'] = tid
        out.append(tex)
    return out, bad, bad_ids

def mip0_pixels(t):
    """Decoded RGB of the top mip."""
    bw, bh = (t['w'] + 3) // 4, (t['h'] + 3) // 4
    if t['plat'] == 'pc':
        raw = t['data']
    else:
        raw = detile_1d_thin(t['data'], bw, bh, t['bpe']).tobytes()
    return decode_color(raw, bw, bh, t['bpe'])

def thumb(img, n=16):
    h, w = img.shape[:2]
    ys = np.linspace(0, h, n + 1).astype(int)
    xs = np.linspace(0, w, n + 1).astype(int)
    a = img.astype(np.float64)
    a = np.add.reduceat(a, ys[:-1], axis=0) / np.diff(ys)[:, None, None]
    a = np.add.reduceat(a, xs[:-1], axis=1) / np.diff(xs)[None, :, None]
    return a.ravel()

def similarity(u, v):
    if abs(u.mean() - v.mean()) > 25:
        return 0.0
    u0, v0 = u - u.mean(), v - v.mean()
    d = np.linalg.norm(u0) * np.linalg.norm(v0)
    return float(u0 @ v0 / d) if d > 1e-6 else 0.0

def write_dds(path, t):
    """Write a PS4 texture (all mips, detiled) as a standard DDS."""
    lay, _ = mip_layout(t['w'], t['h'], t['mips'], t['bpe'])
    parts = []
    for (m, mw, mh, bw, bh, off, sz) in lay:
        parts.append(detile_1d_thin(t['data'], bw, bh, t['bpe'], offset=off).tobytes())
    bw0, bh0 = (t['w'] + 3) // 4, (t['h'] + 3) // 4
    hdr = bytearray(128)
    hdr[0:4] = b'DDS '
    struct.pack_into('<7I', hdr, 4, 124, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000, t['h'], t['w'],
                     bw0 * bh0 * t['bpe'], 0, t['mips'])
    struct.pack_into('<II', hdr, 76, 32, 4)
    hdr[84:88] = FOURCC_OF[t['fmt']]
    struct.pack_into('<I', hdr, 108, 0x1000 | 0x400000 | 0x8)
    with open(path, 'wb') as f:
        f.write(hdr)
        for part in parts:
            f.write(part)

# ----------------------------------------------------------------------------- commands

def open_mm(path):
    f = open(path, 'rb')
    return mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)

def cmd_scan(a):
    mm = open_mm(a.archive)
    files = group_files(mm)
    print('%s: %d files (page chains)' % (os.path.basename(a.archive), len(files)))
    for i, pages in enumerate(files[:a.max_files]):
        try:
            main, vram, ents = decode_file(mm, pages)
        except Exception as e:
            print('  file %d @%#x: not a trM# pack or decode problem (%s)' % (i, 0 if pages == RAW else pages[0], e))
            continue
        pc = find_pc_textures(vram)
        ps, unsup = find_ps4_textures(vram)
        tex = pc if a.archive.lower().endswith('.pc') else ps
        print('  file %3d @%#10x pages=%d entries=%d main=%#x vram=%#x textures=%d%s' %
              (i, 0 if pages == RAW else pages[0], (1 if pages == RAW else len(pages)), len(ents), len(main), len(vram), len(tex),
               (' (unsupported GNF: %d)' % unsup) if unsup else ''))
        for t in tex:
            print('       %5dx%-5d %s mips=%d' % (t['w'], t['h'], t['fmt'], t['mips']))

def index_dir(folder, ext, only):
    """key -> list of (archive_path, pages)."""
    idx = {}
    for name in sorted(os.listdir(folder)):
        if not name.lower().endswith(ext):
            continue
        base = name[:-len(ext)]
        if only and base not in only:
            continue
        path = os.path.join(folder, name)
        try:
            mm = open_mm(path)
        except Exception as e:
            print('  skip', name, e)
            continue
        t0 = time.time()
        n = 0
        for pages in group_files(mm):
            try:
                k = pack_key(mm, pages)
            except Exception:
                continue
            if k is None:
                continue
            idx.setdefault(k[0], []).append((path, pages))
            n += 1
        print('  indexed %-40s %5d packs  (%.1fs)' % (name, n, time.time() - t0))
    return idx

def cmd_build(a):
    only = set(a.archives.split(',')) if a.archives else None
    print('Indexing PS4 archives...')
    ps_idx = index_dir(a.ps4, '.big.ps4', only)
    print('Indexing PC archives...')
    pc_idx = index_dir(a.pc, '.big.pc', only)
    common = [k for k in pc_idx if k in ps_idx]
    print('\n%d resource packs on PC, %d on PS4, %d paired by entry-ID set' % (len(pc_idx), len(ps_idx), len(common)))

    os.makedirs(os.path.join(a.out, 'textures'), exist_ok=True)
    # Merge with an existing pack so the game can be processed archive by archive (use --fresh to start over).
    mpath = os.path.join(a.out, 'manifest.json')
    merged = {}
    if os.path.exists(mpath) and not a.fresh:
        for m in json.load(open(mpath)):
            merged[(int(m['crc32'], 16), m['w'], m['h'])] = m
        print('merging with %d entries already in %s' % (len(merged), mpath))
    before = len(merged)
    st = dict(packs=0, id_packs=0, content_packs=0, pc_tex=0, id_matched=0, content_matched=0, same_size=0,
              no_counterpart=0, incompatible=0, uncertain=0, upgraded=0, unsupported_ps4=0)
    scale_hist, unsup_reasons, uncertain_list, per = {}, {}, [], {}
    t0 = time.time()
    for n, k in enumerate(common, 1):
        if n % 25 == 0:
            print('  ... %d/%d packs (%.0fs), upgraded so far: %d' % (n, len(common), time.time() - t0, st['upgraded']))
        ppath, ppages = pc_idx[k][0]
        spath, spages = ps_idx[k][0]
        arc = os.path.basename(ppath)[:-len('.big.pc')]
        snap = dict(st)
        try:
            pmain, pvram, pents = decode_file(open_mm(ppath), ppages)
            smain, svram, sents = decode_file(open_mm(spath), spages)
        except Exception as e:
            print('  decode problem in a pack (%s), skipped' % e)
            continue
        st['packs'] += 1
        ok = lambda t: t['fmt'] in ('BC1', 'BC3') and min(t['w'], t['h']) >= 16
        PT, ST = pack_textures(pmain, pvram, pents, 'pc'), pack_textures(smain, svram, sents, 'ps4')
        pairs = []                                   # (pc texture, ps4 texture, similarity, how)
        if PT is not None and ST is not None:
            # exact: the pack's texture table gives every texture an ID that is the same on both platforms
            st['id_packs'] += 1
            for why in ST[1]:
                st['unsupported_ps4'] += 1
                unsup_reasons[why] = unsup_reasons.get(why, 0) + 1
            by_id = {t['id']: t for t in ST[0]}
            for p in PT[0]:
                if not ok(p):
                    continue
                st['pc_tex'] += 1
                s = by_id.get(p['id'])
                if s is None:
                    st['no_counterpart'] += 1
                    continue
                st['id_matched'] += 1
                if s['fmt'] != p['fmt'] or p['w'] * s['h'] != p['h'] * s['w']:
                    st['incompatible'] += 1
                    continue
                if s['w'] * s['h'] <= p['w'] * p['h']:
                    st['same_size'] += 1
                    continue
                c = similarity(thumb(mip0_pixels(p)), thumb(mip0_pixels(s)))
                if c < a.id_corr and not a.trust_ids:
                    st['uncertain'] += 1
                    uncertain_list.append('%08x %dx%d -> %dx%d corr %.2f' % (p['id'], p['w'], p['h'], s['w'], s['h'], c))
                    continue
                pairs.append((p, s, c, 'id'))
        else:
            # fallback: no texture table, so match by picture content
            st['content_packs'] += 1
            P = [t for t in find_pc_textures(pvram) if ok(t)]
            S = [t for t in find_ps4_textures(svram)[0] if ok(t)]
            st['pc_tex'] += len(P)
            for t in P + S:
                t['th'] = thumb(mip0_pixels(t))
            cands = []
            for i, p in enumerate(P):
                for j, s in enumerate(S):
                    if p['fmt'] != s['fmt'] or p['w'] * s['h'] != p['h'] * s['w'] or s['w'] < p['w']:
                        continue
                    c = similarity(p['th'], s['th'])
                    if c >= a.min_corr:
                        cands.append((c, i, j))
            used_p, used_s = set(), set()
            for c, i, j in sorted(cands, reverse=True):
                if i in used_p or j in used_s:
                    continue
                used_p.add(i)
                used_s.add(j)
                st['content_matched'] += 1
                if S[j]['w'] * S[j]['h'] <= P[i]['w'] * P[i]['h']:
                    st['same_size'] += 1
                    continue
                pairs.append((P[i], S[j], c, 'content'))
        for p, s, c, how in pairs:
            crc = zlib.crc32(p['data']) & 0xFFFFFFFF
            key = (crc, p['w'], p['h'])
            if key in merged and merged[key]['corr'] >= round(c, 3):
                continue
            name = '%08x_%dx%d.dds' % (crc, p['w'], p['h'])
            write_dds(os.path.join(a.out, 'textures', name), s)
            merged[key] = dict(crc32='%08x' % crc, w=p['w'], h=p['h'], fmt=p['fmt'], file=name,
                               new_w=s['w'], new_h=s['h'], mips=s['mips'], corr=round(c, 3), match=how)
            st['upgraded'] += 1
            sc = '%dx' % (s['w'] // p['w'])
            scale_hist[sc] = scale_hist.get(sc, 0) + 1
        d = per.setdefault(arc, dict.fromkeys(st, 0))
        for kk in st:
            d[kk] += st[kk] - snap[kk]
    manifest = sorted(merged.values(), key=lambda m: m['file'])
    with open(mpath, 'w') as f:
        json.dump(manifest, f, indent=1)
    with open(os.path.join(a.out, 'manifest.txt'), 'w') as f:      # simple format for the C++ plugin
        for m in manifest:
            f.write('%s %d %d %s textures/%s\n' % (m['crc32'], m['w'], m['h'], m['fmt'], m['file']))
    total_mb = sum(os.path.getsize(os.path.join(a.out, 'textures', m['file'])) for m in manifest
                   if os.path.exists(os.path.join(a.out, 'textures', m['file']))) / 1e6
    print('\npacks compared: %d (%d by texture ID, %d by picture content) | PC textures BC1/BC3 >=16px: %d'
          % (st['packs'], st['id_packs'], st['content_packs'], st['pc_tex']))
    print('  same texture on PS4: %d (already same size: %d, incompatible shape/format: %d) | no counterpart on PS4: %d'
          % (st['id_matched'] + st['content_matched'], st['same_size'], st['incompatible'], st['no_counterpart']))
    print('  new/updated upgrades: %d | uncertain (ID matched but pictures differ, skipped): %d | PS4 textures in unsupported formats: %d'
          % (st['upgraded'], st['uncertain'], st['unsupported_ps4']))
    if unsup_reasons:
        print('  unsupported PS4 texture kinds:', dict(sorted(unsup_reasons.items(), key=lambda kv: -kv[1])[:6]))
    if uncertain_list:
        print('  uncertain examples (use --trust-ids to accept them):', uncertain_list[:5])
    if per:
        print('\nper archive (this run):')
        print('  %-24s %5s %7s %9s %9s %7s %7s %6s' % ('archive', 'packs', 'PC tex', 'upgraded', 'same size', 'no PS4', 'unsup.', 'uncert'))
        for arc, d in sorted(per.items(), key=lambda kv: -kv[1]['upgraded']):
            print('  %-24s %5d %7d %9d %9d %7d %7d %6d' % (arc, d['packs'], d['pc_tex'], d['upgraded'], d['same_size'],
                                                           d['no_counterpart'], d['unsupported_ps4'], d['uncertain']))
    print('linear upscale factors this run:', scale_hist)
    print('pack now has %d entries (was %d), %.1f MB on disk, wrote %s' % (len(manifest), before, total_mb, os.path.join(a.out, 'manifest.txt')))

def parse_plugin_log(path):
    """Top-mip uploads recorded by LanTexReplace.dll: lines 'V,w,h,dxgiformat,crc32,inPack'."""
    ups = []
    for line in open(path, errors='replace'):
        if line.startswith('V,'):
            f = line.strip().split(',')
            try:
                ups.append((int(f[1]), int(f[2]), int(f[3]), int(f[4], 16), f[5] == '1'))
            except (ValueError, IndexError):
                pass
    return ups

def cmd_explain(a):
    ups = parse_plugin_log(a.log)
    if not ups:
        sys.exit('No "V," lines in the log. Turn on the diagnostic log (Log=1, LogUpdates=1), play a few minutes, then try again.')
    wanted = {}                                   # (crc, w, h) -> [uploads, inPack]
    for w, h, fmt, crc, hit in ups:
        e = wanted.setdefault((crc, w, h), [0, hit])
        e[0] += 1
    n_hit = sum(1 for v in wanted.values() if v[1])
    print('log: %d top-mip uploads, %d distinct textures (%d already covered by the pack)' % (len(ups), len(wanted), n_hit))

    only = set(a.archives.split(',')) if a.archives else None
    found = {}                                    # (crc, w, h) -> dict(arc, key, id, fmt)
    t0 = time.time()
    print('Scanning PC archives for these textures (this reads every pack, so it takes a while)...')
    for name in sorted(os.listdir(a.pc)):
        if not name.lower().endswith('.big.pc'):
            continue
        arc = name[:-len('.big.pc')]
        if only and arc not in only:
            continue
        try:
            mm = open_mm(os.path.join(a.pc, name))
        except Exception as e:
            print('  skip', name, e)
            continue
        n_pack = n_found = 0
        for pages in group_files(mm):
            try:
                pk = pack_key(mm, pages)
                if pk is None:
                    continue
                main, vram, ents = decode_file(mm, pages)
            except Exception:
                continue
            n_pack += 1
            tt = pack_textures(main, vram, ents, 'pc')
            texs = tt[0] if tt is not None else find_pc_textures(vram)
            for t in texs:
                key = (zlib.crc32(t['data']) & 0xFFFFFFFF, t['w'], t['h'])
                if key in wanted and key not in found:
                    found[key] = dict(k=key, arc=arc, key=pk[0], id=t.get('id'), fmt=t['fmt'], w=t['w'], h=t['h'])
                    n_found += 1
        print('  %-26s %5d packs, %4d of the logged textures found  (%.0fs)' % (arc, n_pack, n_found, time.time() - t0))

    # classify against the PS4 counterpart of each pack
    need = {}
    for v in found.values():
        need.setdefault(v['key'], []).append(v)
    ps_idx = index_dir(a.ps4, '.big.ps4', only) if a.ps4 else {}
    cats = ('upgradeable', 'same size', 'no PS4 counterpart', 'incompatible', 'unsupported PS4 format', 'no PS4 pack')
    per = {}
    def bump(arc, cat, uploads):
        d = per.setdefault(arc, dict.fromkeys(cats, 0))
        d[cat] += 1
        per[arc].setdefault('uploads_' + cat, 0)
        per[arc]['uploads_' + cat] += uploads
    for key, items in need.items():
        ps = None
        if key in ps_idx:
            try:
                spath, spages = ps_idx[key][0]
                smain, svram, sents = decode_file(open_mm(spath), spages)
                st = pack_textures(smain, svram, sents, 'ps4')
                ps = (st[0], set(st[2])) if st is not None else None
            except Exception:
                ps = None
        by_id = {t['id']: t for t in ps[0]} if ps else {}
        for v in items:
            uploads, in_pack = wanted[v['k']]
            if in_pack:
                cat = 'upgradeable'                     # already in the pack
            elif ps is None:
                cat = 'no PS4 pack'
            elif v['id'] in ps[1]:
                cat = 'unsupported PS4 format'
            elif v['id'] not in by_id:
                cat = 'no PS4 counterpart'
            else:
                s = by_id[v['id']]
                if s['fmt'] != v['fmt'] or v['w'] * s['h'] != v['h'] * s['w']:
                    cat = 'incompatible'
                elif s['w'] * s['h'] <= v['w'] * v['h']:
                    cat = 'same size'
                else:
                    cat = 'upgradeable'
            bump(v['arc'], cat, uploads)

    total_up = len(ups)
    found_up = sum(wanted[k][0] for k in found)
    print('\n== Where the textures the game loaded come from (textures / uploads)')
    print('  %-26s %9s %12s %9s %17s %12s %11s' % ('archive', 'upgrade-', 'same size', 'no PS4', 'incompatible/unsup.', 'no PS4 pack', 'total'))
    print('  %-26s %9s %12s %9s %17s %12s %11s' % ('', 'able', '', 'counterp.', '', '', ''))
    for arc, d in sorted(per.items(), key=lambda kv: -kv[1]['uploads_upgradeable']):
        tot = sum(d[c] for c in cats)
        print('  %-26s %9d %12d %9d %17d %12d %11d   (upgradeable uploads: %d)' % (
            arc, d['upgradeable'], d['same size'], d['no PS4 counterpart'], d['incompatible'] + d['unsupported PS4 format'], d['no PS4 pack'], tot,
            d['uploads_upgradeable']))
    up_up = sum(d['uploads_upgradeable'] for d in per.values())
    print('\nuploads in the log: %d | found in the PC archives scanned: %d (%.0f%%) | could be upgraded: %d (%.0f%%) | in the pack now: %d'
          % (total_up, found_up, 100.0 * found_up / total_up, up_up, 100.0 * up_up / total_up,
             sum(wanted[k][0] for k in wanted if wanted[k][1])))
    print('not found in the scanned archives: %d uploads (other archives, runtime textures, or not scanned: omit --archives to scan all)'
          % (total_up - found_up))
    print('Build the archives at the top of this table (largest "upgradeable") first.')

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('scan'); s.add_argument('archive'); s.add_argument('--max-files', type=int, default=10)
    e = sub.add_parser('explain'); e.add_argument('--pc', required=True); e.add_argument('--ps4'); e.add_argument('--log', required=True); e.add_argument('--archives')
    b = sub.add_parser('build'); b.add_argument('--pc', required=True); b.add_argument('--ps4', required=True)
    b.add_argument('--out', required=True); b.add_argument('--archives'); b.add_argument('--min-corr', type=float, default=0.85)
    b.add_argument('--fresh', action='store_true', help='ignore an existing manifest and start a new pack')
    b.add_argument('--id-corr', type=float, default=0.5, help='for textures matched by ID: minimum picture similarity (default 0.5)')
    b.add_argument('--trust-ids', action='store_true', help='accept every ID match regardless of picture similarity')
    a = ap.parse_args()
    {'scan': cmd_scan, 'build': cmd_build, 'explain': cmd_explain}[a.cmd](a)

if __name__ == '__main__':
    main()
