#pragma once
#include "RuntimeSupport.h"
#include <xaudio2.h>

namespace h5runtime::movie {
inline uint64_t base=0,bink=0,current=0;
inline uint32_t advances=0,viewport=0,clockResult=0;
inline void reset(){advances=0;viewport=0;clockResult=0;}
inline void fullscreen(void* handle){
    if(viewport || current!=reinterpret_cast<uint64_t>(handle))return;auto object=reinterpret_cast<unsigned char*>(base+0x5eb2b90);auto header=reinterpret_cast<uint32_t*>(handle);auto rect=reinterpret_cast<float*>(object+48);
    bool initial=rect[0]==0 && rect[1]==1 && rect[2]==0 && rect[3]==1;
    bool centered=rect[0]>0 && rect[1]<1 && rect[2]>0 && rect[3]<1 && rect[0]+rect[1]==1 && rect[2]+rect[3]==1 && rect[1]-rect[0]==rect[3]-rect[2];
    if(*reinterpret_cast<void**>(object+16)!=handle || header[0]!=1920 || header[1]!=1080 || header[2]!=7966 || header[5]!=2997 || header[6]!=100 || *reinterpret_cast<uint32_t*>(object+32)!=0x380012 || !(initial || centered)){viewport=2;return;}
    *reinterpret_cast<uint32_t*>(object+32)&=~0x80000u;const float full[]={0,1,0,1};memcpy(object+48,full,sizeof(full));MemoryBarrier();viewport=1;
}
inline void alignClock(void* handle){
    if(current!=reinterpret_cast<uint64_t>(handle) || (clockResult && clockResult!=10))return;auto bytes=reinterpret_cast<unsigned char*>(handle);auto header=reinterpret_cast<uint32_t*>(handle);
    if(header[0]!=1920 || header[1]!=1080 || header[2]!=7966 || header[5]!=2997 || header[6]!=100 || *reinterpret_cast<uint32_t*>(bytes+0x150)!=3 || *reinterpret_cast<uint32_t*>(bytes+0x534)!=0){clockResult=2;return;}
    auto sounds=*reinterpret_cast<unsigned char**>(bytes+0x1a0);if(!sounds){clockResult=3;return;}uint64_t least=UINT64_MAX,most=0;bool ready=true;
    for(unsigned i=0;i<3;++i){auto sound=sounds+i*0x1c0;auto context=reinterpret_cast<unsigned char*>((reinterpret_cast<uintptr_t>(sound)+0x73)&~uintptr_t(7));
        if(*reinterpret_cast<uint32_t*>(sound+0x34)!=48000 || *reinterpret_cast<uint32_t*>(sound+0x38)!=16 || *reinterpret_cast<uint32_t*>(sound+0x3c)!=2 || !*reinterpret_cast<void**>(context+0x38)){clockResult=4;return;}
        XAUDIO2_VOICE_STATE state{};(*reinterpret_cast<IXAudio2SourceVoice**>(context+0x38))->GetState(&state,0);
        ready=ready && *reinterpret_cast<uint32_t*>(context+0x10)==1 && state.BuffersQueued && state.SamplesPlayed;if(state.SamplesPlayed<least)least=state.SamplesPlayed;if(state.SamplesPlayed>most)most=state.SamplesPlayed;
    }
    // Native preparation starts the video clock before renderer/audio startup.
    // Its zero epoch is explicitly supported by BinkWait's guarded native code.
    if(!advances){if(header[3]!=2 || *reinterpret_cast<uint32_t*>(bytes+0x528)!=1){clockResult=5;return;}*reinterpret_cast<uint32_t*>(bytes+0x53c)=0;MemoryBarrier();clockResult=10;return;}
    if(advances>30){clockResult=6;return;}if(!ready)return;
    if(header[3]!=advances+2 || *reinterpret_cast<uint32_t*>(bytes+0x528)!=advances+1 || most-least>480 || most>48000){clockResult=7;return;}
    if(*reinterpret_cast<uint32_t*>(bink+0x43b18)!=1){clockResult=8;return;}
    auto engine=*reinterpret_cast<IXAudio2**>(bink+0x43b10);auto master=*reinterpret_cast<IXAudio2MasteringVoice**>(bink+0x43b08);if(!engine || !master){clockResult=8;return;}
    XAUDIO2_VOICE_DETAILS details{};XAUDIO2_PERFORMANCE_DATA performance{};master->GetVoiceDetails(&details);engine->GetPerformanceData(&performance);
    if(details.InputSampleRate!=48000 || performance.CurrentLatencyInSamples>24000 || performance.GlitchesSinceEngineStarted){clockResult=9;return;}
    LARGE_INTEGER qpc,frequency;QueryPerformanceCounter(&qpc);QueryPerformanceFrequency(&frequency);if(frequency.QuadPart<=0){clockResult=9;return;}
    auto now=(uint64_t(qpc.QuadPart)*1000+uint64_t(frequency.QuadPart)/2-1)/uint64_t(frequency.QuadPart);
    auto epoch=uint32_t(now-(least*1000+24000)/48000+(uint64_t(performance.CurrentLatencyInSamples)*1000+details.InputSampleRate/2)/details.InputSampleRate);
    *reinterpret_cast<uint32_t*>(bytes+0x540)=0;*reinterpret_cast<uint32_t*>(bytes+0x53c)=epoch;MemoryBarrier();clockResult=1;
}
inline void beforeFrame(void* handle){__try{fullscreen(handle);}__except(EXCEPTION_EXECUTE_HANDLER){viewport=3;}}
inline void afterFrame(void* handle){__try{alignClock(handle);}__except(EXCEPTION_EXECUTE_HANDLER){clockResult=11;}if(current==reinterpret_cast<uint64_t>(handle))++advances;}
}
