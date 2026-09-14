#pragma once
#include "RuntimeSupport.h"
#include <cmath>
#include <intrin.h>
namespace h5runtime::display {
struct DisplayValues {
 float fov=78; volatile LONG fovReads=0,fovWrites=0,paceHits=0,presentHits=0;
 uint64_t property=0,profileType=0;uint32_t initThread=0,lastEnum=0;
 float lastPeriod=0;volatile LONG saveError=0,clockHits=0;uint32_t lastMicroseconds=0;
 wchar_t configPath[32768]{};
};
inline DisplayValues DisplayState;
inline uint64_t image=0;
inline uint64_t base(){return image;}
void* GetCampaignFov(void*,void* output){
 auto p=(unsigned char*)output;
 // Match the native scalar getter's new GenericValue initialization.
 *(uint64_t*)p=0;*(uint32_t*)(p+8)=0xffff0003;
 *(float*)p=DisplayState.fov;InterlockedIncrement(&DisplayState.fovReads);return output;
}
void SetCampaignFov(void*,const void* value){
 float f=*(const float*)value;if(!std::isfinite(f))return;
 f=floorf(f+0.5f);if(f<60)f=60;if(f>120)f=120;
 DisplayState.fov=f;*(float*)(base()+0x590e210)=f;
 InterlockedIncrement(&DisplayState.fovWrites);
 wchar_t text[16];wsprintfW(text,L"%u",(unsigned)f);
 DisplayState.saveError=WritePrivateProfileStringW(L"Campaign",L"FieldOfView",text,DisplayState.configPath)?0:(LONG)GetLastError();
}
static int initialize(){
 auto manager=*(uint64_t*)(base()+0x6222428);auto typeId=*(uint32_t*)(base()+0x6417b24);
 if(!manager||typeId!=74)return 10;
 auto type=*(uint64_t*)(*(uint64_t*)(manager+0x48)+typeId*8);
 if(!type||strcmp(*(char**)(type+0x60),"PlayerProfile"))return 11;
 auto begin=*(uint64_t**)(type+8),end=*(uint64_t**)(type+16);if(end<begin||end-begin>512)return 12;
 for(auto p=begin;p!=end;++p)if(!strcmp(*(char**)(*p+8),"CampaignFieldOfView"))return 13;
 auto property=((uint64_t(*)(size_t))(base()+0x1c8b430))(0x50);if(!property)return 14;
 ((void(*)(uint64_t,const char*,int,int))(base()+0x1e11050))(property,"CampaignFieldOfView",1,3);
 *(uint64_t*)property=base()+0x3842b50;
 *(void**)(property+0x40)=(void*)GetCampaignFov;*(void**)(property+0x48)=(void*)SetCampaignFov;
 ((void(*)(uint64_t,uint64_t))(base()+0x1f32f50))(type,property);
 DisplayState.property=property;DisplayState.profileType=type;
 *(float*)(base()+0x590e210)=DisplayState.fov;
 return 0;
}
static LONG CALLBACK trap(EXCEPTION_POINTERS* x){
 if(x->ExceptionRecord->ExceptionCode!=EXCEPTION_BREAKPOINT)return EXCEPTION_CONTINUE_SEARCH;
 auto a=(uint64_t)x->ExceptionRecord->ExceptionAddress;auto c=x->ContextRecord;auto&s=DisplayState;
 if(a==base()+0x1551ecf){
  int option=(int)c->R14;
  if(option==3||option==4){float period=1.0f/(option==3?144.0f:180.0f);memcpy(&c->Xmm10.Low,&period,4);s.lastPeriod=period;s.lastEnum=option;InterlockedIncrement(&s.paceHits);}
  c->Rdi=(uint64_t)(int64_t)*(int32_t*)(c->Rcx+8);c->Rip=a+4;return EXCEPTION_CONTINUE_EXECUTION;
 }
 if(a==base()+0x152b28e){
  uint32_t interval=(uint32_t)c->Rbx;if(interval==3||interval==4){interval=1;InterlockedIncrement(&s.presentHits);}
  c->Rdx=interval;c->Rip=a+2;return EXCEPTION_CONTINUE_EXECUTION;
 }
 if(a==base()+0x155f483){
  // Original cmp/jg assumes that every value above 1 means 30 FPS.
  int option=*(int*)(c->Rcx+0xac);
  if(option==3||option==4){*(unsigned char*)(base()+0x614168a)=0;c->Rip=base()+0x155f4f1;}
  else c->Rip=base()+(option>1?0x155f4ea:0x155f48c);
  // Both original paths overwrite flags before using them.
  return EXCEPTION_CONTINUE_EXECUTION;
 }
 if(a==base()+0xad5217){
  // Native metronome measures real elapsed time in microseconds. Preserve its
  // wait/deadline and elapsed-time accounting; change only its normal 60 Hz
  // period. Explicit cinematic/engine 30 Hz modes keep their original period.
  int option=*(int*)(base()+0x614194c);
  if((option==3||option==4)&&c->Rdi==16666){
   c->Rdi=option==3?6944:5556;s.lastMicroseconds=(uint32_t)c->Rdi;InterlockedIncrement(&s.clockHits);
  }
  c->Rcx=__readgsqword(0x58);c->Rip=a+9;return EXCEPTION_CONTINUE_EXECUTION;
 }
 return EXCEPTION_CONTINUE_SEARCH;
}
 }
