#include <jni.h>
#include <string>
#include <thread>
#include <cstdio>
#include <cstdlib>
struct HelperApi { int version; const char* (*session)(); int (*capture)(const char*,const char*); int (*begin)(const char*); int (*complete)(const char*); void (*status)(const char*); void (*timer)(int); };
extern HelperApi bh_gateway;
static JavaVM* vm;static jclass bridge;
static JNIEnv* env(){JNIEnv* e=nullptr;if(vm->GetEnv((void**)&e,JNI_VERSION_1_6)!=JNI_OK)vm->AttachCurrentThread(&e,nullptr);return e;}
extern "C" JNIEXPORT jint JNI_OnLoad(JavaVM* v,void*){vm=v;auto e=env();bridge=(jclass)e->NewGlobalRef(e->FindClass("com/bazzarhelper/android/runtime/CaptureBridge"));char address[32];std::snprintf(address,sizeof(address),"%llx",static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(&bh_gateway)));setenv("BAZAARHELPER_BRIDGE_API",address,1);return JNI_VERSION_1_6;}
extern "C" JNIEXPORT void JNICALL Java_com_bazzarhelper_android_runtime_GameRuntime_exempt(JNIEnv* caller,jclass){
    bool success=false;
    // Resolve the boot-class API from an attached native thread, without an app Java caller.
    std::thread worker([&]{JNIEnv* e=nullptr;if(vm->AttachCurrentThread(&e,nullptr)!=JNI_OK)return;
        auto c=e->FindClass("dalvik/system/VMRuntime");
        if(c){auto get=e->GetStaticMethodID(c,"getRuntime","()Ldalvik/system/VMRuntime;");
            if(get){auto runtime=e->CallStaticObjectMethod(c,get);auto method=e->GetMethodID(c,"setHiddenApiExemptions","([Ljava/lang/String;)V");
                if(runtime&&method&&!e->ExceptionCheck()){auto text=e->NewStringUTF("L");auto strings=e->NewObjectArray(1,e->FindClass("java/lang/String"),text);e->CallVoidMethod(runtime,method,strings);success=!e->ExceptionCheck();}
            }
        }
        if(e->ExceptionCheck())e->ExceptionClear();vm->DetachCurrentThread();
    });worker.join();
    if(!success)caller->ThrowNew(caller->FindClass("java/lang/IllegalStateException"),"Cannot initialize game hosting on this Android version");
}
extern "C" __attribute__((visibility("default"))) const char* bh_session(){auto e=env();e->PushLocalFrame(8);auto id=e->GetStaticMethodID(bridge,"session","()Ljava/lang/String;");auto s=(jstring)e->CallStaticObjectMethod(bridge,id);static thread_local std::string value;value.clear();if(s){auto p=e->GetStringUTFChars(s,nullptr);value=p;e->ReleaseStringUTFChars(s,p);}if(e->ExceptionCheck())e->ExceptionClear();e->PopLocalFrame(nullptr);return value.c_str();}
static int call(const char* name,const char* a,const char* b=nullptr){auto e=env();e->PushLocalFrame(8);auto id=e->GetStaticMethodID(bridge,name,b?"(Ljava/lang/String;Ljava/lang/String;)Z":"(Ljava/lang/String;)Z");auto x=e->NewStringUTF(a?a:"");jboolean ok=b?e->CallStaticBooleanMethod(bridge,id,x,e->NewStringUTF(b)):e->CallStaticBooleanMethod(bridge,id,x);if(e->ExceptionCheck()){e->ExceptionClear();ok=false;}e->PopLocalFrame(nullptr);return ok;}
extern "C" __attribute__((visibility("default"))) int bh_capture(const char* stamp,const char* data){return call("capture",stamp,data);}
extern "C" __attribute__((visibility("default"))) int bh_begin(const char* data){return call("begin",data);}
extern "C" __attribute__((visibility("default"))) int bh_complete(const char* run){return call("complete",run);}
extern "C" __attribute__((visibility("default"))) void bh_status(const char* s){auto e=env();e->PushLocalFrame(8);e->CallStaticVoidMethod(bridge,e->GetStaticMethodID(bridge,"status","(Ljava/lang/String;)V"),e->NewStringUTF(s));if(e->ExceptionCheck())e->ExceptionClear();e->PopLocalFrame(nullptr);}

extern "C" __attribute__((visibility("default"))) void bh_timer(int ms){auto e=env();e->PushLocalFrame(4);e->CallStaticVoidMethod(bridge,e->GetStaticMethodID(bridge,"timer","(I)V"),static_cast<jint>(ms));if(e->ExceptionCheck())e->ExceptionClear();e->PopLocalFrame(nullptr);}

HelperApi bh_gateway{2,bh_session,bh_capture,bh_begin,bh_complete,bh_status,bh_timer};
