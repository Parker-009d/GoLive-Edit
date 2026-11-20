#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <new>

// Minimal VST 2.x ABI declarations. The processor is hosted by OBS's bundled
// VST 2.x audio filter; state is transferred as its standard float parameter
// block, so GoLive can set controls through OBS WebSocket.
struct AEffect;
using VstIntPtr = intptr_t;
using AudioMasterCallback = VstIntPtr (*)(AEffect *, int32_t, int32_t, VstIntPtr, void *, float);
using DispatcherProc = VstIntPtr (*)(AEffect *, int32_t, int32_t, VstIntPtr, void *, float);
using ProcessProc = void (*)(AEffect *, float **, float **, int32_t);
using SetParameterProc = void (*)(AEffect *, int32_t, float);
using GetParameterProc = float (*)(AEffect *, int32_t);

struct AEffect {
    int32_t magic;
    DispatcherProc dispatcher;
    ProcessProc process;
    SetParameterProc setParameter;
    GetParameterProc getParameter;
    int32_t numPrograms;
    int32_t numParams;
    int32_t numInputs;
    int32_t numOutputs;
    int32_t flags;
    void *reserved1;
    void *reserved2;
    int32_t initialDelay;
    int32_t realQualities;
    int32_t offQualities;
    float ioRatio;
    void *object;
    void *user;
    int32_t uniqueID;
    int32_t version;
    ProcessProc processReplacing;
    void (*processDoubleReplacing)(AEffect *, double **, double **, int32_t);
    char future[56];
};

namespace {
constexpr int kMagic = ('V' << 24) | ('s' << 16) | ('t' << 8) | 'P';
constexpr int kCanReplacing = 1 << 4;
constexpr int kGetEffectName = 45;
constexpr int kGetVendorString = 47;
constexpr int kGetProductString = 48;
constexpr int kGetVendorVersion = 49;
constexpr int kCanDo = 51;
constexpr int kGetVstVersion = 58;
constexpr int kGetPlugCategory = 35;
constexpr int kOpen = 0;
constexpr int kClose = 1;
constexpr int kSetSampleRate = 10;
constexpr int kSetBlockSize = 11;
constexpr int kMainsChanged = 12;
constexpr int kGetChunk = 23;
constexpr int kSetChunk = 24;
constexpr int kEffect = 1;
constexpr int kSlots = 4;
constexpr int kParamsPerSlot = 10;
constexpr int kEffectParamCount = kSlots * kParamsPerSlot;
constexpr int kGateParamCount = 6;
constexpr int kEQBandCount = 5;
constexpr int kEQEnableParam = kEffectParamCount + kGateParamCount;
constexpr int kEQBandParamStart = kEQEnableParam + 1;
constexpr int kParamsPerEQBand = 5;
constexpr int kParamCount = kEQBandParamStart + kEQBandCount * kParamsPerEQBand;
constexpr int kMaxDelay = 96000;
constexpr int kMaxComb = 6000;
constexpr float kPi = 3.14159265358979323846f;

struct SlotDSP {
    std::array<std::array<float, kMaxDelay + 1>, 2> delay{};
    std::array<std::array<float, kMaxComb>, 8> comb{};
    std::array<std::array<float, 2048>, 4> allpass{};
    std::array<float, 2> lowState{};
    std::array<float, 2> highState{};
    std::array<float, 2> dampState{};
    std::array<float, 2> hpState{};
    std::array<float, 2> previousInput{};
    std::array<int, 2> delayWrite{};
    std::array<std::array<int, 4>, 2> combWrite{};
    std::array<std::array<int, 2>, 2> allpassWrite{};
    std::array<float, 2> gateEnvelope{};
    std::array<float, 2> gateGain{1.0f, 1.0f};
    std::array<int, 2> gateHoldFrames{};
    float phase = 0.0f;
};

struct ParametricBiquad {
    float b0 = 1.0f, b1 = 0.0f, b2 = 0.0f, a1 = 0.0f, a2 = 0.0f;
    float z1 = 0.0f, z2 = 0.0f;

