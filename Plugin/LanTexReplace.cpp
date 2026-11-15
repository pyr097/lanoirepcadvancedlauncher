// LanTexReplace.cpp - L.A. Noire (PC, 32-bit) D3D11 texture replacement plugin (.asi), version 2.
//
// What the first log showed: the engine creates every block-compressed texture EMPTY
// (CreateTexture2D without initial data) and then fills each mip with UpdateSubresource.
// A texture cannot be resized after creation, so this plugin works in three steps:
//   1. UpdateSubresource, mip 0: hash the uploaded bytes (CRC32, same as lan_tex_tools.py). If the
//      hash is in the pack, build the HD replacement texture (with its full mip chain) right away.
//   2. CreateShaderResourceView: remember which view belongs to which texture.
//   3. PSSetShaderResources: when the engine binds a view of a replaced texture, bind a view of the
//      HD texture instead (mip range adjusted to the HD mip chain).
// Cleanup is automatic: private data attached to the engine's objects tells us when they die.
//
// Works with native D3D11 and DXVK. Start with Replace=0 and Log=1 to verify matching.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>
#include <unordered_map>
#include "lantex_core.h"

using namespace lantex;

// ------------------------------------------------------------------ state
static HMODULE g_self = nullptr;
static std::mutex g_entryMx;                          // protects g_map (lazy DDS loading)
static std::unordered_map<uint64_t, Entry> g_map;
static std::recursive_mutex g_mx;                     // protects g_res / g_srv (re-entered from private-data release)
static bool g_replace = true, g_log = false, g_logUpdates = false;
static std::string g_dir, g_pack;
static FILE* g_logf = nullptr;
static long g_lines = 0;
static const long kMaxLines = 300000;
static size_t g_hdBytes = 0, g_peakHdBytes = 0, g_maxHdBytes = size_t(1024) << 20;
static long g_skippedBudget = 0;
static long g_ct2d = 0, g_bcCreated = 0, g_bcMip0 = 0, g_hits = 0, g_built = 0, g_srvBC = 0, g_substituted = 0, g_failedBuild = 0;

static const GUID kGuidRes = {0x6c3a4f10, 0x2b7d, 0x4e1a, {0x9a, 0x01, 0x4c, 0x4c, 0x54, 0x52, 0x45, 0x53}};
static const GUID kGuidSrv = {0x6c3a4f11, 0x2b7d, 0x4e1a, {0x9a, 0x01, 0x4c, 0x4c, 0x54, 0x53, 0x52, 0x56}};

struct ResInfo { ID3D11Texture2D* rep; UINT origMips; UINT newMips; size_t bytes; };
struct SrvInfo { ID3D11Resource* res; ID3D11Texture2D* repTex; ID3D11ShaderResourceView* repSrv; };
static std::unordered_map<ID3D11Resource*, ResInfo> g_res;
static std::unordered_map<ID3D11ShaderResourceView*, SrvInfo> g_srv;

typedef HRESULT(STDMETHODCALLTYPE* PFN_CT2D)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);
typedef HRESULT(STDMETHODCALLTYPE* PFN_CSRV)(ID3D11Device*, ID3D11Resource*, const D3D11_SHADER_RESOURCE_VIEW_DESC*, ID3D11ShaderResourceView**);
typedef void(STDMETHODCALLTYPE* PFN_UPD)(ID3D11DeviceContext*, ID3D11Resource*, UINT, const D3D11_BOX*, const void*, UINT, UINT);
typedef void(STDMETHODCALLTYPE* PFN_PSSRV)(ID3D11DeviceContext*, UINT, UINT, ID3D11ShaderResourceView* const*);
static PFN_CT2D g_origCT2D = nullptr;
static PFN_CSRV g_origCSRV = nullptr;
static PFN_UPD g_origUpd = nullptr;
static PFN_PSSRV g_origPSSRV = nullptr;

static void logLine(const char* fmt, ...) {
    if (!g_logf || g_lines >= kMaxLines) return;
    va_list ap;
    va_start(ap, fmt);
    std::vfprintf(g_logf, fmt, ap);
    va_end(ap);
    if ((++g_lines & 0xFF) == 0) std::fflush(g_logf);
}

static void flushLog() { if (g_logf) std::fflush(g_logf); }

