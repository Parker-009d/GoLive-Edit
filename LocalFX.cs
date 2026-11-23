using System.Text.Json;

namespace GoLivEdit;

/// <summary>Per-channel Local Live FX bus using the same slot values as the Mac host.</summary>
internal sealed class LocalFX
{
    private static readonly double[] ReverbTaps = [29.7, 37.1, 41.1, 43.7];
    private sealed class Slot
    {
        public string Type = "";
        public bool Enabled;
        public bool Assigned;
        public double Send = -60, Return = -60;
        public double[] Params = [];
        public float[] InputDelay = [], FeedbackDelay = [];
        public int Write;
        public float Damping, Low, High;
        public double Phase;
    }

    private readonly Slot[][] slots = Enumerable.Range(0,24).Select(_=>Enumerable.Range(0,4).Select(_=>new Slot()).ToArray()).ToArray();
    private readonly bool[] wasMuted = new bool[24];
    private readonly int[] sendPeakBits = new int[4], returnPeakBits = new int[4];
    private double rate;
    private string configKey = "";

    public void Configure(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("fx", out var fx) || fx.ValueKind != JsonValueKind.Array) return;
        var nextKey = fx.GetRawText(); if (nextKey == configKey) return; configKey = nextKey;
        for (var ch = 0; ch < 24; ch++) for (var i = 0; i < 4; i++)
        {
            var s = slots[ch][i];
            if (fx.GetArrayLength() <= i) { s.Enabled = false; continue; }
            var e = fx[i]; s.Type = Str(e,"t",""); s.Enabled = Bool(e,"on");
            s.Assigned = AtBool(e,"asg",ch); s.Send = AtNum(e,"snd",ch,-60); s.Return = Num(e,"ret",-60);
            s.Params = e.TryGetProperty("p",out var p) && p.ValueKind==JsonValueKind.Array ? p.EnumerateArray().Select(x=>x.TryGetDouble(out var n)?n:0).ToArray() : [];
        }
    }

    public float ProcessChannel(float input, int channel, bool muted, double sampleRate)
    {
        if (channel < 0 || channel >= slots.Length || sampleRate < 8000) return muted ? 0 : input;
        Prepare(sampleRate);
        if(muted&&!wasMuted[channel])ClearChannel(channel);
        wasMuted[channel]=muted;
        if(muted)return 0;
        var output=input;
        for(var i=0;i<4;i++)output+=ProcessSlot(slots[channel][i],input,sampleRate,channel,i);
        return SoftLimit(output);
    }

    public (double[] Sends, double[] Returns) ReadMeters()
    {
        return (ReadPeakLevels(sendPeakBits), ReadPeakLevels(returnPeakBits));
    }

    private static double[] ReadPeakLevels(int[] peaks)
    {
        var levels = new double[peaks.Length];
        for(var i=0;i<peaks.Length;i++)
        {
            var peak=BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref peaks[i],0));
            levels[i]=peak>0?Math.Max(-90,20*Math.Log10(peak)):-90;
        }
        return levels;
    }

    private static void CapturePeak(int[] peaks,int slot,float sample)
    {
        var amplitude=Math.Abs(sample);if(!float.IsFinite(amplitude)||amplitude<=0)return;
        var bits=BitConverter.SingleToInt32Bits(amplitude);var current=Volatile.Read(ref peaks[slot]);
        while(BitConverter.Int32BitsToSingle(current)<amplitude)
        {
            var prior=Interlocked.CompareExchange(ref peaks[slot],bits,current);if(prior==current)break;current=prior;
        }
    }

    private void Prepare(double sampleRate)
    {
        if (Math.Abs(rate-sampleRate) <= 1) return;
        rate=sampleRate; var length=Math.Max(4096,(int)(sampleRate*2.5)+4);
        foreach(var channel in slots) foreach(var s in channel) { s.InputDelay=new float[length]; s.FeedbackDelay=new float[length]; s.Write=0; s.Damping=s.Low=s.High=0; s.Phase=0; }
        Array.Clear(wasMuted);
    }

    private void ClearChannel(int channel)
    {
        foreach(var s in slots[channel]){Array.Clear(s.InputDelay);Array.Clear(s.FeedbackDelay);s.Write=0;s.Damping=s.Low=s.High=0;}
    }

    private float ProcessSlot(Slot s,float input,double rate,int channel,int slot)
    {
        if(!s.Enabled||!s.Assigned||s.Send<=-59.9||s.Return<=-59.9||s.InputDelay.Length==0)return 0;
        var size=s.InputDelay.Length; var at=s.Write; var send=Gain(s.Send); var ret=Gain(s.Return); var p=s.Params; float wet=0;
        CapturePeak(sendPeakBits,slot,input*send);
        switch(s.Type)
        {
            case "Hall Reverb": case "Room Reverb": case "Chamber Reverb": case "Plate Reverb":
            {
                var pred=Param(p,0,20);var decay=Param(p,1,1.8);var room=Param(p,2,60);var damp=Param(p,3,8000);var lowCut=Param(p,5,80);
                var scale=Math.Max(.2,room/70)*(s.Type=="Room Reverb"?.65:s.Type=="Chamber Reverb"?1.18:1.0);
                var loopMs=Math.Max(20,37.1*scale);var loop=Math.Clamp((int)(rate*loopMs/1000),1,size-2);var pre=Math.Clamp((int)(rate*pred/1000),0,size-2);
                var preSample=pre==0?input:s.InputDelay[Wrap(at-pre,size)];s.Low+=(float)Alpha(lowCut,rate)*(preSample-s.Low);var hp=preSample-s.Low;
                var delayed=s.FeedbackDelay[Wrap(at-loop,size)];s.Damping+=(float)Alpha(damp,rate)*(delayed-s.Damping);
                var feedback=(float)Math.Min(.92,Math.Pow(.001,loopMs/Math.Max(200,decay*1000)));s.FeedbackDelay[at]=hp*send+s.Damping*feedback;
                foreach(var tap in ReverbTaps){var d=Math.Clamp((int)(rate*tap*scale/1000),1,size-2);wet+=s.FeedbackDelay[Wrap(at-d,size)]*.25f;}
                if(s.Type=="Plate Reverb"){var a=(float)Alpha(Param(p,3,6000),rate);s.High+=a*(wet-s.High);wet+=(wet-s.High)*(float)(Math.Pow(10,Param(p,4,0)/20)-1);}
                s.InputDelay[at]=input;break;
            }
            case "Stereo Delay":
            {
                var delay=Math.Clamp((int)(rate*Param(p,0,350)/1000),1,size-2);var delayed=s.InputDelay[Wrap(at-delay,size)];var feedback=(float)(Param(p,1,30)/100);
                s.InputDelay[at]=input*send+delayed*feedback;s.Low+=(float)Alpha(Param(p,2,100),rate)*(delayed-s.Low);var hp=delayed-s.Low;s.High+=(float)Alpha(Param(p,3,6000),rate)*(hp-s.High);wet=s.High;break;
            }
            case "Stereo Chorus":
            {
                var depth=Param(p,1,40)*.35;var baseMs=Param(p,2,12);var phase=s.Phase;
                var delay=Math.Max(1,baseMs+depth*Math.Sin(phase))*rate/1000;var position=at-delay;var floor=Math.Floor(position);var r0=Wrap((int)floor,size);var r1=(r0+1)%size;wet=s.InputDelay[r0]+(s.InputDelay[r1]-s.InputDelay[r0])*(float)(position-floor);
                s.InputDelay[at]=input*send;s.Phase+=2*Math.PI*Param(p,0,.8)/rate;if(s.Phase>=2*Math.PI)s.Phase-=2*Math.PI;break;
            }
            default:return 0;
        }
        s.Write=(at+1)%size;var returned=wet*ret;CapturePeak(returnPeakBits,slot,returned);return returned;
    }
    private static int Wrap(int n,int size){var x=n%size;return x<0?x+size:x;}
    private static double Alpha(double hz,double rate)=>1-Math.Exp(-2*Math.PI*Math.Max(10,hz)/rate);
    private static float Gain(double db)=>db<=-59.9?0:(float)Math.Pow(10,db/20);
    private static float SoftLimit(float value){var a=Math.Abs(value);if(a<=.96f)return value;var limited=.96f+.04f*(1-MathF.Exp(-(a-.96f)/.04f));return value<0?-limited:limited;}
    private static double Param(double[] p,int i,double fallback)=>p.Length>i?p[i]:fallback;
    private static bool Bool(JsonElement e,string k)=>e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.True;
    private static string Str(JsonElement e,string k,string d)=>e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??d:d;
    private static double Num(JsonElement e,string k,double d)=>e.TryGetProperty(k,out var v)&&v.TryGetDouble(out var x)?x:d;
    private static double AtNum(JsonElement e,string k,int i,double d)=>e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Array&&v.GetArrayLength()>i&&v[i].TryGetDouble(out var n)?n:d;
    private static bool AtBool(JsonElement e,string k,int i)=>e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Array&&v.GetArrayLength()>i&&v[i].ValueKind==JsonValueKind.True;
}

