/* ============================================================================
   L'ORACLE : le SDK Windows installe, pas notre lecture de la documentation.

   Nous avons paye plusieurs erreurs de traduction P/Invoke que le compilateur
   C# acceptait sans broncher : une union transformee en deux champs, des tailles
   de structures approximees, un delegue ramasse par le GC. Aucune n'a leve
   d'exception — elles ont produit un programme qui se bloque, ou qui lit des
   valeurs plausibles a la mauvaise adresse.

   Ce fichier n'est PAS une bibliotheque DrDXT. C'est un outil de validation qui
   imprime ce que les en-tetes Windows disent VRAIMENT, pour qu'un test C# puisse
   comparer offset par offset.
   ============================================================================ */
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <stdio.h>

int main(void)
{
    printf("SDK_STRUCT EVENT_TRACE_LOGFILEW size=%zu\n", sizeof(EVENT_TRACE_LOGFILEW));
    printf("SDK_OFFSET LogFileName=%zu\n",         offsetof(EVENT_TRACE_LOGFILEW, LogFileName));
    printf("SDK_OFFSET LoggerName=%zu\n",          offsetof(EVENT_TRACE_LOGFILEW, LoggerName));
    printf("SDK_OFFSET CurrentTime=%zu\n",         offsetof(EVENT_TRACE_LOGFILEW, CurrentTime));
    printf("SDK_OFFSET BuffersRead=%zu\n",         offsetof(EVENT_TRACE_LOGFILEW, BuffersRead));
    printf("SDK_OFFSET ProcessTraceMode=%zu\n",    offsetof(EVENT_TRACE_LOGFILEW, ProcessTraceMode));
    printf("SDK_OFFSET CurrentEvent=%zu\n",        offsetof(EVENT_TRACE_LOGFILEW, CurrentEvent));
    printf("SDK_OFFSET LogfileHeader=%zu\n",       offsetof(EVENT_TRACE_LOGFILEW, LogfileHeader));
    printf("SDK_OFFSET PerfFreq=%zu\n",
           offsetof(EVENT_TRACE_LOGFILEW, LogfileHeader) + offsetof(TRACE_LOGFILE_HEADER, PerfFreq));
    printf("SDK_OFFSET BufferCallback=%zu\n",      offsetof(EVENT_TRACE_LOGFILEW, BufferCallback));
    printf("SDK_OFFSET BufferSize=%zu\n",          offsetof(EVENT_TRACE_LOGFILEW, BufferSize));
    printf("SDK_OFFSET EventRecordCallback=%zu\n", offsetof(EVENT_TRACE_LOGFILEW, EventRecordCallback));
    printf("SDK_OFFSET IsKernelTrace=%zu\n",       offsetof(EVENT_TRACE_LOGFILEW, IsKernelTrace));
    printf("SDK_OFFSET Context=%zu\n",             offsetof(EVENT_TRACE_LOGFILEW, Context));

    printf("SDK_STRUCT EVENT_TRACE_PROPERTIES size=%zu\n", sizeof(EVENT_TRACE_PROPERTIES));
    printf("SDK_OFFSET Wnode=%zu\n",               offsetof(EVENT_TRACE_PROPERTIES, Wnode));
    printf("SDK_OFFSET LogFileMode=%zu\n",         offsetof(EVENT_TRACE_PROPERTIES, LogFileMode));
    printf("SDK_OFFSET EnableFlags=%zu\n",         offsetof(EVENT_TRACE_PROPERTIES, EnableFlags));
    printf("SDK_OFFSET LoggerNameOffset=%zu\n",    offsetof(EVENT_TRACE_PROPERTIES, LoggerNameOffset));

    printf("SDK_STRUCT EVENT_RECORD size=%zu\n",   sizeof(EVENT_RECORD));
    printf("SDK_OFFSET Rec_BufferContext=%zu\n",   offsetof(EVENT_RECORD, BufferContext));
    printf("SDK_OFFSET Rec_UserDataLength=%zu\n",  offsetof(EVENT_RECORD, UserDataLength));
    printf("SDK_OFFSET Rec_UserData=%zu\n",        offsetof(EVENT_RECORD, UserData));
    printf("SDK_OFFSET Rec_TimeStamp=%zu\n",
           offsetof(EVENT_RECORD, EventHeader) + offsetof(EVENT_HEADER, TimeStamp));
    printf("SDK_OFFSET Rec_ProviderId=%zu\n",
           offsetof(EVENT_RECORD, EventHeader) + offsetof(EVENT_HEADER, ProviderId));
    printf("SDK_OFFSET Rec_Opcode=%zu\n",
           offsetof(EVENT_RECORD, EventHeader) + offsetof(EVENT_HEADER, EventDescriptor)
           + offsetof(EVENT_DESCRIPTOR, Opcode));

#ifdef SYSTEM_SCHEDULER_KW_CONTEXT_SWITCH
    printf("SDK_CONST SYSTEM_SCHEDULER_KW_CONTEXT_SWITCH=0x%llx\n",
           (unsigned long long)SYSTEM_SCHEDULER_KW_CONTEXT_SWITCH);
#else
    printf("SDK_CONST SYSTEM_SCHEDULER_KW_CONTEXT_SWITCH=ABSENT\n");
#endif
    printf("SDK_CONST EVENT_TRACE_FLAG_CSWITCH=0x%lx\n", (unsigned long)EVENT_TRACE_FLAG_CSWITCH);
    printf("SDK_CONST EVENT_TRACE_SYSTEM_LOGGER_MODE=0x%lx\n",
           (unsigned long)EVENT_TRACE_SYSTEM_LOGGER_MODE);
    return 0;
}
