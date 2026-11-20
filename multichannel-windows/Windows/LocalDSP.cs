using System.Text.Json;

namespace GoLivEdit;

/// <summary>Per-channel Gate, EQ and compressor processing for Local Live WAV capture.</summary>
internal sealed class LocalDSP
{
    private sealed record Band(string Type, double Frequency, double Gain, double Q, bool Enabled);
    private struct Biquad
    {
        public double B0, B1, B2, A1, A2, Z1, Z2;
        public void Configure(Band band, double rate)
        {
            var f = Math.Clamp(band.Frequency, 10, rate * .45); var q = Math.Clamp(band.Q, .1, 18);
            var w = 2 * Math.PI * f / rate; var c = Math.Cos(w); var s = Math.Sin(w); var alpha = s / (2 * q); var a = Math.Pow(10, band.Gain / 40);
            double b0 = 1, b1 = 0, b2 = 0, a0 = 1, a1 = 0, a2 = 0;
            switch (band.Type)
            {
                case "lc": b0=(1+c)/2; b1=-(1+c); b2=b0; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
                case "ls":
                    var beta=2*Math.Sqrt(a)*alpha; b0=a*((a+1)-(a-1)*c+beta); b1=2*a*((a-1)-(a+1)*c); b2=a*((a+1)-(a-1)*c-beta); a0=(a+1)+(a-1)*c+beta; a1=-2*((a-1)+(a+1)*c); a2=(a+1)+(a-1)*c-beta; break;
                case "hs":
                    beta=2*Math.Sqrt(a)*alpha; b0=a*((a+1)+(a-1)*c+beta); b1=-2*a*((a-1)+(a+1)*c); b2=a*((a+1)+(a-1)*c-beta); a0=(a+1)-(a-1)*c+beta; a1=2*((a-1)-(a+1)*c); a2=(a+1)-(a-1)*c-beta; break;
                default: b0=1+alpha*a; b1=-2*c; b2=1-alpha*a; a0=1+alpha/a; a1=-2*c; a2=1-alpha/a; break;
            }
            B0=b0/a0; B1=b1/a0; B2=b2/a0; A1=a1/a0; A2=a2/a0;
        }
        public double Process(double x) { var y=B0*x+Z1; Z1=B1*x-A1*y+Z2; Z2=B2*x-A2*y; return y; }
    }

    private bool bypass, gateOn, eqOn, compOn, dirty=true;
    private double[] gate=[-40,-47,12,300,300], comp=[-20,3,8,120,1];
    private Band[] bands=[]; private Biquad[] filters=[]; private string configKey=""; private double filterRate, detector, gateEnvelope, gateGain=1; private int hold;

    public void Configure(JsonElement dsp)
    {
        if (dsp.ValueKind != JsonValueKind.Object) return;
        var nextKey=dsp.GetRawText(); if(nextKey==configKey)return; configKey=nextKey;
        bypass=Bool(dsp,"bypass"); gateOn=Bool(dsp,"gOn"); eqOn=Bool(dsp,"eOn"); compOn=Bool(dsp,"cOn");
        gate=Numbers(dsp,"g",gate); comp=Numbers(dsp,"c",comp);
        if (dsp.TryGetProperty("e",out var arr)&&arr.ValueKind==JsonValueKind.Array)
        {
            bands=arr.EnumerateArray().Select(b=>new Band(Text(b,"t","pk"),Number(b,"f",1000),Number(b,"g",Number(b,"gain",0)),Number(b,"q",.71),Bool(b,"on"))).ToArray();
            if(filters.Length!=bands.Length) filters=new Biquad[bands.Length]; dirty=true;
        }
    }
    public float Process(float input, double rate)
    {
        var x=(double)input;
        if(!bypass&&gateOn&&gate.Length>=5)
        {
            var mag=Math.Abs(x); var dc=Math.Exp(-1/((mag>gateEnvelope ? .010 : .080)*rate)); gateEnvelope=dc*gateEnvelope+(1-dc)*mag;
            var db=20*Math.Log10(Math.Max(1e-6,gateEnvelope)); var holdSamples=(int)(Math.Max(0,gate[3])*.001*rate);
            if(db>=gate[0])hold=holdSamples;else if(hold>0)hold--;
            var target=1d;if(hold==0&&db<=gate[1])target=.1;else if(hold==0&&db<gate[0])target=.1+.9*Math.Clamp((db-gate[1])/Math.Max(1,gate[0]-gate[1]),0,1);
            var coeff=Math.Exp(-1/(Math.Max(1,target>gateGain?gate[2]:gate[4])*.001*rate));gateGain=coeff*gateGain+(1-coeff)*target;x*=gateGain;
        }
        if(!bypass&&eqOn)
        {
            if(dirty||Math.Abs(filterRate-rate)>1){for(var i=0;i<bands.Length;i++)if(bands[i].Enabled)filters[i].Configure(bands[i],rate);filterRate=rate;dirty=false;}
            for(var i=0;i<bands.Length;i++)if(bands[i].Enabled)x=filters[i].Process(x);
        }
        if(!bypass&&compOn&&comp.Length>=5)
        {
            var coeff=Math.Exp(-1/(Math.Max(.1,Math.Abs(x)>detector ? comp[2] : comp[3])*.001*rate));detector=coeff*detector+(1-coeff)*Math.Abs(x);
            var db=20*Math.Log10(Math.Max(1e-6,detector));var over=Math.Max(0,db-comp[0]);var reduction=over-over/Math.Max(1,comp[1]);x*=Math.Pow(10,(comp[4]-reduction)/20);
        }
        return (float)x;
    }
    private static bool Bool(JsonElement e,string k)=>e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.True;
    private static double Number(JsonElement e,string k,double d)=>e.TryGetProperty(k,out var v)&&v.TryGetDouble(out var n)?n:d;
    private static string Text(JsonElement e,string k,string d)=>e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??d:d;
    private static double[] Numbers(JsonElement e,string k,double[] fallback)=>e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Array?v.EnumerateArray().Select(x=>x.TryGetDouble(out var n)?n:0).ToArray():fallback;
}