static uint32_t bcBytes(DXGI_FORMAT f) {
    int v = static_cast<int>(f);
    if (v >= 70 && v <= 72) return 8;     // BC1
    if (v >= 73 && v <= 78) return 16;    // BC2 / BC3
    return 0;
}

// ------------------------------------------------------------------ private data (lifetime tracking)
static void onDestroyed(int kind, void* key);
struct PrivData final : IUnknown {
    LONG rc = 1;
    int kind;
    void* key;
    PrivData(int k, void* p) : kind(k), key(p) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (IsEqualGUID(r, __uuidof(IUnknown))) { *p = this; AddRef(); return S_OK; }
        *p = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&rc); }
    ULONG STDMETHODCALLTYPE Release() override {
        LONG n = InterlockedDecrement(&rc);
        if (n == 0) { onDestroyed(kind, key); delete this; }
        return n;
    }
};

static void onDestroyed(int kind, void* key) {
    std::lock_guard<std::recursive_mutex> lock(g_mx);
    if (kind == 0) {
        auto it = g_res.find(static_cast<ID3D11Resource*>(key));
        if (it != g_res.end()) {
            if (it->second.rep) it->second.rep->Release();
            g_hdBytes = g_hdBytes >= it->second.bytes ? g_hdBytes - it->second.bytes : 0;
            g_res.erase(it);
        }
    } else {
        auto it = g_srv.find(static_cast<ID3D11ShaderResourceView*>(key));
        if (it != g_srv.end()) {
            if (it->second.repSrv) it->second.repSrv->Release();
            g_srv.erase(it);
        }
    }
}

static bool attach(ID3D11DeviceChild* obj, const GUID& guid, int kind, void* key) {
    PrivData* pd = new PrivData(kind, key);
    HRESULT hr = obj->SetPrivateDataInterface(guid, pd);
    pd->Release();                       // the object now owns it (or it is freed if the call failed)
    return SUCCEEDED(hr);
}

// ------------------------------------------------------------------ replacement lookup / build
static Entry* lookup(uint32_t crc, uint32_t w, uint32_t h, uint32_t bpe) {
    std::lock_guard<std::mutex> lock(g_entryMx);
    auto it = g_map.find(makeKey(crc, w, h));
    if (it == g_map.end() || it->second.bpe != bpe || it->second.failed) return nullptr;
    return &it->second;     // the DDS file is read only when the HD texture is built, and freed right after
}

// Reads the DDS from disk, creates an immutable GPU texture from it, and lets the CPU copy go out of scope.
// This keeps the 32-bit process small even with thousands of replaced textures.
static ID3D11Texture2D* buildReplacement(ID3D11DeviceContext* ctx, const D3D11_TEXTURE2D_DESC& od, Entry* e, UINT* newMips, size_t* bytes) {
    DdsImage img;
    std::string err;
    if (!loadDds(e->path, img, &err) || img.bpe != e->bpe) {
        std::lock_guard<std::mutex> lock(g_entryMx);
        e->failed = true;
        logLine("# failed to load %s: %s\n", e->path.c_str(), err.c_str());
        return nullptr;
    }
    if (g_hdBytes + img.bytes.size() > g_maxHdBytes) {
        if (g_skippedBudget++ == 0) logLine("# HD memory budget reached (%zu MB); further replacements are skipped until textures are released\n", g_maxHdBytes >> 20);
        return nullptr;
    }
    ID3D11Device* dev = nullptr;
    ctx->GetDevice(&dev);
    if (!dev) return nullptr;
    D3D11_TEXTURE2D_DESC nd = od;
    nd.Width = img.w;
    nd.Height = img.h;
    nd.MipLevels = img.mips;
    nd.ArraySize = 1;
    nd.Usage = D3D11_USAGE_IMMUTABLE;
    nd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    nd.CPUAccessFlags = 0;
    nd.MiscFlags = 0;
    D3D11_SUBRESOURCE_DATA sub[16];
    for (uint32_t m = 0; m < img.mips && m < 16; m++) {
        sub[m].pSysMem = img.bytes.data() + img.offset[m];
        sub[m].SysMemPitch = img.pitch[m];
        sub[m].SysMemSlicePitch = 0;
    }
    ID3D11Texture2D* tex = nullptr;
    HRESULT hr = g_origCT2D(dev, &nd, sub, &tex);
    dev->Release();
    if (FAILED(hr)) { logLine("# HD texture rejected by the runtime (hr=%08x)\n", (unsigned)hr); return nullptr; }
    *newMips = img.mips;
    *bytes = img.bytes.size();
    return tex;
}

