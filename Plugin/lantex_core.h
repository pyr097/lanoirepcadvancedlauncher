// lantex_core.h - platform-independent core of the L.A. Noire texture replacement plugin.
// CRC32 (same as zlib/Python), manifest parsing and DDS loading. No Windows dependencies,
// so it can be unit-tested anywhere.
#pragma once
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

namespace lantex {

inline uint32_t crc32(const void* data, size_t n) {
    static uint32_t table[256];
    static bool init = false;
    if (!init) {
        for (uint32_t i = 0; i < 256; i++) {
            uint32_t c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            table[i] = c;
        }
        init = true;
    }
    uint32_t c = 0xFFFFFFFFu;
    const uint8_t* p = static_cast<const uint8_t*>(data);
    for (size_t i = 0; i < n; i++) c = table[(c ^ p[i]) & 0xFF] ^ (c >> 8);
    return ~c;
}

// A DDS file with a BC1/BC2/BC3 payload and a full mip chain (as written by lan_tex_tools.py).
struct DdsImage {
    uint32_t w = 0, h = 0, mips = 0, bpe = 0;      // bpe = bytes per 4x4 block (8 or 16)
    std::vector<uint8_t> bytes;                     // all mip payloads back to back
    std::vector<size_t> offset;                     // per mip
    std::vector<uint32_t> pitch;                    // per mip: bytes per block row
};

inline bool loadDds(const std::string& path, DdsImage& out, std::string* err = nullptr) {
    auto fail = [&](const char* m) { if (err) *err = m; return false; };
    std::ifstream f(path, std::ios::binary);
    if (!f) return fail("cannot open file");
    std::vector<uint8_t> d((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    if (d.size() < 128 || std::memcmp(d.data(), "DDS ", 4) != 0) return fail("not a DDS file");
    auto u32 = [&](size_t o) { uint32_t v; std::memcpy(&v, d.data() + o, 4); return v; };
    if (u32(4) != 124) return fail("bad header size");
    uint32_t h = u32(12), w = u32(16), mips = u32(28);
    if (mips == 0) mips = 1;
    const char* fc = reinterpret_cast<const char*>(d.data() + 84);
    uint32_t bpe = 0;
    if (!std::memcmp(fc, "DXT1", 4)) bpe = 8;
    else if (!std::memcmp(fc, "DXT3", 4) || !std::memcmp(fc, "DXT5", 4)) bpe = 16;
    else return fail("unsupported pixel format (only DXT1/3/5)");
    if (w == 0 || h == 0 || w > 16384 || h > 16384 || mips > 16) return fail("bad dimensions");
    out = DdsImage();
    out.w = w; out.h = h; out.mips = mips; out.bpe = bpe;
    size_t pos = 128;
    for (uint32_t m = 0; m < mips; m++) {
        uint32_t mw = (w >> m) ? (w >> m) : 1, mh = (h >> m) ? (h >> m) : 1;
        uint32_t bw = (mw + 3) / 4, bh = (mh + 3) / 4;
        size_t sz = size_t(bw) * bh * bpe;
        if (pos + sz > d.size()) return fail("file shorter than its mip chain");
        out.offset.push_back(pos - 128);
        out.pitch.push_back(bw * bpe);
        pos += sz;
    }
    out.bytes.assign(d.begin() + 128, d.begin() + pos);
    return true;
}

struct Entry {
    std::string path;
    uint32_t w = 0, h = 0, bpe = 0;
    DdsImage img;
    bool loaded = false, failed = false;
};

inline uint64_t makeKey(uint32_t crc, uint32_t w, uint32_t h) {
    return (uint64_t(crc) << 32) | (uint64_t(w & 0xFFFF) << 16) | (h & 0xFFFF);
}

// manifest.txt lines: "<crc32 hex> <width> <height> <BC1|BC2|BC3> <relative path>"
inline size_t loadManifest(const std::string& file, const std::string& baseDir,
                           std::unordered_map<uint64_t, Entry>& out) {
    std::ifstream f(file);
    std::string line;
    size_t n = 0;
    while (std::getline(f, line)) {
        std::istringstream ss(line);
        std::string crcs, fmt, rel;
        uint32_t w = 0, h = 0;
        if (!(ss >> crcs >> w >> h >> fmt >> rel)) continue;
        uint32_t crc = static_cast<uint32_t>(std::strtoul(crcs.c_str(), nullptr, 16));
        Entry e;
        e.w = w; e.h = h;
        e.bpe = (fmt == "BC1") ? 8 : 16;
        e.path = baseDir + "/" + rel;
        out.emplace(makeKey(crc, w, h), std::move(e));
        n++;
    }
    return n;
}

}  // namespace lantex
