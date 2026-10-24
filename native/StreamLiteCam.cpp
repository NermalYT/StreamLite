// StreamLite Camera: a Windows 11 virtual camera media source (MFCreateVirtualCamera).
// Windows' Frame Server loads this DLL and asks it for frames; StreamLite publishes its processed video (NV12, 1080p)
// in the shared memory section "Global\StreamLiteCamFrame". When StreamLite isn't sending, a dark placeholder is shown.
// Structure follows smourier/VCamSample (MIT), rewritten as plain COM without WIL/WinRT.
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <initguid.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>


// ---------------------------------------------------------------- declarations missing from the mingw headers

DEFINE_GUID(CLSID_StreamLiteCam, 0x7d1f6a52, 0x3b9e, 0x4c3a, 0x9f, 0x61, 0x2e, 0x5a, 0x8c, 0x47, 0xd3, 0x19);
DEFINE_GUID(SL_MF_DEVICESTREAM_STREAM_CATEGORY, 0x2939e7b8, 0xa62e, 0x4579, 0xb6, 0x74, 0xd4, 0x07, 0x3d, 0xfa, 0xbb, 0xba);
DEFINE_GUID(SL_MF_DEVICESTREAM_STREAM_ID, 0x11bd5120, 0xd124, 0x446b, 0x88, 0xe6, 0x17, 0x06, 0x02, 0x57, 0xff, 0xf9);
DEFINE_GUID(SL_MF_DEVICESTREAM_FRAMESERVER_SHARED, 0x1cb378e9, 0xb279, 0x41d4, 0xaf, 0x97, 0x34, 0xa2, 0x43, 0xe6, 0x83, 0x20);
DEFINE_GUID(SL_MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, 0x17145fd1, 0x1b2b, 0x423c, 0x80, 0x01, 0x2b, 0x68, 0x33, 0xed, 0x35, 0x88);
DEFINE_GUID(SL_MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 0xf0273718, 0x4a4d, 0x4ac5, 0xa1, 0x5d, 0x30, 0x5e, 0xb5, 0xe9, 0x06, 0x67);
DEFINE_GUID(SL_MFT_TRANSFORM_CLSID_Attribute, 0x6821c42b, 0x65a4, 0x4e82, 0x99, 0xbc, 0x9a, 0x88, 0x20, 0x5e, 0xcd, 0x0c);
DEFINE_GUID(SL_PINNAME_VIDEO_CAPTURE, 0xfb6c4281, 0x0353, 0x11d1, 0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba);
DEFINE_GUID(SL_MFSampleExtension_Token, 0x8294da66, 0xf328, 0x4805, 0xb5, 0x51, 0x00, 0xde, 0xb4, 0xc5, 0x7a, 0x61);
DEFINE_GUID(IID_SL_IMFMediaStream2, 0xc5bc37d6, 0x75c7, 0x46a1, 0xa1, 0x32, 0x81, 0xb5, 0xf7, 0x23, 0xc2, 0x0f);
DEFINE_GUID(IID_SL_IMFSampleAllocatorControl, 0xda62b958, 0x3a38, 0x4a97, 0xbd, 0x27, 0x14, 0x9c, 0x64, 0x0c, 0x07, 0x71);
DEFINE_GUID(IID_SL_IKsControl, 0x28f54685, 0x06fd, 0x11d2, 0xb2, 0x7a, 0x00, 0xa0, 0xc9, 0x22, 0x31, 0x96);

static const GUID SL_GUID_NULL = {};
#define GUID_NULL SL_GUID_NULL

enum SL_MF_STREAM_STATE { SL_STREAM_STOPPED = 0, SL_STREAM_PAUSED = 1, SL_STREAM_RUNNING = 2 };

struct SL_IMFMediaStream2 : public IMFMediaStream
{
    virtual HRESULT STDMETHODCALLTYPE SetStreamState(int value) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetStreamState(int* value) = 0;
};

struct SL_IMFSampleAllocatorControl : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, int* usage) = 0;
};

struct SL_IKsControl : public IUnknown
{
    virtual LONG STDMETHODCALLTYPE KsProperty(void* property, ULONG length, void* data, ULONG dataLength, ULONG* returned) = 0;
    virtual LONG STDMETHODCALLTYPE KsMethod(void* method, ULONG length, void* data, ULONG dataLength, ULONG* returned) = 0;
    virtual LONG STDMETHODCALLTYPE KsEvent(void* evt, ULONG length, void* data, ULONG dataLength, ULONG* returned) = 0;
};

static const LONG KS_NOT_FOUND = (LONG)0x80070492;   // HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND)

static HMODULE g_module;
static volatile LONG g_objects;

// ---------------------------------------------------------------- shared frame from StreamLite

