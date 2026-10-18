using NAudio.Wave;
using System.Numerics;

namespace SaturnTracktor;

public sealed class Biquad
{
    readonly double b0,b1,b2,a1,a2;
    double z1,z2;
    Biquad(double b0,double b1,double b2,double a0,double a1,double a2)
    { this.b0=b0/a0;this.b1=b1/a0;this.b2=b2/a0;this.a1=a1/a0;this.a2=a2/a0; }
    public double Process(double x)
    {
        var y=b0*x+z1;z1=b1*x-a1*y+z2;z2=b2*x-a2*y;
        if(Math.Abs(z1)<1e-25)z1=0;if(Math.Abs(z2)<1e-25)z2=0;return y;
    }
    public static Biquad Peak(int rate,double frequency,double q,double gain)
    {
        var w=2*Math.PI*Math.Min(frequency,rate*.475)/rate;var c=Math.Cos(w);var alpha=Math.Sin(w)/(2*q);var a=Math.Pow(10,gain/40);
        return new(1+alpha*a,-2*c,1-alpha*a,1+alpha/a,-2*c,1-alpha/a);
    }
    public static Biquad Shelf(int rate,double frequency,double gain,bool high)
    {
        var w=2*Math.PI*frequency/rate;var c=Math.Cos(w);var a=Math.Pow(10,gain/40);var beta=Math.Sin(w)*Math.Sqrt(2*a);
        return high ? new(a*((a+1)+(a-1)*c+beta),-2*a*((a-1)+(a+1)*c),a*((a+1)+(a-1)*c-beta),(a+1)-(a-1)*c+beta,2*((a-1)-(a+1)*c),(a+1)-(a-1)*c-beta)
            : new(a*((a+1)-(a-1)*c+beta),2*a*((a-1)-(a+1)*c),a*((a+1)-(a-1)*c-beta),(a+1)+(a-1)*c+beta,-2*((a-1)+(a+1)*c),(a+1)+(a-1)*c-beta);
    }
}

public sealed class FilterBank
{
    readonly Biquad[][] filters;
    public FilterBank(int sampleRate,AudioSettings state)
    {
        filters=new Biquad[2][];
        for(var ch=0;ch<2;ch++)
        {
            var list=new List<Biquad>();
            for(var i=0;i<31;i++)if(Math.Abs(state.Eq[i])>.001)list.Add(Biquad.Peak(sampleRate,Profiles.Frequencies[i],4.318*state.Q,state.Eq[i]));
            if(state.Bass>0)list.Add(Biquad.Shelf(sampleRate,110,state.Bass*.8,false));
            if(state.Clarity>0)list.Add(Biquad.Shelf(sampleRate,3500,state.Clarity*.5,true));
            filters[ch]=list.ToArray();
        }
    }
    public double Process(int ch,double x){foreach(var f in filters[ch])x=f.Process(x);return x;}
}

public sealed class DspProcessor
{
    readonly int rate;
    readonly float[] delayL,delayR;
    int delayPosition;
    FilterBank bank;
    FilterBank? fadingBank;
    int fadeRemaining;
    readonly int fadeSamples;
    AudioSettings current=new();
    volatile Pending? pending;
    double gain=1,envelope,limiter=1,leveler=1;
    public double Peak {get;private set;}
    public double LimiterReductionDb {get;private set;}
    public readonly SpectrumAnalyzer Spectrum;
    sealed record Pending(AudioSettings Settings,FilterBank Bank);
    public DspProcessor(int rate)
    {
        this.rate=rate;fadeSamples=Math.Max(1,rate/50);bank=new(rate,current);
        delayL=new float[(int)(rate*.127)];delayR=new float[(int)(rate*.149)];Spectrum=new(rate);
    }
    public void Update(AudioSettings s){s=s.Copy();s.Validate();pending=new(s,new FilterBank(rate,s));}
    public void Process(float[] samples,int count)
    {
        var next=Interlocked.Exchange(ref pending,null);
        if(next!=null){fadingBank=bank;bank=next.Bank;current=next.Settings;fadeRemaining=fadeSamples;}
        var target=Math.Pow(10,current.Gain/20);var smooth=1-Math.Exp(-1d/(rate*.012));
        var attack=Math.Exp(-1d/(rate*.003));var release=Math.Exp(-1d/(rate*.18));
        double peak=0,reduction=0;
        for(int i=0;i+1<count;i+=2)
        {
            var l=(double)samples[i];var r=(double)samples[i+1];
            var a=bank.Process(0,l);var b=bank.Process(1,r);
            if(fadeRemaining>0&&fadingBank!=null)
            {
                var mix=1d-fadeRemaining/(double)fadeSamples;
                a=fadingBank.Process(0,l)*(1-mix)+a*mix;b=fadingBank.Process(1,r)*(1-mix)+b*mix;
                if(--fadeRemaining==0)fadingBank=null;
            }
            var mid=(a+b)*.5;var side=(a-b)*.5*(1+current.Surround*.055);
            l=mid+side;r=mid-side;
            var dl=delayL[delayPosition%delayL.Length];var dr=delayR[delayPosition%delayR.Length];
            var wet=current.Ambience*.018;
            delayL[delayPosition%delayL.Length]=(float)(l+dl*.24);delayR[delayPosition%delayR.Length]=(float)(r+dr*.24);
            delayPosition=(delayPosition+1)% (delayL.Length*delayR.Length/GreatestCommonDivisor(delayL.Length,delayR.Length));
            l+=dr*wet;r+=dl*wet;
            var inputPeak=Math.Max(Math.Abs(l),Math.Abs(r));
            var coef=inputPeak>envelope?attack:release;envelope=coef*envelope+(1-coef)*inputPeak;
            double compression=1;
            if(current.Leveling>0||current.Dynamics>0)
            {
                var threshold=Math.Pow(10,(-12-current.Leveling*1.2)/20);
                var ratio=1.2+current.Leveling*.22+current.Dynamics*.12;
                if(envelope>threshold)compression=Math.Pow(envelope/threshold,1/ratio-1);
                // A bounded makeup gain avoids amplifying the noise floor without limit.
                var makeup=1+current.Dynamics*.045+current.Leveling*.025;
                leveler+=(compression*makeup-leveler)*(compression*makeup<leveler?.01:.0005);
            }
            else leveler+=(1-leveler)*.002;
            gain+=(target-gain)*smooth;l*=gain*leveler;r*=gain*leveler;
            if(current.Balance<0)r*=1+current.Balance/100;
            if(current.Balance>0)l*=1-current.Balance/100;
            var p=Math.Max(Math.Abs(l),Math.Abs(r));
            var desired=p>.97?.97/p:1;
            limiter=desired<limiter?desired:limiter+(1-limiter)*.00025;
            l*=limiter;r*=limiter;
            if(!double.IsFinite(l)||!double.IsFinite(r)){l=0;r=0;}
            samples[i]=(float)Math.Clamp(l,-.98,.98);samples[i+1]=(float)Math.Clamp(r,-.98,.98);
            peak=Math.Max(peak,Math.Max(Math.Abs(l),Math.Abs(r)));reduction=Math.Max(reduction,-20*Math.Log10(Math.Max(limiter,1e-9)));
            Spectrum.Add((float)((l+r)*.5));
        }
        Peak=peak;LimiterReductionDb=reduction;
    }
    static int GreatestCommonDivisor(int a,int b){while(b!=0){var t=b;b=a%b;a=t;}return a;}
}