// ------------------------------------------------------------------ hooks
static HRESULT STDMETHODCALLTYPE HK_CreateTexture2D(ID3D11Device* self, const D3D11_TEXTURE2D_DESC* desc,
                                                     const D3D11_SUBRESOURCE_DATA* init, ID3D11Texture2D** out) {
    g_ct2d++;
    if (desc && bcBytes(desc->Format)) {
        g_bcCreated++;
        if (g_log && g_bcCreated <= 3000)
            logLine("C,%u,%u,%u,%d,%u,%u,%d\n", desc->Width, desc->Height, desc->MipLevels, static_cast<int>(desc->Format),
                    desc->Usage, desc->BindFlags, (init && init[0].pSysMem) ? 1 : 0);
    }
    return g_origCT2D(self, desc, init, out);
}

static HRESULT STDMETHODCALLTYPE HK_CreateSRV(ID3D11Device* self, ID3D11Resource* res, const D3D11_SHADER_RESOURCE_VIEW_DESC* desc,
                                               ID3D11ShaderResourceView** out) {
    HRESULT hr = g_origCSRV(self, res, desc, out);
    if (SUCCEEDED(hr) && out && *out && res && g_replace) {
        ID3D11Texture2D* tex = nullptr;
        if (SUCCEEDED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&tex))) && tex) {
            D3D11_TEXTURE2D_DESC d;
            tex->GetDesc(&d);
            tex->Release();
            if (bcBytes(d.Format) && d.ArraySize == 1) {
                g_srvBC++;
                ID3D11ShaderResourceView* srv = *out;
                if (attach(srv, kGuidSrv, 1, srv)) {
                    std::lock_guard<std::recursive_mutex> lock(g_mx);
                    g_srv[srv] = SrvInfo{res, nullptr, nullptr};
                }
            }
        }
    }
    return hr;
}

static void STDMETHODCALLTYPE HK_UpdateSubresource(ID3D11DeviceContext* self, ID3D11Resource* res, UINT sub, const D3D11_BOX* box,
                                                    const void* data, UINT rowPitch, UINT depthPitch) {
    if (sub == 0 && res && data && (g_replace || g_logUpdates)) {
        ID3D11Texture2D* tex = nullptr;
        if (SUCCEEDED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&tex))) && tex) {
            D3D11_TEXTURE2D_DESC d;
            tex->GetDesc(&d);
            tex->Release();
            uint32_t bpe = bcBytes(d.Format);
            uint32_t bw = (d.Width + 3) / 4, bh = (d.Height + 3) / 4;
            if (bpe && d.ArraySize == 1 && d.Width >= 16 && d.Height >= 16 && rowPitch == bw * bpe) {
                g_bcMip0++;
                uint32_t crc = crc32(data, size_t(rowPitch) * bh);
                Entry* e = lookup(crc, d.Width, d.Height, bpe);
                if (e) g_hits++;
                if (g_logUpdates && (e || g_bcMip0 <= 30000))
                    logLine("V,%u,%u,%d,%08x,%d\n", d.Width, d.Height, static_cast<int>(d.Format), crc, e ? 1 : 0);
                if (g_replace) {
                    std::lock_guard<std::recursive_mutex> lock(g_mx);
                    if (g_res.count(res)) res->SetPrivateDataInterface(kGuidRes, nullptr);   // content changed: drop old mapping
                    if (e) {
                        UINT nm = 0;
                        size_t nb = 0;
                        ID3D11Texture2D* rep = buildReplacement(self, d, e, &nm, &nb);
                        if (rep) {
                            if (attach(res, kGuidRes, 0, res)) {
                                g_res[res] = ResInfo{rep, d.MipLevels, nm, nb};
                                g_hdBytes += nb;
                                if (g_hdBytes > g_peakHdBytes) g_peakHdBytes = g_hdBytes;
                                g_built++;
                                if (g_log && g_built <= 400)
                                    logLine("R,%u,%u,%08x,%u,%u,%zu\n", d.Width, d.Height, crc, e->w, e->h, nb);
                            } else {
                                rep->Release();
                                g_failedBuild++;
                            }
                        } else {
                            g_failedBuild++;
                        }
                    }
                }
            }
        }
    }
    g_origUpd(self, res, sub, box, data, rowPitch, depthPitch);
}