// Layout written by StreamLite (VirtualCamera.cs): 64-byte header, then NV12 width x height.
struct SharedHeader
{
    UINT32 magic;          // 'SLC1'
    UINT32 width, height;
    volatile LONG seq;     // odd while StreamLite is writing
    UINT64 frameTick;      // GetTickCount64 of the last frame
    UINT64 consumerTick;   // written here: a client is watching
    UINT32 consumerWidth, consumerHeight;
    UINT32 reserved[6];
};
static const UINT32 Magic = 0x31434C53;

struct SharedFrame
{
    HANDLE map = nullptr;
    BYTE* view = nullptr;
    ULONGLONG nextTry = 0, staleSince = 0;
    BYTE* copy = nullptr;
    UINT32 copyW = 0, copyH = 0;

    bool Open()
    {
        if (view) return true;
        ULONGLONG now = GetTickCount64();
        if (now < nextTry) return false;
        nextTry = now + 1000;
        map = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, L"Global\\StreamLiteCamFrame");
        if (!map)
        {
            // StreamLite without admin rights can't create Global\ sections: it shares a temporary file instead.
            wchar_t path[MAX_PATH];
            if (!ExpandEnvironmentStringsW(L"%ProgramData%\\StreamLite\\cam\\frame.bin", path, MAX_PATH)) return false;
            HANDLE f = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (f == INVALID_HANDLE_VALUE) return false;
            map = CreateFileMappingW(f, nullptr, PAGE_READWRITE, 0, 0, nullptr);
            CloseHandle(f);
            if (!map) return false;
        }
        view = (BYTE*)MapViewOfFile(map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0);
        if (!view) { CloseHandle(map); map = nullptr; return false; }
        return true;
    }

    void Unmap()
    {
        if (view) UnmapViewOfFile(view);
        if (map) CloseHandle(map);
        view = nullptr; map = nullptr;
    }

    void Close()
    {
        Unmap();
        if (copy) VirtualFree(copy, 0, MEM_RELEASE);
        copy = nullptr; copyW = copyH = 0;
    }

    // Copies the newest frame (consistent snapshot); false when none is fresh.
    bool Latest(UINT32 wantW, UINT32 wantH, UINT32* w, UINT32* h)
    {
        if (!Open()) return false;
        auto hdr = (SharedHeader*)view;
        hdr->consumerTick = GetTickCount64();
        hdr->consumerWidth = wantW; hdr->consumerHeight = wantH;
        if (hdr->magic != Magic || hdr->width == 0 || hdr->height == 0 || hdr->width > 3840 || hdr->height > 2160) return false;
        if (GetTickCount64() - hdr->frameTick > 1500)
        {
            // Nothing new for a while: StreamLite may have restarted with the other kind of sharing (section <-> file),
            // so look again instead of watching a mapping nobody writes to any more.
            ULONGLONG t = GetTickCount64();
            if (!staleSince) staleSince = t;
            else if (t - staleSince > 3000) { Unmap(); staleSince = 0; }
            return false;
        }
        staleSince = 0;
        UINT32 fw = hdr->width, fh = hdr->height;
        SIZE_T size = (SIZE_T)fw * fh * 3 / 2;
        if (fw != copyW || fh != copyH)
        {
            if (copy) VirtualFree(copy, 0, MEM_RELEASE);
            copy = (BYTE*)VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (!copy) { copyW = copyH = 0; return false; }
            copyW = fw; copyH = fh;
        }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            LONG s1 = hdr->seq;
            if (s1 & 1) { Sleep(1); continue; }
            MemoryBarrier();
            memcpy(copy, view + 64, size);
            MemoryBarrier();
            if (hdr->seq == s1) break;
        }
        *w = fw; *h = fh;
        return true;
    }
};

// Bilinear resize of one plane (for clients that pick a smaller size than StreamLite produces).
static void ResizePlane(const BYTE* src, UINT32 sw, UINT32 sh, UINT32 spitch, BYTE* dst, UINT32 dw, UINT32 dh, UINT32 dpitch, int comps)
{
    if (sw == dw && sh == dh)
    {
        for (UINT32 y = 0; y < dh; y++) memcpy(dst + (SIZE_T)y * dpitch, src + (SIZE_T)y * spitch, (SIZE_T)dw * comps);
        return;
    }
    for (UINT32 y = 0; y < dh; y++)
    {
        float fy = ((y + 0.5f) * sh / dh) - 0.5f; if (fy < 0) fy = 0;
        UINT32 y0 = (UINT32)fy, y1 = y0 + 1 < sh ? y0 + 1 : y0; float ay = fy - y0;
        const BYTE* r0 = src + (SIZE_T)y0 * spitch; const BYTE* r1 = src + (SIZE_T)y1 * spitch;
        BYTE* d = dst + (SIZE_T)y * dpitch;
        for (UINT32 x = 0; x < dw; x++)
        {
            float fx = ((x + 0.5f) * sw / dw) - 0.5f; if (fx < 0) fx = 0;
            UINT32 x0 = (UINT32)fx, x1 = x0 + 1 < sw ? x0 + 1 : x0; float ax = fx - x0;
            for (int c = 0; c < comps; c++)
            {
                float a = r0[x0 * comps + c] * (1 - ax) + r0[x1 * comps + c] * ax;
                float b = r1[x0 * comps + c] * (1 - ax) + r1[x1 * comps + c] * ax;
                d[x * comps + c] = (BYTE)(a * (1 - ay) + b * ay + 0.5f);
            }
        }
    }
}

