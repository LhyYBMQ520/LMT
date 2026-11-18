#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <tlhelp32.h>
#include <wrl.h>
#include <atomic>
#include <thread>
#include <vector>
#include <set>
#include <memory>
#include <algorithm>

using namespace Microsoft::WRL;
using PcmCallback = void(__cdecl*)(const short*, unsigned);
using ErrorCallback = void(__cdecl*)(int);

class Activation final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, FtmBase, IActivateAudioInterfaceCompletionHandler> {
public:
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HRESULT result = E_PENDING;
    ComPtr<IAudioClient> client;
    ~Activation() { if (ready) CloseHandle(ready); }
    STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation) override {
        ComPtr<IUnknown> unknown;
        HRESULT activation = E_FAIL;
        result = operation->GetActivateResult(&activation, &unknown);
        if (SUCCEEDED(result)) result = activation;
        if (SUCCEEDED(result)) result = unknown.As(&client);
        SetEvent(ready);
        return S_OK;
    }
};
struct Capture {
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE initialized = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::thread worker;
    std::atomic<HRESULT> result{E_PENDING};
    ~Capture() { if (worker.joinable()) { SetEvent(stop); worker.join(); } CloseHandle(stop); CloseHandle(initialized); }
};
static HRESULT Run(Capture* capture, DWORD pid, PcmCallback pcm) {
    HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return HRESULT_FROM_WIN32(GetLastError());
    struct Handle { HANDLE value; ~Handle(){ if(value)CloseHandle(value); } } processGuard{process};
    if (WaitForSingleObject(process, 0) == WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);
    AUDIOCLIENT_ACTIVATION_PARAMS params{};
    params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    params.ProcessLoopbackParams.TargetProcessId = pid;
    // EXCLUDE would capture OTHER applications. Never use it as a single-PID mode.
    params.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
    PROPVARIANT variant{};
    variant.vt = VT_BLOB;
    variant.blob.cbSize = sizeof(params);
    variant.blob.pBlobData = reinterpret_cast<BYTE*>(&params);
    auto handler = Make<Activation>();
    ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
    HRESULT hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &variant, handler.Get(), &operation);
    if (FAILED(hr)) return hr;
    HANDLE activationHandles[] = {capture->stop, handler->ready, process};
    DWORD wait = WaitForMultipleObjects(3, activationHandles, FALSE, 10000);
    if (wait == WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
    if (wait == WAIT_OBJECT_0+2) return HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);
    if (wait != WAIT_OBJECT_0+1) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    if (FAILED(handler->result)) return handler->result;
    auto client = handler->client;
    WAVEFORMATEX format{};
    format.wFormatTag=WAVE_FORMAT_PCM;format.nChannels=2;format.nSamplesPerSec=48000;
    format.wBitsPerSample=16;format.nBlockAlign=4;format.nAvgBytesPerSec=192000;
    hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 200000, 0, &format, nullptr);
    if (FAILED(hr)) return hr;
    Handle audio{CreateEventW(nullptr,FALSE,FALSE,nullptr)};
    if (!audio.value) return HRESULT_FROM_WIN32(GetLastError());
    if (FAILED(hr=client->SetEventHandle(audio.value))) return hr;
    ComPtr<IAudioCaptureClient> reader;
    if (FAILED(hr=client->GetService(IID_PPV_ARGS(&reader)))) return hr;
    if (FAILED(hr=client->Start())) return hr;
    capture->result = S_OK;SetEvent(capture->initialized);
    HANDLE events[] = {capture->stop, process, audio.value};
    std::vector<short> silence;
    for (;;) {
        wait=WaitForMultipleObjects(3,events,FALSE,2000);
        if (wait==WAIT_OBJECT_0) { hr=S_OK;break; }
        if (wait==WAIT_OBJECT_0+1) { hr=HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);break; }
        if (wait==WAIT_TIMEOUT) continue;
        if (wait!=WAIT_OBJECT_0+2) { hr=HRESULT_FROM_WIN32(GetLastError());break; }
        UINT32 packets=0;
        if (FAILED(hr=reader->GetNextPacketSize(&packets))) break;
        while (packets) {
            BYTE* data=nullptr;UINT32 frames=0;DWORD flags=0;
            if (FAILED(hr=reader->GetBuffer(&data,&frames,&flags,nullptr,nullptr))) break;
            if (flags&AUDCLNT_BUFFERFLAGS_SILENT) { silence.assign(static_cast<size_t>(frames)*2,0);pcm(silence.data(),frames); }
            else if (data) pcm(reinterpret_cast<const short*>(data),frames);
            hr=reader->ReleaseBuffer(frames);
            if (FAILED(hr)) break;
            if (FAILED(hr=reader->GetNextPacketSize(&packets))) break;
        }
        if (FAILED(hr)) break;
    }
    client->Stop();
    return hr;
}
extern "C" __declspec(dllexport) int __cdecl lmt_start(unsigned pid, PcmCallback pcm, ErrorCallback error, Capture** output) {
    if (!output||!pcm||!error||!pid) return E_INVALIDARG;
    *output=nullptr;
    auto capture=std::make_unique<Capture>();
    if (!capture->stop||!capture->initialized) return E_OUTOFMEMORY;
    auto ptr=capture.get();
    try {
        ptr->worker=std::thread([ptr,pid,pcm,error] {
            HRESULT init=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
            HRESULT result=init;
            try { if(SUCCEEDED(init)) result=Run(ptr,pid,pcm); } catch (...) { result=E_OUTOFMEMORY; }
            if (ptr->result==E_PENDING) { ptr->result=result;SetEvent(ptr->initialized); }
            else if (FAILED(result)&&WaitForSingleObject(ptr->stop,0)!=WAIT_OBJECT_0) error(result);
            if (SUCCEEDED(init)) CoUninitialize();
        });
        if (WaitForSingleObject(ptr->initialized,15000)!=WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        if (FAILED(ptr->result)) return ptr->result;
        *output=capture.release();return S_OK;
    } catch (...) { return E_OUTOFMEMORY; }
}
extern "C" __declspec(dllexport) void __cdecl lmt_stop(Capture* capture) { delete capture; }

// hasAudio flags: 1 = an unexpired audio session, 2 = a running audio stream.
struct ProcessInfo { unsigned pid; unsigned parent; int hasAudio; wchar_t name[260]; };
extern "C" __declspec(dllexport) int __cdecl lmt_processes(ProcessInfo* output,int capacity) {
    HRESULT init=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    std::set<DWORD> audioPids, activePids;
    ComPtr<IMMDeviceEnumerator> enumerator;
    if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator)))) {
        ComPtr<IMMDeviceCollection> devices;
        if(SUCCEEDED(enumerator->EnumAudioEndpoints(eRender,DEVICE_STATE_ACTIVE,&devices))) {
            UINT count=0;devices->GetCount(&count);
            for(UINT i=0;i<count;i++) {
                ComPtr<IMMDevice> device;ComPtr<IAudioSessionManager2> manager;ComPtr<IAudioSessionEnumerator> sessions;
                if(FAILED(devices->Item(i,&device))||FAILED(device->Activate(__uuidof(IAudioSessionManager2),CLSCTX_ALL,nullptr,&manager))||FAILED(manager->GetSessionEnumerator(&sessions)))continue;
                int total=0;sessions->GetCount(&total);
                for(int n=0;n<total;n++) {ComPtr<IAudioSessionControl> control;ComPtr<IAudioSessionControl2> control2;DWORD pid=0;AudioSessionState state;
                    if(SUCCEEDED(sessions->GetSession(n,&control))&&SUCCEEDED(control.As(&control2))&&SUCCEEDED(control->GetState(&state))&&state!=AudioSessionStateExpired&&SUCCEEDED(control2->GetProcessId(&pid))&&pid){
                        audioPids.insert(pid);
                        if(state==AudioSessionStateActive)activePids.insert(pid);
                    }
                }
            }
        }
    }
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);int count=0;
    if(snapshot!=INVALID_HANDLE_VALUE) {PROCESSENTRY32W entry{};entry.dwSize=sizeof(entry);
        if(Process32FirstW(snapshot,&entry))do{if(!entry.th32ProcessID)continue;if(output&&count<capacity){auto& item=output[count];item.pid=entry.th32ProcessID;item.parent=entry.th32ParentProcessID;item.hasAudio=(audioPids.count(item.pid)?1:0)|(activePids.count(item.pid)?2:0);wcsncpy_s(item.name,entry.szExeFile,_TRUNCATE);}count++;}while(Process32NextW(snapshot,&entry));CloseHandle(snapshot);
    }
    // Release COM objects before balancing initialization on this thread.
    enumerator.Reset();if(SUCCEEDED(init))CoUninitialize();return count;
}
