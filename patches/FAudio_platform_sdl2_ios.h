/* Local iOS adaptation for FAudio 22.09: mix independent FAudio engines into
 * SDL's single permitted iOS output. Engine ownership remains unchanged.
 * Included only by FAudio_platform_sdl2.c; no allocation in the audio callback.
 */
typedef struct FAudioIOSClient
{
    FAudio *audio;
    struct FAudioIOSClient *next;
} FAudioIOSClient;

static struct
{
    SDL_AudioDeviceID device;
    SDL_AudioSpec spec;
    FAudioIOSClient *clients;
    Uint8 *scratch;
} FAudioIOSOutput;
static SDL_SpinLock FAudioIOSOutputLock;
static uint32_t iosCallbacks, iosOversized;
static float iosPeak;
/* v27 diagnostics: per-client max peak over the read window (pre-mix), reset on each read. */
#define FAUDIO_IOS_MAX_DIAG_CLIENTS 4
static float iosClientMaxPeak[FAUDIO_IOS_MAX_DIAG_CLIENTS];
static float iosMixMaxPeak;

FAUDIOAPI void FAudio_IOS_GetDebugState(uint32_t *callbacks, uint32_t *oversized, uint32_t *clients, float *peak)
{
    FAudioIOSClient *c; uint32_t count=0;
    SDL_AtomicLock(&FAudioIOSOutputLock);
    if(FAudioIOSOutput.device) SDL_LockAudioDevice(FAudioIOSOutput.device);
    for(c=FAudioIOSOutput.clients;c;c=c->next)count++;
    *callbacks=iosCallbacks; *oversized=iosOversized; *clients=count; *peak=iosPeak;
    if(FAudioIOSOutput.device) SDL_UnlockAudioDevice(FAudioIOSOutput.device);
    SDL_AtomicUnlock(&FAudioIOSOutputLock);
}


/* v27: human-readable per-engine state for the trace: active, master, voices, playing voices, max peaks since last read. */
FAUDIOAPI int FAudio_IOS_GetDebugState2(char *buf, int buflen)
{
    FAudioIOSClient *c; int index = 0, used = 0;
    if (!buf || buflen <= 0) return 0;
    buf[0] = 0;
    SDL_AtomicLock(&FAudioIOSOutputLock);
    if (FAudioIOSOutput.device) SDL_LockAudioDevice(FAudioIOSOutput.device);
    used += SDL_snprintf(buf + used, buflen - used, "dev=%u %dHz %uch fmt=0x%x samples=%u mixMax=%.6f |",
        (unsigned) FAudioIOSOutput.device, FAudioIOSOutput.spec.freq, FAudioIOSOutput.spec.channels,
        FAudioIOSOutput.spec.format, FAudioIOSOutput.spec.samples, iosMixMaxPeak);
    iosMixMaxPeak = 0;
    for (c = FAudioIOSOutput.clients; c && used < buflen; c = c->next, index++)
    {
        FAudio *a = c->audio; LinkedList *l; unsigned voices = 0, playing = 0, buffered = 0;
        char detail[512]; int dused = 0; detail[0] = 0;
        FAudio_PlatformLockMutex(a->sourceLock);
        for (l = a->sources; l; l = l->next) {
            FAudioSourceVoice *v = (FAudioSourceVoice*) l->entry; voices++;
            if (v->src.active == 1) playing++;
            if (v->src.bufferList) buffered++;
            /* v29: first two playing voices: volume, channel volume, sends, format, ratio, decode progress */
            if (v->src.active == 1 && playing <= 2 && dused < (int) sizeof(detail)) {
                dused += SDL_snprintf(detail + dused, sizeof(detail) - dused,
                    " [vol=%.3f ch0=%.3f outCh=%u sends=%u tag=0x%x %uch %uHz bits=%u ratio=%.3f total=%llu off=%u]",
                    v->volume, v->channelVolume ? v->channelVolume[0] : -1.0f, v->outputChannels, v->sends.SendCount,
                    v->src.format ? v->src.format->wFormatTag : 0, v->src.format ? v->src.format->nChannels : 0,
                    v->src.format ? v->src.format->nSamplesPerSec : 0, v->src.format ? v->src.format->wBitsPerSample : 0,
                    v->src.freqRatio, (unsigned long long) v->src.totalSamples, v->src.curBufferOffset);
            }
        }
        FAudio_PlatformUnlockMutex(a->sourceLock);
        used += SDL_snprintf(buf + used, buflen - used, " e%d active=%u master=%d masterVol=%.3f %uHz/%uch upd=%u voices=%u playing=%u withBuffers=%u max=%.6f%s |",
            index, a->active, a->master != NULL, a->master ? a->master->volume : -1.0f, a->mixFormat.Format.nSamplesPerSec, a->mixFormat.Format.nChannels, a->updateSize,
            voices, playing, buffered, index < FAUDIO_IOS_MAX_DIAG_CLIENTS ? iosClientMaxPeak[index] : -1.0f, detail);
        if (index < FAUDIO_IOS_MAX_DIAG_CLIENTS) iosClientMaxPeak[index] = 0;
    }
    if (FAudioIOSOutput.device) SDL_UnlockAudioDevice(FAudioIOSOutput.device);
    SDL_AtomicUnlock(&FAudioIOSOutputLock);
    return used;
}