// Placeholder: dark slate with a soft vignette (StreamLite isn't sending video).
static void Placeholder(BYTE* y, UINT32 pitch, BYTE* uv, UINT32 w, UINT32 h)
{
    for (UINT32 r = 0; r < h; r++)
    {
        BYTE* row = y + (SIZE_T)r * pitch;
        float dy = (r - h * 0.5f) / (h * 0.5f);
        for (UINT32 c = 0; c < w; c++)
        {
            float dx = (c - w * 0.5f) / (w * 0.5f);
            float v = 46 - 14 * (dx * dx + dy * dy);
            row[c] = (BYTE)(v < 18 ? 18 : v);
        }
    }
    for (UINT32 r = 0; r < h / 2; r++)
    {
        BYTE* row = uv + (SIZE_T)r * pitch;
        for (UINT32 c = 0; c < w / 2; c++) { row[2 * c] = 134; row[2 * c + 1] = 124; }
    }
}

// ---------------------------------------------------------------- IMFAttributes delegation

#define DELEGATE_ATTRIBUTES(A) \
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return A->GetItem(k, v); } \
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return A->GetItemType(k, t); } \
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return A->CompareItem(k, v, r); } \
    STDMETHODIMP Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return A->Compare(t, m, r); } \
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return A->GetUINT32(k, v); } \
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return A->GetUINT64(k, v); } \
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return A->GetDouble(k, v); } \
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return A->GetGUID(k, v); } \
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* v) override { return A->GetStringLength(k, v); } \
    STDMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 n, UINT32* l) override { return A->GetString(k, v, n, l); } \
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* v, UINT32* l) override { return A->GetAllocatedString(k, v, l); } \
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* v) override { return A->GetBlobSize(k, v); } \
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 n, UINT32* l) override { return A->GetBlob(k, b, n, l); } \
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* l) override { return A->GetAllocatedBlob(k, b, l); } \
    STDMETHODIMP GetUnknown(REFGUID k, REFIID i, LPVOID* v) override { return A->GetUnknown(k, i, v); } \
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return A->SetItem(k, v); } \
    STDMETHODIMP DeleteItem(REFGUID k) override { return A->DeleteItem(k); } \
    STDMETHODIMP DeleteAllItems() override { return A->DeleteAllItems(); } \
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return A->SetUINT32(k, v); } \
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return A->SetUINT64(k, v); } \
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return A->SetDouble(k, v); } \
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return A->SetGUID(k, v); } \
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return A->SetString(k, v); } \
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 n) override { return A->SetBlob(k, b, n); } \
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* v) override { return A->SetUnknown(k, v); } \
    STDMETHODIMP LockStore() override { return A->LockStore(); } \
    STDMETHODIMP UnlockStore() override { return A->UnlockStore(); } \
    STDMETHODIMP GetCount(UINT32* n) override { return A->GetCount(n); } \
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return A->GetItemByIndex(i, k, v); } \
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return A->CopyAllItems(d); }

#define IMPLEMENT_REFCOUNT \
    volatile LONG refs = 1; \
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs); } \
    STDMETHODIMP_(ULONG) Release() override { LONG r = InterlockedDecrement(&refs); if (r == 0) delete this; return r; }

struct Lock
{
    CRITICAL_SECTION* cs;
    explicit Lock(CRITICAL_SECTION* c) : cs(c) { EnterCriticalSection(cs); }
    ~Lock() { LeaveCriticalSection(cs); }
};

template <class T> static void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

// ---------------------------------------------------------------- stream

static const UINT32 Sizes[][2] = { { 1920, 1080 }, { 1280, 720 }, { 640, 360 } };
static const int NumSizes = 3;