    void configure(int type, float frequency, float gainDb, float q, float sampleRate) {
        const float f = std::min(sampleRate * 0.45f, std::max(10.0f, frequency));
        const float w = 2.0f * kPi * f / sampleRate;
        const float cosine = std::cos(w), sine = std::sin(w);
        const float effectiveQ = type == 3 ? q * (1.0f + std::abs(gainDb) / 9.0f) : q;
        const float alpha = sine / (2.0f * std::max(0.1f, effectiveQ));
        const float a = std::pow(10.0f, gainDb / 40.0f);
        float nb0 = 1.0f, nb1 = 0.0f, nb2 = 0.0f, na0 = 1.0f, na1 = 0.0f, na2 = 0.0f;
        switch (type) {
        case 0: // low cut
            nb0 = (1.0f + cosine) * 0.5f; nb1 = -(1.0f + cosine); nb2 = nb0;
            na0 = 1.0f + alpha; na1 = -2.0f * cosine; na2 = 1.0f - alpha;
            break;
        case 1: { // low shelf
            const float beta = 2.0f * std::sqrt(a) * alpha;
            nb0 = a * ((a + 1.0f) - (a - 1.0f) * cosine + beta);
            nb1 = 2.0f * a * ((a - 1.0f) - (a + 1.0f) * cosine);
            nb2 = a * ((a + 1.0f) - (a - 1.0f) * cosine - beta);
            na0 = (a + 1.0f) + (a - 1.0f) * cosine + beta;
            na1 = -2.0f * ((a - 1.0f) + (a + 1.0f) * cosine);
            na2 = (a + 1.0f) + (a - 1.0f) * cosine - beta;
            break;
        }
        case 2: // peaking EQ
        case 3: // variable-Q peaking EQ
            nb0 = 1.0f + alpha * a; nb1 = -2.0f * cosine; nb2 = 1.0f - alpha * a;
            na0 = 1.0f + alpha / a; na1 = -2.0f * cosine; na2 = 1.0f - alpha / a;
            break;
        case 4: { // high shelf
            const float beta = 2.0f * std::sqrt(a) * alpha;
            nb0 = a * ((a + 1.0f) + (a - 1.0f) * cosine + beta);
            nb1 = -2.0f * a * ((a - 1.0f) + (a + 1.0f) * cosine);
            nb2 = a * ((a + 1.0f) + (a - 1.0f) * cosine - beta);
            na0 = (a + 1.0f) - (a - 1.0f) * cosine + beta;
            na1 = 2.0f * ((a - 1.0f) - (a + 1.0f) * cosine);
            na2 = (a + 1.0f) - (a - 1.0f) * cosine - beta;
            break;
        }
        default: // high cut
            nb0 = (1.0f - cosine) * 0.5f; nb1 = 1.0f - cosine; nb2 = nb0;
            na0 = 1.0f + alpha; na1 = -2.0f * cosine; na2 = 1.0f - alpha;
            break;
        }
        b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0;
        a1 = na1 / na0; a2 = na2 / na0;
    }

    float process(float input) {
        const float output = b0 * input + z1;
        z1 = b1 * input - a1 * output + z2;
        z2 = b2 * input - a2 * output;
        return output;
    }
};

struct PluginState {
    std::array<std::atomic<float>, kParamCount> params;
    std::array<SlotDSP, kSlots> slots;
    std::array<std::array<ParametricBiquad, 2>, kEQBandCount> eqFilters;
    std::array<float, kParamCount> chunkCache{};
    float sampleRate = 48000.0f;
    int blockSize = 1024;
    bool active = false;

