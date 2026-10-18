using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SaturnTracktor;

public static class Diagnostics
{
    const int Rate=48000;
    static float[] Tone(double frequency=1000,double amplitude=.005,double seconds=2)
    {
        var b=new float[(int)(Rate*seconds)*2];for(var i=0;i<b.Length;i+=2)b[i]=b[i+1]=(float)(amplitude*Math.Sin(2*Math.PI*frequency*i/2/Rate));return b;
    }
    static double Rms(float[] b,int ch=0){double sum=0;var count=0;for(var i=b.Length/2+(b.Length/2%2)+ch;i<b.Length;i+=2){sum+=b[i]*b[i];count++;}return Math.Sqrt(sum/count);}
    static float[] Process(AudioSettings s,double freq=1000,double amplitude=.005){var dsp=new DspProcessor(Rate);dsp.Update(s);var b=Tone(freq,amplitude);for(int pos=0;pos<b.Length;pos+=960){var n=Math.Min(960,b.Length-pos);var block=new float[n];Array.Copy(b,pos,block,0,n);dsp.Process(block,n);Array.Copy(block,0,b,pos,n);}return b;}
    public static int RunOffline(string path)
    {
        var tests=new List<object>();var failed=0;
        void Check(string name,bool passed,object detail){tests.Add(new{name,passed,detail});if(!passed)failed++;}
        try
        {
            var neutral=Process(new());var rms=Rms(neutral);Check("Unity signal transparency",Math.Abs(rms/(.005/Math.Sqrt(2))-1)<.001,rms);
            foreach(var db in new[]{6d,20d}){var b=Process(new(){Gain=db});var measured=20*Math.Log10(Rms(b)/rms);Check("Master gain "+db+" dB",Math.Abs(measured-db)<.01,new{measured,percent=(Math.Pow(10,db/20)-1)*100});}
            var maxError=0d;
            for(var i=0;i<31;i++){var s=new AudioSettings();s.Eq[i]=6;var b=Process(s,Profiles.Frequencies[i]);var bandBaseline=Process(new(),Profiles.Frequencies[i]);var db=20*Math.Log10(Rms(b)/Rms(bandBaseline));maxError=Math.Max(maxError,Math.Abs(db-6));}
            Check("All 31 EQ band center gains",maxError<.15,new{maxErrorDb=maxError,bands=31});
            var left=Process(new(){Balance=-100});var right=Process(new(){Balance=100});Check("Channel balance endpoints",Rms(left,1)<1e-9&&Rms(right,0)<1e-9,new{rightAtLeft=Rms(left,1),leftAtRight=Rms(right,0)});
            var stress=Process(new(){Gain=20,Eq=Enumerable.Repeat(12d,31).ToArray(),Bass=10,Clarity=10,Dynamics=10,Leveling=10,Surround=10,Ambience=10},1000,.6);
            Check("Limiter and finite output under maximum gain",stress.All(float.IsFinite)&&stress.Max(Math.Abs)<=.980001,stress.Max(Math.Abs));
            var high=Process(new(){Clarity=10},8000);var highBase=Process(new(),8000);Check("Clarity high shelf",Rms(high)>Rms(highBase)*1.3,Rms(high)/Rms(highBase));
            var low=Process(new(){Bass=10},40);var lowBase=Process(new(),40);Check("Bass low shelf",Rms(low)>Rms(lowBase)*1.8,Rms(low)/Rms(lowBase));
            var level=Process(new(){Leveling=10,Dynamics=5},1000,.6);var loud=Process(new(),1000,.6);Check("Dynamics compressor",Rms(level)<Rms(loud)*.8,Rms(level)/Rms(loud));
            var ambience=Process(new(){Ambience=10});Check("Ambience delayed stereo reflections",ambience.Where((_,i)=>i%2==0).Zip(ambience.Where((_,i)=>i%2==1),(a,b)=>Math.Abs(a-b)).Average()>1e-6,"different L/R delay paths");
            var p=Profiles.Factory["Music (Музыка)"].Copy();var baseline=p.Copy();p.Gain=19;p.Balance=-50;p.Eq[3]=12;p=Profiles.Factory["Music (Музыка)"].Copy();Check("Profile snapshot remains immutable",p.Gain==baseline.Gain&&p.Balance==baseline.Balance&&p.Eq.SequenceEqual(baseline.Eq),"Deep copies preserve selected preset");
            var start=Stopwatch.StartNew();var perfDsp=new DspProcessor(Rate);perfDsp.Update(new(){Eq=Enumerable.Repeat(1d,31).ToArray(),Ambience=5,Dynamics=5});var block=Tone(1000,.005,.02);for(var i=0;i<500;i++)perfDsp.Process(block,block.Length);start.Stop();Check("Realtime DSP throughput",start.Elapsed.TotalSeconds<10,new{audioSeconds=10,computeSeconds=start.Elapsed.TotalSeconds});
        }
        catch(Exception ex){Check("Unhandled exception",false,ex.ToString());}
        var report=new{utc=DateTime.UtcNow,platform=Environment.OSVersion.ToString(),failed,tests};File.WriteAllText(path,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
    }
    public static int RunLive(string path)
    {
        var all=WindowsAudio.Enumerate();var virt=all.FirstOrDefault(d=>d.IsVirtual);var physical=all.FirstOrDefault(d=>d.Id==WindowsAudio.Default()&&!d.IsVirtual)??all.FirstOrDefault(d=>!d.IsVirtual);
        if(virt==null||physical==null){File.WriteAllText(path,JsonSerializer.Serialize(new{passed=false,reason="A signed virtual audio endpoint and a physical output are required",devices=all}));return 2;}
        var defaultsBefore=new Dictionary<string,string>{{"Console",WindowsAudio.Default(Role.Console)},{"Multimedia",WindowsAudio.Default(Role.Multimedia)}};
        var route=default(RouteBackup);var passed=false;string? error=null;double input=0,output=0,hardware=0;bool idle=false;int sampleRate=0;
        try
        {
            using var devices=new MMDeviceEnumerator();using var v=devices.GetDevice(virt.Id);using var dest=devices.GetDevice(physical.Id);
            using var engine=new AudioEngine();engine.Start(new(){Gain=6},physical.Id,false);sampleRate=engine.SampleRate;
            using var measured=new SystemLoopbackCapture(dest);measured.WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(sampleRate,2);
            measured.DataAvailable+=(_,e)=>{var floats=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(e.Buffer.AsSpan(0,e.BytesRecorded));foreach(var s in floats)hardware=Math.Max(hardware,Math.Abs(s));};measured.StartRecording();
            using var tone=new WasapiOut(v,AudioClientShareMode.Shared,true,40);tone.Init(new QuietTone(sampleRate));tone.Play();
            for(var i=0;i<30;i++){Thread.Sleep(100);input=Math.Max(input,engine.InputPeak);output=Math.Max(output,engine.OutputPeak);}
            tone.Stop();Thread.Sleep(1900);idle=!engine.Rendering;measured.StopRecording();engine.Stop();
            // Exercise restoration independently; no sound is played during this short route check.
            route=WindowsAudio.Activate(virt.Id);WindowsAudio.Restore(route);route=null;
            passed=input>0.00001&&output>input*1.7&&hardware>0.000001&&idle&&WindowsAudio.Default(Role.Multimedia)==defaultsBefore["Multimedia"];
        }
        catch(Exception ex){error=ex.ToString();}
        finally{WindowsAudio.Restore(route);}
        File.WriteAllText(path,JsonSerializer.Serialize(new{passed,utc=DateTime.UtcNow,virtualDevice=virt.Name,physicalDevice=physical.Name,sampleRate,inputPeak=input,processedPeak=output,physicalLoopbackPeak=hardware,idleRenderStopped=idle,defaultsRestored=WindowsAudio.Default(Role.Multimedia)==defaultsBefore["Multimedia"],error},new JsonSerializerOptions{WriteIndented=true}));return passed?0:1;
    }
    sealed class QuietTone(int rate):IWaveProvider
    {
        long position;
        public WaveFormat WaveFormat{get;}=WaveFormat.CreateIeeeFloatWaveFormat(rate,2);
        public int Read(byte[] buffer,int offset,int count)
        {
            var samples=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(buffer.AsSpan(offset,count));
            for(var i=0;i+1<samples.Length;i+=2){var s=(float)(.0005*Math.Sin(2*Math.PI*997*position++/rate));samples[i]=samples[i+1]=s;}return count;
        }
    }
}