class MediaStream : public SL_IMFMediaStream2, public IMFAttributes, public SL_IKsControl
{
public:
    IMPLEMENT_REFCOUNT
    IMFAttributes* attrs = nullptr;
    IMFMediaEventQueue* queue = nullptr;
    IMFStreamDescriptor* descriptor = nullptr;
    IMFMediaSource* source = nullptr;          // weak (the source owns the stream)
    IMFVideoSampleAllocatorEx* allocator = nullptr;
    CRITICAL_SECTION cs;
    int state = SL_STREAM_STOPPED;
    UINT32 width = 1920, height = 1080;
    // Pacing thread: one sample per request, at most 30 per second.
    HANDLE thread = nullptr, wake = nullptr;
    volatile bool quit = false;
    IUnknown* tokens[16];
    int tokenCount = 0, pending = 0;
    SharedFrame shared;

    MediaStream() { InitializeCriticalSection(&cs); InterlockedIncrement(&g_objects); }
    ~MediaStream()
    {
        StopThread();
        shared.Close();
        SafeRelease(allocator); SafeRelease(descriptor); SafeRelease(queue); SafeRelease(attrs);
        DeleteCriticalSection(&cs);
        InterlockedDecrement(&g_objects);
    }

    HRESULT Initialize(IMFMediaSource* src)
    {
        source = src;
        HRESULT hr = MFCreateAttributes(&attrs, 8);
        if (FAILED(hr)) return hr;
        attrs->SetGUID(SL_MF_DEVICESTREAM_STREAM_CATEGORY, SL_PINNAME_VIDEO_CAPTURE);
        attrs->SetUINT32(SL_MF_DEVICESTREAM_STREAM_ID, 0);
        attrs->SetUINT32(SL_MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
        attrs->SetUINT32(SL_MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, 1);   // color
        hr = MFCreateEventQueue(&queue);
        if (FAILED(hr)) return hr;
        IMFMediaType* types[NumSizes] = {};
        for (int i = 0; i < NumSizes && SUCCEEDED(hr); i++)
        {
            hr = MFCreateMediaType(&types[i]);
            if (FAILED(hr)) break;
            UINT32 w = Sizes[i][0], h = Sizes[i][1];
            types[i]->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            types[i]->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            types[i]->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            types[i]->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
            MFSetAttributeSize(types[i], MF_MT_FRAME_SIZE, w, h);
            types[i]->SetUINT32(MF_MT_DEFAULT_STRIDE, w);
            MFSetAttributeRatio(types[i], MF_MT_FRAME_RATE, 30, 1);
            MFSetAttributeRatio(types[i], MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            types[i]->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)(w * h * 12 * 30));
            types[i]->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
        }
        if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(0, NumSizes, types, &descriptor);
        if (SUCCEEDED(hr))
        {
            IMFMediaTypeHandler* handler = nullptr;
            hr = descriptor->GetMediaTypeHandler(&handler);
            if (SUCCEEDED(hr)) { hr = handler->SetCurrentMediaType(types[0]); handler->Release(); }
        }
        for (auto t : types) if (t) t->Release();
        return hr;
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaStream || riid == IID_SL_IMFMediaStream2) *ppv = static_cast<SL_IMFMediaStream2*>(this);
        else if (riid == IID_IMFAttributes) *ppv = static_cast<IMFAttributes*>(this);
        else if (riid == IID_SL_IKsControl) *ppv = static_cast<SL_IKsControl*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }

    DELEGATE_ATTRIBUTES(attrs)

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** e) override { IMFMediaEventQueue* q; { Lock l(&cs); q = queue; if (!q) return MF_E_SHUTDOWN; q->AddRef(); } HRESULT hr = q->GetEvent(flags, e); q->Release(); return hr; }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* st) override { Lock l(&cs); return queue ? queue->BeginGetEvent(cb, st) : MF_E_SHUTDOWN; }
    STDMETHODIMP EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) override { Lock l(&cs); return queue ? queue->EndGetEvent(r, e) : MF_E_SHUTDOWN; }
    STDMETHODIMP QueueEvent(MediaEventType t, REFGUID g, HRESULT s, const PROPVARIANT* v) override { Lock l(&cs); return queue ? queue->QueueEventParamVar(t, g, s, v) : MF_E_SHUTDOWN; }

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** s) override { if (!s) return E_POINTER; if (!source) return MF_E_SHUTDOWN; *s = source; source->AddRef(); return S_OK; }
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** d) override { if (!d) return E_POINTER; Lock l(&cs); if (!descriptor) return MF_E_SHUTDOWN; *d = descriptor; descriptor->AddRef(); return S_OK; }

    STDMETHODIMP RequestSample(IUnknown* token) override
    {
        Lock l(&cs);
        if (!queue) return MF_E_SHUTDOWN;
        if (state != SL_STREAM_RUNNING) return MF_E_MEDIA_SOURCE_WRONGSTATE;
        if (tokenCount < 16) { tokens[tokenCount++] = token; if (token) token->AddRef(); }
        else pending++;
        return S_OK;
    }

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(int value) override
    {
        if (value == state) return S_OK;
        if (value == SL_STREAM_RUNNING) return Start(nullptr);
        if (value == SL_STREAM_STOPPED) return Stop();
        if (value == SL_STREAM_PAUSED) { if (state != SL_STREAM_RUNNING) return MF_E_INVALID_STATE_TRANSITION; state = value; return S_OK; }
        return MF_E_INVALID_STATE_TRANSITION;
    }
    STDMETHODIMP GetStreamState(int* value) override { if (!value) return E_POINTER; *value = state; return S_OK; }

    // IKsControl: no camera properties exposed.
    STDMETHODIMP_(LONG) KsProperty(void*, ULONG, void*, ULONG, ULONG* r) override { if (r) *r = 0; return KS_NOT_FOUND; }
    STDMETHODIMP_(LONG) KsMethod(void*, ULONG, void*, ULONG, ULONG* r) override { if (r) *r = 0; return KS_NOT_FOUND; }
    STDMETHODIMP_(LONG) KsEvent(void*, ULONG, void*, ULONG, ULONG* r) override { if (r) *r = 0; return KS_NOT_FOUND; }

    HRESULT SetAllocator(IUnknown* a)
    {
        Lock l(&cs);
        SafeRelease(allocator);
        return a ? a->QueryInterface(IID_IMFVideoSampleAllocatorEx, (void**)&allocator) : E_POINTER;
    }

    HRESULT Start(IMFMediaType* type)
    {
        Lock l(&cs);
        if (!queue) return MF_E_SHUTDOWN;
        if (type)
        {
            UINT32 w = 0, h = 0;
            if (SUCCEEDED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &w, &h)) && w && h) { width = w; height = h; }
        }
        else
        {
            IMFMediaTypeHandler* handler = nullptr; IMFMediaType* cur = nullptr;
            if (descriptor && SUCCEEDED(descriptor->GetMediaTypeHandler(&handler)) && SUCCEEDED(handler->GetCurrentMediaType(&cur)))
            {
                UINT32 w = 0, h = 0;
                if (SUCCEEDED(MFGetAttributeSize(cur, MF_MT_FRAME_SIZE, &w, &h)) && w && h) { width = w; height = h; }
            }
            SafeRelease(cur); SafeRelease(handler);
        }
        if (allocator)
        {
            IMFMediaType* t = type;
            IMFMediaTypeHandler* handler = nullptr; IMFMediaType* cur = nullptr;
            if (!t && descriptor && SUCCEEDED(descriptor->GetMediaTypeHandler(&handler)) && SUCCEEDED(handler->GetCurrentMediaType(&cur))) t = cur;
            HRESULT hr = t ? allocator->InitializeSampleAllocator(10, t) : E_FAIL;
            SafeRelease(cur); SafeRelease(handler);
            if (FAILED(hr)) return hr;
        }
        state = SL_STREAM_RUNNING;
        StartThread();
        return queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
    }

    HRESULT Stop()
    {
        StopThread();
        Lock l(&cs);
        if (!queue) return MF_E_SHUTDOWN;
        if (allocator) allocator->UninitializeSampleAllocator();
        for (int i = 0; i < tokenCount; i++) if (tokens[i]) tokens[i]->Release();
        tokenCount = 0; pending = 0;
        state = SL_STREAM_STOPPED;
        return queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }

    void Shutdown()
    {
        StopThread();
        Lock l(&cs);
        if (queue) queue->Shutdown();
        SafeRelease(queue);
        SafeRelease(descriptor);
        SafeRelease(allocator);
        for (int i = 0; i < tokenCount; i++) if (tokens[i]) tokens[i]->Release();
        tokenCount = 0;
        source = nullptr;
        state = SL_STREAM_STOPPED;
    }

    void StartThread()
    {
        if (thread) return;
        quit = false;
        wake = CreateWaitableTimerW(nullptr, FALSE, nullptr);
        AddRef();
        thread = CreateThread(nullptr, 0, [](LPVOID p) -> DWORD { ((MediaStream*)p)->Run(); ((MediaStream*)p)->Release(); return 0; }, this, 0, nullptr);
        if (!thread) Release();
    }

    void StopThread()
    {
        HANDLE t = thread;
        if (!t) return;
        quit = true;
        if (GetCurrentThreadId() != GetThreadId(t)) WaitForSingleObject(t, 2000);
        CloseHandle(t);
        thread = nullptr;
        if (wake) { CloseHandle(wake); wake = nullptr; }
    }

    void Run()
    {
        LARGE_INTEGER due; due.QuadPart = -333333;   // first tick after 33 ms
        SetWaitableTimer(wake, &due, 0, nullptr, nullptr, FALSE);
        LONGLONG next = MFGetSystemTime() + 333333;
        while (!quit)
        {
            WaitForSingleObject(wake, 100);
            if (quit) break;
            Deliver();
            next += 333333;
            LONGLONG now = MFGetSystemTime();
            if (next < now - 1000000) next = now + 333333;   // fell far behind: resync
            due.QuadPart = -(next - now > 0 ? next - now : 1);
            SetWaitableTimer(wake, &due, 0, nullptr, nullptr, FALSE);
        }
    }

    void Deliver()
    {
        IUnknown* token = nullptr;
        IMFVideoSampleAllocatorEx* alloc = nullptr;
        IMFMediaEventQueue* q = nullptr;
        UINT32 w, h;
        {
            Lock l(&cs);
            if (state != SL_STREAM_RUNNING || !queue || (tokenCount == 0 && pending == 0)) return;
            if (tokenCount > 0)
            {
                token = tokens[0];
                for (int i = 1; i < tokenCount; i++) tokens[i - 1] = tokens[i];
                tokenCount--;
            }
            else pending--;
            alloc = allocator; if (alloc) alloc->AddRef();
            q = queue; q->AddRef();
            w = width; h = height;
        }
        IMFSample* sample = nullptr;
        HRESULT hr;
        if (alloc) hr = alloc->AllocateSample(&sample);
        else
        {
            hr = MFCreateSample(&sample);
            IMFMediaBuffer* mb = nullptr;
            if (SUCCEEDED(hr)) hr = MFCreateMemoryBuffer(w * h * 3 / 2, &mb);
            if (SUCCEEDED(hr)) { mb->SetCurrentLength(w * h * 3 / 2); hr = sample->AddBuffer(mb); }
            SafeRelease(mb);
        }
        if (SUCCEEDED(hr)) hr = Fill(sample, w, h);
        if (SUCCEEDED(hr))
        {
            sample->SetSampleTime(MFGetSystemTime());
            sample->SetSampleDuration(333333);
            if (token) sample->SetUnknown(SL_MFSampleExtension_Token, token);
            q->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample);
        }
        SafeRelease(sample); SafeRelease(token); SafeRelease(alloc); SafeRelease(q);
    }

    HRESULT Fill(IMFSample* sample, UINT32 w, UINT32 h)
    {
        IMFMediaBuffer* buffer = nullptr;
        HRESULT hr = sample->GetBufferByIndex(0, &buffer);
        if (FAILED(hr)) return hr;
        IMF2DBuffer2* b2 = nullptr;
        BYTE* scan0 = nullptr; LONG pitch = 0; BYTE* start = nullptr; DWORD len = 0;
        bool locked2d = false;
        if (SUCCEEDED(buffer->QueryInterface(IID_IMF2DBuffer2, (void**)&b2)) &&
            SUCCEEDED(b2->Lock2DSize(MF2DBuffer_LockFlags_Write, &scan0, &pitch, &start, &len)))
            locked2d = true;
        else
        {
            DWORD max = 0;
            hr = buffer->Lock(&scan0, &max, nullptr);
            pitch = (LONG)w;
            if (FAILED(hr)) { SafeRelease(b2); buffer->Release(); return hr; }
        }
        if (pitch < 0) pitch = -pitch;   // NV12 is top-down
        BYTE* yPlane = scan0;
        BYTE* uvPlane = scan0 + (SIZE_T)pitch * h;
        UINT32 fw = 0, fh = 0;
        if (shared.Latest(w, h, &fw, &fh))
        {
            ResizePlane(shared.copy, fw, fh, fw, yPlane, w, h, (UINT32)pitch, 1);
            ResizePlane(shared.copy + (SIZE_T)fw * fh, fw / 2, fh / 2, fw, uvPlane, w / 2, h / 2, (UINT32)pitch, 2);
        }
        else Placeholder(yPlane, (UINT32)pitch, uvPlane, w, h);
        if (locked2d) b2->Unlock2D(); else { buffer->Unlock(); buffer->SetCurrentLength(w * h * 3 / 2); }
        SafeRelease(b2);
        buffer->Release();
        return S_OK;
    }
};