static void STDMETHODCALLTYPE HK_PSSetShaderResources(ID3D11DeviceContext* self, UINT start, UINT num,
                                                       ID3D11ShaderResourceView* const* views) {
    if (!g_replace || !views || num == 0 || num > 128) { g_origPSSRV(self, start, num, views); return; }
    ID3D11ShaderResourceView* local[128];
    bool changed = false;
    {
        std::lock_guard<std::recursive_mutex> lock(g_mx);
        if (g_res.empty()) { g_origPSSRV(self, start, num, views); return; }
        for (UINT i = 0; i < num; i++) {
            local[i] = views[i];
            if (!views[i]) continue;
            auto si = g_srv.find(views[i]);
            if (si == g_srv.end()) continue;
            auto ri = g_res.find(si->second.res);
            if (ri == g_res.end()) continue;
            SrvInfo& s = si->second;
            if (s.repTex != ri->second.rep) {                               // (re)create the HD view
                if (s.repSrv) { s.repSrv->Release(); s.repSrv = nullptr; }
                s.repTex = ri->second.rep;
                D3D11_SHADER_RESOURCE_VIEW_DESC od;
                views[i]->GetDesc(&od);
                if (od.ViewDimension == D3D11_SRV_DIMENSION_TEXTURE2D) {
                    UINT extra = ri->second.newMips > ri->second.origMips ? ri->second.newMips - ri->second.origMips : 0;
                    D3D11_SHADER_RESOURCE_VIEW_DESC nd = od;
                    UINT most = od.Texture2D.MostDetailedMip;
                    nd.Texture2D.MostDetailedMip = most == 0 ? 0 : most + extra;
                    if (od.Texture2D.MipLevels != static_cast<UINT>(-1)) {
                        UINT lv = od.Texture2D.MipLevels + (most == 0 ? extra : 0);
                        nd.Texture2D.MipLevels = (nd.Texture2D.MostDetailedMip + lv > ri->second.newMips) ? static_cast<UINT>(-1) : lv;
                    }
                    ID3D11Device* dev = nullptr;
                    s.repTex->GetDevice(&dev);
                    if (dev) {
                        ID3D11ShaderResourceView* nv = nullptr;
                        if (SUCCEEDED(g_origCSRV(dev, s.repTex, &nd, &nv))) s.repSrv = nv;
                        dev->Release();
                    }
                }
            }
            if (s.repSrv) { local[i] = s.repSrv; changed = true; g_substituted++; }
        }
    }
    g_origPSSRV(self, start, num, changed ? local : views);
}

// ------------------------------------------------------------------ setup
static bool patchSlot(void** vtbl, int index, void* hook, void** orig) {
    DWORD oldProt;
    if (!VirtualProtect(&vtbl[index], sizeof(void*), PAGE_READWRITE, &oldProt)) return false;
    *orig = vtbl[index];
    vtbl[index] = hook;
    VirtualProtect(&vtbl[index], sizeof(void*), oldProt, &oldProt);
    return true;
}

static void stats(const char* tag) {
    logLine("# %s: ct2d=%ld bcCreated=%ld bcMip0=%ld hits=%ld built=%ld failedBuild=%ld srvBC=%ld substitutedBinds=%ld skippedBudget=%ld hdMB=%zu peakHdMB=%zu\n", tag, g_ct2d,
            g_bcCreated, g_bcMip0, g_hits, g_built, g_failedBuild, g_srvBC, g_substituted, g_skippedBudget, g_hdBytes >> 20, g_peakHdBytes >> 20);
}