    PluginState() {
        for (auto &p : params) p.store(0.0f, std::memory_order_relaxed);
        for (int i = 0; i < kSlots; ++i) {
            params[i * kParamsPerSlot + 3].store(60.0f / 66.0f, std::memory_order_relaxed);
        }
    }
};

struct SlotParameters {
    int type = 0;
    bool enabled = false;
    float send = 0.0f;
    float ret = 0.0f;
    float predelayMs = 0.0f;
    float decay = 1.0f;
    float size = 70.0f;
    float dampingAlpha = 0.0f;
    float diffusion = 70.0f;
    float lowCutAlpha = 0.0f;
    float highShelfAlpha = 0.0f;
    float highShelfGain = 0.0f;
    std::array<float, 4> combFeedback{};
    int allpassLength = 100;
    float allpassCoefficient = 0.5f;
    float delaySamples = 1.0f;
    float delayFeedback = 0.0f;
    float delayLowAlpha = 0.0f;
    float delayHighAlpha = 0.0f;
    float chorusRate = 0.8f;
    float chorusDepthMs = 0.0f;
    float chorusBaseMs = 12.0f;
    float chorusPhase = 0.0f;
};

inline float clamp01(float x) { return std::max(0.0f, std::min(1.0f, x)); }
inline float decodeRange(float n, float lo, float hi) { return lo + clamp01(n) * (hi - lo); }
inline float dbGain(float normalized) {
    const float db = decodeRange(normalized, -60.0f, 6.0f);
    return db <= -59.9f ? 0.0f : std::pow(10.0f, db / 20.0f);
}
inline float cutoffAlpha(float hz, float sr) {
    return 1.0f - std::exp(-2.0f * kPi * std::max(10.0f, hz) / sr);
}

PluginState *stateOf(AEffect *effect) { return static_cast<PluginState *>(effect->object); }

void setParameter(AEffect *effect, int32_t index, float value) {
    if (!effect || index < 0 || index >= kParamCount) return;
    stateOf(effect)->params[index].store(clamp01(value), std::memory_order_relaxed);
}

float getParameter(AEffect *effect, int32_t index) {
    if (!effect || index < 0 || index >= kParamCount) return 0.0f;
    return stateOf(effect)->params[index].load(std::memory_order_relaxed);
}

float readParam(PluginState *state, int slot, int param) {
    return state->params[slot * kParamsPerSlot + param].load(std::memory_order_relaxed);
}

float reverbSample(SlotDSP &dsp, int channel, float input, const SlotParameters &p) {
    dsp.hpState[channel] += p.lowCutAlpha * (input - dsp.hpState[channel]);
    const float filtered = input - dsp.hpState[channel];
    float sum = 0.0f;
    constexpr int combLengths[4] = {1426, 1781, 1973, 2098};
    for (int line = 0; line < 4; ++line) {
        int length = std::max(32, std::min(kMaxComb - 1, static_cast<int>(combLengths[line] * std::max(0.2f, p.size / 70.0f))));
        int &write = dsp.combWrite[channel][line];
        int read = write - length;
        if (read < 0) read += kMaxComb;
        const float delayed = dsp.comb[channel * 4 + line][read];
        dsp.dampState[channel] += p.dampingAlpha * (delayed - dsp.dampState[channel]);
        dsp.comb[channel * 4 + line][write] = filtered + dsp.dampState[channel] * p.combFeedback[line];
        if (++write >= kMaxComb) write = 0;
        sum += delayed;
    }
    float wet = sum * 0.25f;
    for (int pass = 0; pass < 2; ++pass) {
        int &write = dsp.allpassWrite[channel][pass];
        int read = write - p.allpassLength - pass * 37;
        while (read < 0) read += 2048;
        const float delayed = dsp.allpass[channel * 2 + pass][read];
        const float out = delayed - p.allpassCoefficient * wet;
        dsp.allpass[channel * 2 + pass][write] = wet + p.allpassCoefficient * out;
        if (++write >= 2048) write = 0;
        wet = out;
    }
    if (p.type == 4 && p.highShelfGain != 0.0f) {
        dsp.highState[channel] += p.highShelfAlpha * (wet - dsp.highState[channel]);
        const float high = wet - dsp.highState[channel];
        wet += high * p.highShelfGain;
    }
    return wet;
}

SlotParameters makeSlotParameters(PluginState *state, int slotIndex, float sr) {
    SlotParameters p;
    p.type = static_cast<int>(std::round(readParam(state, slotIndex, 0) * 6.0f));
    p.enabled = readParam(state, slotIndex, 1) >= 0.5f && p.type >= 1 && p.type <= 6;
    p.send = dbGain(readParam(state, slotIndex, 2));
    p.ret = dbGain(readParam(state, slotIndex, 3));
    const float p0 = readParam(state, slotIndex, 4);
    const float p1 = readParam(state, slotIndex, 5);
    const float p2 = readParam(state, slotIndex, 6);
    const float p3 = readParam(state, slotIndex, 7);
    const float p4 = readParam(state, slotIndex, 8);
    const float p5 = readParam(state, slotIndex, 9);
    if (p.type >= 1 && p.type <= 4) {
        p.predelayMs = decodeRange(p0, 0.0f, 200.0f);
        p.decay = decodeRange(p1, 0.2f, 5.0f);
        p.size = decodeRange(p2, 2.0f, 100.0f);
        const float roomScale = p.type == 2 ? 0.65f : p.type == 3 ? 1.18f : 1.0f;
        constexpr float combMs[4] = {29.7f, 37.1f, 41.1f, 43.7f};
        for (int i = 0; i < 4; ++i) p.combFeedback[i] = std::min(0.92f, std::pow(0.001f, combMs[i] / (std::max(0.2f, p.decay) * 1000.0f)));
        const float dampingHz = p.type == 4 ? decodeRange(p3, 1000.0f, 20000.0f) : decodeRange(p3, 1000.0f, 20000.0f);
        const float lowCutHz = p.type == 4 ? decodeRange(p5, 20.0f, 400.0f) : decodeRange(p5, 20.0f, 400.0f);
        p.dampingAlpha = cutoffAlpha(dampingHz, sr);
        p.lowCutAlpha = cutoffAlpha(lowCutHz, sr);
        p.diffusion = p.type == 4 ? 70.0f : decodeRange(p4, 0.0f, 100.0f);
        p.allpassLength = std::max(32, std::min(1900, static_cast<int>(sr * (0.005f + p.diffusion * 0.00012f))));
        p.allpassCoefficient = 0.35f + 0.25f * (p.diffusion / 100.0f);
        if (p.type == 4) {
            p.highShelfAlpha = cutoffAlpha(decodeRange(p3, 1000.0f, 20000.0f), sr);
            p.highShelfGain = std::pow(10.0f, decodeRange(p4, -15.0f, 15.0f) / 20.0f) - 1.0f;
        }
    } else if (p.type == 5) {
        p.delaySamples = decodeRange(p0, 10.0f, 1000.0f) * sr / 1000.0f;
        p.delayFeedback = decodeRange(p1, 0.0f, 0.9f);
        p.delayLowAlpha = cutoffAlpha(decodeRange(p2, 20.0f, 400.0f), sr);
        p.delayHighAlpha = cutoffAlpha(decodeRange(p3, 1000.0f, 20000.0f), sr);
    } else if (p.type == 6) {
        p.chorusRate = decodeRange(p0, 0.05f, 10.0f);
        p.chorusDepthMs = decodeRange(p1, 0.0f, 100.0f) * 0.35f;
        p.chorusBaseMs = decodeRange(p2, 1.0f, 40.0f);
        p.chorusPhase = decodeRange(p3, 0.0f, 100.0f) * kPi / 100.0f;
    }
    return p;
}

float processSlot(PluginState *state, int slotIndex, const SlotParameters &p, int channel, float input, float sr) {
    if (!p.enabled || p.send == 0.0f || p.ret == 0.0f) return 0.0f;
    SlotDSP &dsp = state->slots[slotIndex];
    float wet = 0.0f;
    if (p.type >= 1 && p.type <= 4) {
        const int pre = std::min(kMaxDelay - 1, static_cast<int>(p.predelayMs * sr / 1000.0f));
        int &write = dsp.delayWrite[channel];
        dsp.delay[channel][write] = input * p.send;
        int preRead = write - pre;
        if (preRead < 0) preRead += kMaxDelay + 1;
        const float delayed = dsp.delay[channel][preRead];
        wet = reverbSample(dsp, channel, delayed, p);
        if (++write >= kMaxDelay + 1) write = 0;
    } else if (p.type == 5) {
        const int delaySamples = std::max(1, std::min(kMaxDelay - 1, static_cast<int>(p.delaySamples)));
        int &write = dsp.delayWrite[channel];
        int read = write - delaySamples;
        if (read < 0) read += kMaxDelay + 1;
        const float delayed = dsp.delay[channel][read];
        dsp.delay[channel][write] = input * p.send + delayed * p.delayFeedback;
        if (++write >= kMaxDelay + 1) write = 0;
        dsp.lowState[channel] += p.delayLowAlpha * (delayed - dsp.lowState[channel]);
        dsp.highState[channel] += p.delayHighAlpha * (delayed - dsp.highState[channel]);
        wet = dsp.highState[channel] - dsp.lowState[channel];
    } else {
        int &write = dsp.delayWrite[channel];
        dsp.delay[channel][write] = input * p.send;
        const float channelPhase = channel == 0 ? 0.0f : p.chorusPhase;
        const float mod = std::sin(dsp.phase + channelPhase);
        const float delayMs = std::max(1.0f, p.chorusBaseMs + p.chorusDepthMs * mod);
        const float delaySamples = delayMs * sr / 1000.0f;
        float readPosition = static_cast<float>(write) - delaySamples;
        while (readPosition < 0) readPosition += kMaxDelay + 1;
        const int r0 = static_cast<int>(readPosition) % (kMaxDelay + 1);
        const int r1 = (r0 + 1) % (kMaxDelay + 1);
        const float frac = readPosition - std::floor(readPosition);
        wet = dsp.delay[channel][r0] * (1.0f - frac) + dsp.delay[channel][r1] * frac;
        if (++write >= kMaxDelay + 1) write = 0;
        if (channel == 1) dsp.phase += 2.0f * kPi * p.chorusRate / sr;
        if (dsp.phase >= 2.0f * kPi) dsp.phase -= 2.0f * kPi;
    }
    return wet * p.ret;
}

void processReplacing(AEffect *effect, float **inputs, float **outputs, int32_t frames) {
    PluginState *state = stateOf(effect);
    if (!state || !outputs) return;
    const float sr = std::max(8000.0f, state->sampleRate);
    std::array<SlotParameters, kSlots> parameters;
    for (int slot = 0; slot < kSlots; ++slot) parameters[slot] = makeSlotParameters(state, slot, sr);
    const bool gateEnabled = state->params[kEffectParamCount].load(std::memory_order_relaxed) >= 0.5f;
    const float gateOpenDb = decodeRange(state->params[kEffectParamCount + 1].load(std::memory_order_relaxed), -80.0f, 0.0f);
    const float gateCloseDb = decodeRange(state->params[kEffectParamCount + 2].load(std::memory_order_relaxed), -80.0f, 0.0f);
    const float gateAttackMs = decodeRange(state->params[kEffectParamCount + 3].load(std::memory_order_relaxed), 1.0f, 200.0f);
    const float gateHoldMs = decodeRange(state->params[kEffectParamCount + 4].load(std::memory_order_relaxed), 0.0f, 2000.0f);
    const float gateReleaseMs = decodeRange(state->params[kEffectParamCount + 5].load(std::memory_order_relaxed), 5.0f, 2000.0f);
    const float detectorAttack = std::exp(-1.0f / (0.010f * sr));
    const float detectorRelease = std::exp(-1.0f / (0.080f * sr));
    const float gateAttack = std::exp(-1.0f / (gateAttackMs * 0.001f * sr));
    const float gateRelease = std::exp(-1.0f / (gateReleaseMs * 0.001f * sr));
    const int gateHoldSamples = static_cast<int>(gateHoldMs * 0.001f * sr);
    const bool eqEnabled = state->params[kEQEnableParam].load(std::memory_order_relaxed) >= 0.5f;
    std::array<bool, kEQBandCount> eqBandEnabled{};
    for (int band = 0; band < kEQBandCount; ++band) {
        const int base = kEQBandParamStart + band * kParamsPerEQBand;
        eqBandEnabled[band] = eqEnabled && state->params[base].load(std::memory_order_relaxed) >= 0.5f;
        if (!eqBandEnabled[band]) continue;
        const int type = std::max(0, std::min(5, static_cast<int>(std::round(state->params[base + 1].load(std::memory_order_relaxed) * 5.0f))));
        const float frequency = 20.0f * std::pow(1000.0f, clamp01(state->params[base + 2].load(std::memory_order_relaxed)));
        const float gain = decodeRange(state->params[base + 3].load(std::memory_order_relaxed), -18.0f, 18.0f);
        const float q = decodeRange(state->params[base + 4].load(std::memory_order_relaxed), 0.3f, 8.0f);
        for (int channel = 0; channel < 2; ++channel) state->eqFilters[band][channel].configure(type, frequency, gain, q, sr);
    }
    for (int32_t i = 0; i < frames; ++i) {
        const float left = inputs && inputs[0] ? inputs[0][i] : 0.0f;
        const float right = inputs && inputs[1] ? inputs[1][i] : left;
        const float input[2] = {left, right};
        float gated[2] = {left, right};
        if (gateEnabled) {
            for (int channel = 0; channel < 2; ++channel) {
                SlotDSP &gate = state->slots[0];
                const float magnitude = std::abs(input[channel]);
                const float detectorCoeff = magnitude > gate.gateEnvelope[channel] ? detectorAttack : detectorRelease;
                gate.gateEnvelope[channel] = detectorCoeff * gate.gateEnvelope[channel] + (1.0f - detectorCoeff) * magnitude;
                const float levelDb = 20.0f * std::log10(std::max(1.0e-6f, gate.gateEnvelope[channel]));
                if (levelDb >= gateOpenDb) gate.gateHoldFrames[channel] = gateHoldSamples;
                else if (gate.gateHoldFrames[channel] > 0) --gate.gateHoldFrames[channel];
                float targetGain = 1.0f;
                if (gate.gateHoldFrames[channel] == 0 && levelDb <= gateCloseDb) targetGain = 0.1f;
                else if (gate.gateHoldFrames[channel] == 0 && levelDb < gateOpenDb) {
                    const float span = std::max(1.0f, gateOpenDb - gateCloseDb);
                    const float amount = clamp01((levelDb - gateCloseDb) / span);
                    targetGain = 0.1f + 0.9f * amount;
                }
                const float coeff = targetGain > gate.gateGain[channel] ? gateAttack : gateRelease;
                gate.gateGain[channel] = coeff * gate.gateGain[channel] + (1.0f - coeff) * targetGain;
                gated[channel] = input[channel] * gate.gateGain[channel];
            }
        } else {
            state->slots[0].gateGain = {1.0f, 1.0f};
            state->slots[0].gateHoldFrames = {0, 0};
        }
        float equalized[2] = {gated[0], gated[1]};
        if (eqEnabled) {
            for (int band = 0; band < kEQBandCount; ++band) {
                if (!eqBandEnabled[band]) continue;
                for (int channel = 0; channel < 2; ++channel) equalized[channel] = state->eqFilters[band][channel].process(equalized[channel]);
            }
        }
        float outL = equalized[0];
        float outR = equalized[1];
        for (int slot = 0; slot < kSlots; ++slot) {
            outL += processSlot(state, slot, parameters[slot], 0, equalized[0], sr);
            outR += processSlot(state, slot, parameters[slot], 1, equalized[1], sr);
        }
        auto softLimit = [](float sample) {
            const float magnitude = std::abs(sample);
            if (magnitude <= 0.95f) return sample;
            const float limited = 0.95f + 0.05f * std::tanh((magnitude - 0.95f) / 0.05f);
            return sample < 0.0f ? -limited : limited;
        };
        outputs[0][i] = softLimit(outL);
        outputs[1][i] = softLimit(outR);
    }
}

VstIntPtr dispatcher(AEffect *effect, int32_t opcode, int32_t, VstIntPtr value, void *ptr, float opt) {
    PluginState *state = stateOf(effect);
    switch (opcode) {
    case kOpen: return 1;
    case kClose:
        delete state;
        delete effect;
        return 1;
    case kSetSampleRate:
        if (state && opt > 8000.0f) state->sampleRate = opt;
        return 1;
    case kSetBlockSize:
        if (state) state->blockSize = 1024;
        return 1;
    case kMainsChanged:
        if (state) state->active = true;
        return 1;
    case kGetChunk:
        if (state && ptr) {
            for (int i = 0; i < kParamCount; ++i) state->chunkCache[i] = state->params[i].load(std::memory_order_relaxed);
            *static_cast<void **>(ptr) = state->chunkCache.data();
            return static_cast<VstIntPtr>(sizeof(float) * kParamCount);
        }
        return 0;
    case kSetChunk:
        if (state && ptr && value >= static_cast<VstIntPtr>(sizeof(float))) {
            const auto *chunk = static_cast<const float *>(ptr);
            const int count = std::min(kParamCount, static_cast<int>(value / sizeof(float)));
            for (int i = 0; i < count; ++i) state->params[i].store(clamp01(chunk[i]), std::memory_order_relaxed);
            return count * static_cast<VstIntPtr>(sizeof(float));
        }
        return 0;
    case kGetEffectName:
        if (ptr) std::strncpy(static_cast<char *>(ptr), "GoLive FX", 63);
        return 1;
    case kGetVendorString:
        if (ptr) std::strncpy(static_cast<char *>(ptr), "GoLive", 63);
        return 1;
    case kGetProductString:
        if (ptr) std::strncpy(static_cast<char *>(ptr), "GoLive FX Bus", 63);
        return 1;
    case kGetVendorVersion: return 1;
    case kGetVstVersion: return 2400;
    case kGetPlugCategory: return kEffect;
    case kCanDo:
        if (ptr && std::strcmp(static_cast<const char *>(ptr), "x2in2out") == 0) return 1;
        if (ptr && std::strcmp(static_cast<const char *>(ptr), "plugAsChannelInsert") == 0) return 1;
        return 0;
    default: return 0;
    }
}
} // namespace

#if defined(_WIN32)
#define GOLIVE_VST_EXPORT extern "C" __declspec(dllexport)
#else
#define GOLIVE_VST_EXPORT extern "C" __attribute__((visibility("default")))
#endif
GOLIVE_VST_EXPORT AEffect *VSTPluginMain(AudioMasterCallback host) {
    if (!host) return nullptr;
    auto *state = new (std::nothrow) PluginState();
    auto *effect = new (std::nothrow) AEffect{};
    if (!state || !effect) { delete state; delete effect; return nullptr; }
    effect->magic = kMagic;
    effect->dispatcher = dispatcher;
    effect->setParameter = setParameter;
    effect->getParameter = getParameter;
    effect->numPrograms = 1;
    effect->numParams = kParamCount;
    effect->numInputs = 2;
    effect->numOutputs = 2;
    effect->flags = kCanReplacing;
    effect->ioRatio = 1.0f;
    effect->object = state;
    effect->uniqueID = ('G' << 24) | ('L' << 16) | ('F' << 8) | 'X';
    effect->version = 1;
    effect->processReplacing = processReplacing;
    return effect;
}