// ---------------------------------------------------------------- source

class MediaSource : public IMFMediaSourceEx, public IMFAttributes, public IMFGetService, public SL_IKsControl, public SL_IMFSampleAllocatorControl
{
public:
    IMPLEMENT_REFCOUNT
    IMFAttributes* attrs = nullptr;
    IMFMediaEventQueue* queue = nullptr;
    IMFPresentationDescriptor* descriptor = nullptr;
    MediaStream* stream = nullptr;
    CRITICAL_SECTION cs;

    MediaSource() { InitializeCriticalSection(&cs); InterlockedIncrement(&g_objects); }
    ~MediaSource()
    {
        if (stream) { stream->Shutdown(); stream->Release(); }
        SafeRelease(descriptor); SafeRelease(queue); SafeRelease(attrs);
        DeleteCriticalSection(&cs);
        InterlockedDecrement(&g_objects);
    }

    HRESULT Initialize(IMFAttributes* from)
    {
        HRESULT hr = MFCreateAttributes(&attrs, 8);
        if (FAILED(hr)) return hr;
        if (from) from->CopyAllItems(attrs);
        stream = new MediaStream();
        hr = stream->Initialize(static_cast<IMFMediaSourceEx*>(this));
        if (FAILED(hr)) return hr;
        IMFStreamDescriptor* sd = nullptr;
        hr = stream->GetStreamDescriptor(&sd);
        if (SUCCEEDED(hr)) hr = MFCreatePresentationDescriptor(1, &sd, &descriptor);
        SafeRelease(sd);
        if (SUCCEEDED(hr)) hr = MFCreateEventQueue(&queue);
        return hr;
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaSource || riid == IID_IMFMediaSourceEx) *ppv = static_cast<IMFMediaSourceEx*>(this);
        else if (riid == IID_IMFAttributes) *ppv = static_cast<IMFAttributes*>(this);
        else if (riid == IID_IMFGetService) *ppv = static_cast<IMFGetService*>(this);
        else if (riid == IID_SL_IKsControl) *ppv = static_cast<SL_IKsControl*>(this);
        else if (riid == IID_SL_IMFSampleAllocatorControl) *ppv = static_cast<SL_IMFSampleAllocatorControl*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }

    DELEGATE_ATTRIBUTES(attrs)

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** e) override { IMFMediaEventQueue* q; { Lock l(&cs); q = queue; if (!q) return MF_E_SHUTDOWN; q->AddRef(); } HRESULT hr = q->GetEvent(flags, e); q->Release(); return hr; }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* st) override { Lock l(&cs); return queue ? queue->BeginGetEvent(cb, st) : MF_E_SHUTDOWN; }
    STDMETHODIMP EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) override { Lock l(&cs); return queue ? queue->EndGetEvent(r, e) : MF_E_SHUTDOWN; }
    STDMETHODIMP QueueEvent(MediaEventType t, REFGUID g, HRESULT s, const PROPVARIANT* v) override { Lock l(&cs); return queue ? queue->QueueEventParamVar(t, g, s, v) : MF_E_SHUTDOWN; }

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* c) override { if (!c) return E_POINTER; *c = MFMEDIASOURCE_IS_LIVE; return S_OK; }
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** pd) override { if (!pd) return E_POINTER; Lock l(&cs); return descriptor ? descriptor->Clone(pd) : MF_E_SHUTDOWN; }
    STDMETHODIMP Pause() override { return MF_E_INVALID_STATE_TRANSITION; }

    STDMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPos) override
    {
        if (!pd || !startPos) return E_POINTER;
        if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
        Lock l(&cs);
        if (!queue || !descriptor || !stream) return MF_E_SHUTDOWN;
        DWORD count = 0;
        HRESULT hr = pd->GetStreamDescriptorCount(&count);
        if (FAILED(hr)) return hr;
        if (count != 1) return E_INVALIDARG;
        BOOL selected = FALSE; IMFStreamDescriptor* sd = nullptr;
        hr = pd->GetStreamDescriptorByIndex(0, &selected, &sd);
        if (FAILED(hr)) return hr;
        int st = 0; stream->GetStreamState(&st);
        bool running = st != SL_STREAM_STOPPED;
        if (selected && !running)
        {
            descriptor->SelectStream(0);
            queue->QueueEventParamUnk(MENewStream, GUID_NULL, S_OK, static_cast<SL_IMFMediaStream2*>(stream));
            IMFMediaTypeHandler* handler = nullptr; IMFMediaType* type = nullptr;
            if (SUCCEEDED(sd->GetMediaTypeHandler(&handler))) handler->GetCurrentMediaType(&type);
            hr = stream->Start(type);
            SafeRelease(type); SafeRelease(handler);
        }
        else if (!selected && running)
        {
            descriptor->DeselectStream(0);
            stream->Stop();
        }
        SafeRelease(sd);
        if (FAILED(hr)) return hr;
        PROPVARIANT time; PropVariantInit(&time);
        time.vt = VT_I8; time.hVal.QuadPart = MFGetSystemTime();
        return queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
    }

    STDMETHODIMP Stop() override
    {
        Lock l(&cs);
        if (!queue || !descriptor || !stream) return MF_E_SHUTDOWN;
        stream->Stop();
        descriptor->DeselectStream(0);
        PROPVARIANT time; PropVariantInit(&time);
        time.vt = VT_I8; time.hVal.QuadPart = MFGetSystemTime();
        return queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &time);
    }

    STDMETHODIMP Shutdown() override
    {
        Lock l(&cs);
        if (!queue) return MF_E_SHUTDOWN;
        queue->Shutdown();
        SafeRelease(queue);
        if (stream) stream->Shutdown();
        SafeRelease(descriptor);
        return S_OK;
    }

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** a) override { if (!a) return E_POINTER; *a = static_cast<IMFAttributes*>(this); AddRef(); return S_OK; }
    STDMETHODIMP GetStreamAttributes(DWORD id, IMFAttributes** a) override
    {
        if (!a) return E_POINTER;
        if (id != 0 || !stream) { *a = nullptr; return E_INVALIDARG; }
        *a = static_cast<IMFAttributes*>(stream); stream->AddRef(); return S_OK;
    }
    STDMETHODIMP SetD3DManager(IUnknown*) override { return S_OK; }   // CPU samples only

    // IMFGetService
    STDMETHODIMP GetService(REFGUID, REFIID, LPVOID* p) override { if (p) *p = nullptr; return MF_E_UNSUPPORTED_SERVICE; }

    // IKsControl
    STDMETHODIMP_(LONG) KsProperty(void*, ULONG, void*, ULONG, ULONG* r) override { if (r) *r = 0; return KS_NOT_FOUND; }
    STDMETHODIMP_(LONG) KsMethod(void*, ULONG, void*, ULONG, ULONG* r) override { if (r) *r = 0; return KS_NOT_FOUND; }
    STDMETHODIMP_(LONG) KsEvent(void*, ULONG, void*, ULONG, ULONG* r) override { if (r) *r = 0; return KS_NOT_FOUND; }

    // IMFSampleAllocatorControl
    STDMETHODIMP SetDefaultAllocator(DWORD id, IUnknown* a) override { if (id != 0 || !stream) return E_INVALIDARG; return stream->SetAllocator(a); }
    STDMETHODIMP GetAllocatorUsage(DWORD id, DWORD* input, int* usage) override
    {
        if (!input || !usage) return E_POINTER;
        if (id != 0) return E_INVALIDARG;
        *input = id; *usage = 0;   // MFSampleAllocatorUsage_UsesProvidedAllocator
        return S_OK;
    }
};

