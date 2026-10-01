#include <IOKit/IOKitLib.h>
#include <mach/mach.h>
#include <execinfo.h>
#include <stdio.h>
#include <stdlib.h>
#include <pthread.h>
static uint64_t footprint(void) { task_vm_info_data_t info; mach_msg_type_number_t count=TASK_VM_INFO_COUNT; if(task_info(mach_task_self(),TASK_VM_INFO,(task_info_t)&info,&count))return 0;return info.phys_footprint; }
static FILE *logfile;
static pthread_once_t once=PTHREAD_ONCE_INIT;
static void openLog(void) {const char *path=getenv("TRACE_IO_PATH");if(path)logfile=fopen(path,"w");}
static kern_return_t traced(io_connect_t conn,uint32_t sel,const uint64_t *input,uint32_t inputCnt,const void *inputStruct,size_t inputStructCnt,uint64_t *output,uint32_t *outputCnt,void *outputStruct,size_t *outputStructCnt) {
 uint64_t before=footprint();
 kern_return_t result=IOConnectCallMethod(conn,sel,input,inputCnt,inputStruct,inputStructCnt,output,outputCnt,outputStruct,outputStructCnt);
 uint64_t after=footprint();
 if(after>before+1024*1024){pthread_once(&once,openLog);if(logfile){void *frames[32];int n=backtrace(frames,32);char **symbols=backtrace_symbols(frames,n);flockfile(logfile);fprintf(logfile,"DELTA %lld selector=%u connection=%u\n",(long long)(after-before),sel,conn);for(int i=1;i<n;i++)fprintf(logfile,"%s\n",symbols[i]);fflush(logfile);funlockfile(logfile);free(symbols);}}
 return result;
}
__attribute__((used)) static struct {const void *replacement;const void *original;} interpose __attribute__((section("__DATA,__interpose"))) = {(void*)&traced,(void*)&IOConnectCallMethod};

__attribute__((constructor)) static void started(void) {pthread_once(&once,openLog);if(logfile){fprintf(logfile,"LOADED\n");fflush(logfile);}}
static void report(uint64_t before,const char *name,uint32_t selector) {uint64_t after=footprint();if(after>before+1024*1024 && logfile){void *frames[32];int n=backtrace(frames,32);char **symbols=backtrace_symbols(frames,n);flockfile(logfile);fprintf(logfile,"DELTA %lld %s selector=%u\n",(long long)(after-before),name,selector);for(int i=1;i<n;i++)fprintf(logfile,"%s\n",symbols[i]);fflush(logfile);funlockfile(logfile);free(symbols);}}
static kern_return_t trap0(io_connect_t c,uint32_t s){uint64_t before=footprint();kern_return_t r=IOConnectTrap0(c,s);report(before,"trap0",s);return r;}
__attribute__((used)) static struct {const void *replacement;const void *original;} trapInterpose0 __attribute__((section("__DATA,__interpose"))) = {(void*)&trap0,(void*)&IOConnectTrap0};
static kern_return_t trap1(io_connect_t c,uint32_t s,uintptr_t p0){uint64_t before=footprint();kern_return_t r=IOConnectTrap1(c,s,p0);report(before,"trap1",s);return r;}
__attribute__((used)) static struct {const void *replacement;const void *original;} trapInterpose1 __attribute__((section("__DATA,__interpose"))) = {(void*)&trap1,(void*)&IOConnectTrap1};
static kern_return_t trap2(io_connect_t c,uint32_t s,uintptr_t p0,uintptr_t p1){uint64_t before=footprint();kern_return_t r=IOConnectTrap2(c,s,p0,p1);report(before,"trap2",s);return r;}
__attribute__((used)) static struct {const void *replacement;const void *original;} trapInterpose2 __attribute__((section("__DATA,__interpose"))) = {(void*)&trap2,(void*)&IOConnectTrap2};
static kern_return_t trap3(io_connect_t c,uint32_t s,uintptr_t p0,uintptr_t p1,uintptr_t p2){uint64_t before=footprint();kern_return_t r=IOConnectTrap3(c,s,p0,p1,p2);report(before,"trap3",s);return r;}
__attribute__((used)) static struct {const void *replacement;const void *original;} trapInterpose3 __attribute__((section("__DATA,__interpose"))) = {(void*)&trap3,(void*)&IOConnectTrap3};
static kern_return_t trap4(io_connect_t c,uint32_t s,uintptr_t p0,uintptr_t p1,uintptr_t p2,uintptr_t p3){uint64_t before=footprint();kern_return_t r=IOConnectTrap4(c,s,p0,p1,p2,p3);report(before,"trap4",s);return r;}
__attribute__((used)) static struct {const void *replacement;const void *original;} trapInterpose4 __attribute__((section("__DATA,__interpose"))) = {(void*)&trap4,(void*)&IOConnectTrap4};
static kern_return_t trap5(io_connect_t c,uint32_t s,uintptr_t p0,uintptr_t p1,uintptr_t p2,uintptr_t p3,uintptr_t p4){uint64_t before=footprint();kern_return_t r=IOConnectTrap5(c,s,p0,p1,p2,p3,p4);report(before,"trap5",s);return r;}
__attribute__((used)) static struct {const void *replacement;const void *original;} trapInterpose5 __attribute__((section("__DATA,__interpose"))) = {(void*)&trap5,(void*)&IOConnectTrap5};
static kern_return_t trap6(io_connect_t c,uint32_t s,uintptr_t p0,uintptr_t p1,uintptr_t p2,uintptr_t p3,uintptr_t p4,uintptr_t p5){uint64_t before=footprint();kern_return_t r=IOConnectTrap6(c,s,p0,p1,p2,p3,p4,p5);report(before,"trap6",s);return r;}
__attribute__((used)) static struct {const void *replacement;const void *original;} trapInterpose6 __attribute__((section("__DATA,__interpose"))) = {(void*)&trap6,(void*)&IOConnectTrap6};
static kern_return_t structHook(mach_port_t c,uint32_t s,const void *in,size_t il,void *out,size_t *ol){uint64_t before=footprint();kern_return_t r=IOConnectCallStructMethod(c,s,in,il,out,ol);report(before,"struct",s);return r;}
__attribute__((used)) static struct {const void *replacement;const void *original;} structInterpose __attribute__((section("__DATA,__interpose"))) = {(void*)&structHook,(void*)&IOConnectCallStructMethod};