static DWORD WINAPI InitThread(LPVOID) {
    char path[MAX_PATH];
    GetModuleFileNameA(g_self, path, MAX_PATH);
    g_dir = path;
    g_dir = g_dir.substr(0, g_dir.find_last_of("\\/"));
    std::string ini = g_dir + "\\LanTexReplace.ini";
    g_replace = GetPrivateProfileIntA("LanTexReplace", "Replace", 1, ini.c_str()) != 0;
    g_log = GetPrivateProfileIntA("LanTexReplace", "Log", 0, ini.c_str()) != 0;
    g_logUpdates = GetPrivateProfileIntA("LanTexReplace", "LogUpdates", 0, ini.c_str()) != 0;
    UINT mb = GetPrivateProfileIntA("LanTexReplace", "MaxHdMB", 1024, ini.c_str());
    g_maxHdBytes = size_t(mb < 64 ? 64 : mb) << 20;
    char pack[MAX_PATH];
    GetPrivateProfileStringA("LanTexReplace", "Pack", "hdpack", pack, MAX_PATH, ini.c_str());
    g_pack = (pack[1] == ':' || pack[0] == '\\') ? std::string(pack) : g_dir + "\\..\\" + pack;

    if (g_log || g_logUpdates) {
        std::string lp = g_dir + "\\LanTexReplace.log";
        g_logf = std::fopen(lp.c_str(), "w");
        logLine("# LanTexReplace log. C=CreateTexture2D(BC), V=mip0 upload (w,h,dxgiformat,crc32,inPack), R=HD texture built (w,h,crc32,newW,newH,bytes)\n");
    }
    // The manifest is always loaded so that matching can be verified even with Replace=0.
    size_t n = loadManifest(g_pack + "\\manifest.txt", g_pack, g_map);
    logLine("# pack=%s entries=%zu replace=%d maxHdMB=%zu\n", g_pack.c_str(), n, g_replace ? 1 : 0, g_maxHdBytes >> 20);
    flushLog();

    // Stay out of the way while the game starts (other plugins, e.g. V-Patch, scan and patch the exe at that moment).
    // Do nothing until the game itself has loaded d3d11.dll (native or DXVK), waiting up to 90 seconds.
    // With the DirectX 9 path that never happens and the plugin simply stays idle.
    HMODULE d3d = nullptr;
    for (int i = 0; i < 18000 && !(d3d = GetModuleHandleA("d3d11.dll")); i++) Sleep(5);
    if (!d3d) { logLine("# the game never loaded d3d11.dll (DirectX 9 path?), nothing to hook\n"); flushLog(); return 0; }
    logLine("# the game loaded d3d11.dll, installing hooks\n");
    auto create = reinterpret_cast<PFN_D3D11_CREATE_DEVICE>(GetProcAddress(d3d, "D3D11CreateDevice"));
    if (!create) { logLine("# D3D11CreateDevice not found\n"); flushLog(); return 0; }

    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = create(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
    if (FAILED(hr)) hr = create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
    if (FAILED(hr) || !dev || !ctx) { logLine("# could not create a probe device (hr=%08x)\n", (unsigned)hr); return 0; }

    void** dv = *reinterpret_cast<void***>(dev);
    void** cv = *reinterpret_cast<void***>(ctx);
    // ID3D11Device: CreateTexture2D = slot 5, CreateShaderResourceView = slot 7.
    // ID3D11DeviceContext: PSSetShaderResources = slot 8, UpdateSubresource = slot 48.
    bool a = patchSlot(dv, 5, reinterpret_cast<void*>(&HK_CreateTexture2D), reinterpret_cast<void**>(&g_origCT2D));
    bool b = patchSlot(dv, 7, reinterpret_cast<void*>(&HK_CreateSRV), reinterpret_cast<void**>(&g_origCSRV));
    bool c = patchSlot(cv, 48, reinterpret_cast<void*>(&HK_UpdateSubresource), reinterpret_cast<void**>(&g_origUpd));
    bool d = patchSlot(cv, 8, reinterpret_cast<void*>(&HK_PSSetShaderResources), reinterpret_cast<void**>(&g_origPSSRV));
    logLine("# hooks: CreateTexture2D=%d CreateSRV=%d UpdateSubresource=%d PSSetShaderResources=%d\n", a, b, c, d);
    flushLog();
    ctx->Release();
    dev->Release();
    return 0;
}

extern "C" BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = h;
        DisableThreadLibraryCalls(h);
        HANDLE t = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        if (t) CloseHandle(t);
    } else if (reason == DLL_PROCESS_DETACH && g_logf) {
        stats("final");
        std::fclose(g_logf);
        g_logf = nullptr;
    }
    return TRUE;
}