// ---------------------------------------------------------------- activator

class Activator : public IMFActivate
{
public:
    IMPLEMENT_REFCOUNT
    IMFAttributes* attrs = nullptr;
    MediaSource* source = nullptr;

    Activator() { InterlockedIncrement(&g_objects); }
    ~Activator() { if (source) source->Release(); SafeRelease(attrs); InterlockedDecrement(&g_objects); }

    HRESULT Initialize()
    {
        HRESULT hr = MFCreateAttributes(&attrs, 4);
        if (FAILED(hr)) return hr;
        attrs->SetUINT32(SL_MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1);
        attrs->SetGUID(SL_MFT_TRANSFORM_CLSID_Attribute, CLSID_StreamLiteCam);
        source = new MediaSource();
        return source->Initialize(attrs);
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFAttributes || riid == IID_IMFActivate) { *ppv = static_cast<IMFActivate*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }

    DELEGATE_ATTRIBUTES(attrs)

    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (!source) { *ppv = nullptr; return MF_E_SHUTDOWN; }
        return source->QueryInterface(riid, ppv);
    }
    STDMETHODIMP ShutdownObject() override { return S_OK; }
    STDMETHODIMP DetachObject() override { if (source) { source->Release(); source = nullptr; } return S_OK; }
};

// ---------------------------------------------------------------- COM plumbing