static void FAudioIOSMix(void *userdata, Uint8 *stream, int len)
{
    FAudioIOSClient *client;
    (void) userdata;
    SDL_memset(stream, 0, len);
    iosCallbacks++;
    if ((Uint32) len > FAudioIOSOutput.spec.size) { iosOversized++; return; }
    {
    int index = 0;
    for (client = FAudioIOSOutput.clients; client; client = client->next, index++)
    {
        if (!client->audio->active) continue;
        SDL_memset(FAudioIOSOutput.scratch, 0, len);
        FAudio_INTERNAL_UpdateEngine(client->audio, (float*) FAudioIOSOutput.scratch);
        if (index < FAUDIO_IOS_MAX_DIAG_CLIENTS) {
            const float *s = (const float*) FAudioIOSOutput.scratch; int n = len / (int) sizeof(float), i; float p = iosClientMaxPeak[index];
            for (i = 0; i < n; i++) { float v = s[i] < 0 ? -s[i] : s[i]; if (v > p) p = v; }
            iosClientMaxPeak[index] = p;
        }
        SDL_MixAudioFormat(stream, FAudioIOSOutput.scratch, AUDIO_F32, len, SDL_MIX_MAXVOLUME);
    }
    }
    {
        const float *s = (const float*) stream; int n = len / (int) sizeof(float), i; float p = iosMixMaxPeak;
        for (i = 0; i < n; i++) { float v = s[i] < 0 ? -s[i] : s[i]; if (v > p) p = v; }
        iosMixMaxPeak = p;
    }
    if ((iosCallbacks & 63) == 0) {
        float peak=0; int i; float *samples=(float*)stream;
        for(i=0;i<len/(int)sizeof(float);i++){float value=samples[i];if(value<0)value=-value;if(value>peak)peak=value;}
        iosPeak=peak;
    }
}

static void FAudioIOSInit(FAudio *audio, uint32_t flags, uint32_t deviceIndex,
    FAudioWaveFormatExtensible *mixFormat, uint32_t *updateSize, void **platformDevice)
{
    FAudioIOSClient *client;
    SDL_AudioSpec want;
    *platformDevice = NULL;
    client = (FAudioIOSClient*) SDL_malloc(sizeof(FAudioIOSClient));
    if (!client) return;
    client->audio = audio;
    SDL_AtomicLock(&FAudioIOSOutputLock);
    if (!FAudioIOSOutput.device)
    {
        SDL_zero(want);
        want.freq = mixFormat->Format.nSamplesPerSec > 0 ? mixFormat->Format.nSamplesPerSec : 48000;
        want.channels = mixFormat->Format.nChannels > 0 ? mixFormat->Format.nChannels : 2;
        want.format = AUDIO_F32;
        want.samples = (flags & FAUDIO_1024_QUANTUM) ?
            (int) (want.freq / (1000.0 / (64.0 / 3.0))) : want.freq / 100;
        want.callback = FAudioIOSMix;
        /* SDL opens paused: publish clients and scratch before starting it. */
        FAudioIOSOutput.device = SDL_OpenAudioDevice(
            deviceIndex > 0 ? SDL_GetAudioDeviceName(deviceIndex - 1, 0) : NULL,
            0, &want, &FAudioIOSOutput.spec, 0);
        if (!FAudioIOSOutput.device)
        {
            SDL_Log("FAudio iOS shared output failed: %s", SDL_GetError());
            SDL_AtomicUnlock(&FAudioIOSOutputLock);
            SDL_free(client);
            return;
        }
        FAudioIOSOutput.scratch = (Uint8*) SDL_malloc(FAudioIOSOutput.spec.size);
        if (!FAudioIOSOutput.scratch)
        {
            SDL_CloseAudioDevice(FAudioIOSOutput.device);
            SDL_zero(FAudioIOSOutput);
            SDL_AtomicUnlock(&FAudioIOSOutputLock);
            SDL_free(client);
            return;
        }
        SDL_Log("FAudio iOS shared output opened: %d Hz, %u channels", FAudioIOSOutput.spec.freq, FAudioIOSOutput.spec.channels);
    }
    SDL_LockAudioDevice(FAudioIOSOutput.device);
    WriteWaveFormatExtensible(mixFormat, FAudioIOSOutput.spec.channels,
        FAudioIOSOutput.spec.freq, &DATAFORMAT_SUBTYPE_IEEE_FLOAT);
    *updateSize = FAudioIOSOutput.spec.samples;
    client->next = FAudioIOSOutput.clients;
    FAudioIOSOutput.clients = client;
    *platformDevice = client;
    SDL_UnlockAudioDevice(FAudioIOSOutput.device);
    SDL_PauseAudioDevice(FAudioIOSOutput.device, 0);
    SDL_AtomicUnlock(&FAudioIOSOutputLock);
}

static void FAudioIOSQuit(void *platformDevice)
{
    FAudioIOSClient *client = (FAudioIOSClient*) platformDevice;
    FAudioIOSClient **slot;
    if (!client) return;
    SDL_AtomicLock(&FAudioIOSOutputLock);
    SDL_LockAudioDevice(FAudioIOSOutput.device);
    for (slot = &FAudioIOSOutput.clients; *slot && *slot != client; slot = &(*slot)->next) { }
    if (*slot) *slot = client->next;
    SDL_UnlockAudioDevice(FAudioIOSOutput.device);
    SDL_free(client);
    if (!FAudioIOSOutput.clients)
    {
        SDL_CloseAudioDevice(FAudioIOSOutput.device);
        SDL_free(FAudioIOSOutput.scratch);
        SDL_zero(FAudioIOSOutput);
        SDL_Log("FAudio iOS shared output closed");
    }
    SDL_AtomicUnlock(&FAudioIOSOutputLock);
}