public sealed class StereoDspProvider:ISampleProvider
{
    readonly ISampleProvider input;readonly int channels;float[] scratch=Array.Empty<float>();
    public DspProcessor Processor {get;}
    public WaveFormat WaveFormat {get;}
    public StereoDspProvider(ISampleProvider input)
    {this.input=input;channels=input.WaveFormat.Channels;WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(input.WaveFormat.SampleRate,2);Processor=new(WaveFormat.SampleRate);}
    public int Read(float[] buffer,int offset,int count)
    {
        count-=count%2;var needed=count/2*channels;
        if(scratch.Length<needed)scratch=new float[needed];
        var read=input.Read(scratch,0,needed);var frames=read/channels;
        for(var frame=0;frame<frames;frame++)
        {
            var p=frame*channels;var l=scratch[p];var r=channels>1?scratch[p+1]:l;
            if(channels>2){l+=scratch[p+2]*.707f;r+=scratch[p+2]*.707f;}
            if(channels>=6){l+=scratch[p+4]*.707f;r+=scratch[p+5]*.707f;}
            buffer[offset+frame*2]=l;buffer[offset+frame*2+1]=r;
        }
        if(offset==0)Processor.Process(buffer,frames*2);
        else{var block=new float[frames*2];Array.Copy(buffer,offset,block,0,block.Length);Processor.Process(block,block.Length);Array.Copy(block,0,buffer,offset,block.Length);}
        return frames*2;
    }
}

public sealed class SpectrumAnalyzer
{
    const int N=2048;readonly float[] ring=new float[N];readonly int rate;int index;
    public SpectrumAnalyzer(int rate){this.rate=rate;}
    public void Add(float x){ring[index]=x;index=(index+1)%N;}
    public double[] Read()
    {
        var spectrum=new Complex[N];var start=index;
        for(var i=0;i<N;i++)spectrum[i]=new Complex(ring[(start+i)%N]*(.5-.5*Math.Cos(2*Math.PI*i/(N-1))),0);
        for(int i=1,j=0;i<N;i++){int bit=N>>1;for(; (j&bit)!=0;bit>>=1)j^=bit;j^=bit;if(i<j)(spectrum[i],spectrum[j])=(spectrum[j],spectrum[i]);}
        for(var len=2;len<=N;len<<=1){var root=Complex.FromPolarCoordinates(1,-2*Math.PI/len);for(var i=0;i<N;i+=len){var w=Complex.One;for(var j=0;j<len/2;j++){var u=spectrum[i+j];var v=spectrum[i+j+len/2]*w;spectrum[i+j]=u+v;spectrum[i+j+len/2]=u-v;w*=root;}}}
        return Profiles.Frequencies.Select(f=>{var bin=Math.Clamp((int)Math.Round(f*N/rate),1,N/2-1);var m=spectrum[bin].Magnitude/(N*.25);return Math.Clamp((20*Math.Log10(Math.Max(m,1e-6))+70)/70,0,1);}).ToArray();
    }
}