class ClassFactory : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = this; return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto a = new Activator();
        HRESULT hr = a->Initialize();
        if (SUCCEEDED(hr)) hr = a->QueryInterface(riid, ppv);
        a->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&g_objects); else InterlockedDecrement(&g_objects); return S_OK; }
};
static ClassFactory g_factory;

extern "C" BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = h; DisableThreadLibraryCalls(h); }
    return TRUE;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_StreamLiteCam) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

static const wchar_t* KeyPath = L"Software\\Classes\\CLSID\\{7D1F6A52-3B9E-4C3A-9F61-2E5A8C47D319}";

extern "C" HRESULT __stdcall DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t sub[256];
    lstrcpyW(sub, KeyPath); lstrcatW(sub, L"\\InprocServer32");
    HKEY key;
    LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, sub, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    RegSetValueExW(key, nullptr, 0, REG_SZ, (const BYTE*)path, (lstrlenW(path) + 1) * 2);
    RegSetValueExW(key, L"ThreadingModel", 0, REG_SZ, (const BYTE*)L"Both", 10);
    RegCloseKey(key);
    return S_OK;
}

extern "C" HRESULT __stdcall DllUnregisterServer()
{
    LONG r = RegDeleteTreeW(HKEY_LOCAL_MACHINE, KeyPath);
    return r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(r);
}
