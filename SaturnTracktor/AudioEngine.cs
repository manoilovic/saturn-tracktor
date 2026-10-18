using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SaturnTracktor;

public sealed class SystemLoopbackCapture:WasapiCapture
{
    public SystemLoopbackCapture(MMDevice device):base(device,false,20){}
    protected override AudioClientStreamFlags GetAudioClientStreamFlags()=>base.GetAudioClientStreamFlags()|AudioClientStreamFlags.Loopback;
}

public sealed class AudioEngine:IDisposable
{
    readonly object gate=new();
    SystemLoopbackCapture? capture;
    WasapiOut? output;
    BufferedWaveProvider? buffer;
    StereoDspProvider? provider;
    MMDevice? inputDevice,outputDevice;
    System.Threading.Timer? timer;
    RouteBackup? backup;
    volatile bool running,rendering;
    long lastAudioTicks;
    int faulted;
    public bool Running=>running;
    public bool Rendering=>rendering;
    public int SampleRate=>provider?.WaveFormat.SampleRate??0;
    public string? Error {get;private set;}
    public double InputPeak {get;private set;}
    public double OutputPeak=>rendering?(provider?.Processor.Peak??0):0;
    public double LimiterReduction=>rendering?(provider?.Processor.LimiterReductionDb??0):0;
    public double[] Spectrum=>rendering?provider?.Processor.Spectrum.Read()??new double[31]:new double[31];
    public event Action<string>? Failed;

    public void Start(AudioSettings settings,string destinationId,bool changeDefault=true)
    {
        Stop();Error=null;faulted=0;
        var all=WindowsAudio.Enumerate();var virt=all.FirstOrDefault(d=>d.IsVirtual);
        if(virt==null)throw new InvalidOperationException("Не установлен системный аудиокомпонент. Нажмите «Настроить звук». Без него обработка Windows недоступна.");
        if(Process.GetProcessesByName("FxSound").Length>0)throw new InvalidOperationException("Закройте приложение FxSound перед включением Saturn Tracktor, чтобы звук не обрабатывался дважды.");
        var physical=all.FirstOrDefault(d=>d.Id==destinationId&&!d.IsVirtual)??all.FirstOrDefault(d=>d.Id==WindowsAudio.Default()&&!d.IsVirtual)??all.FirstOrDefault(d=>!d.IsVirtual);
        if(physical==null)throw new InvalidOperationException("Подключите наушники или динамики.");
        try
        {
            using var devices=new MMDeviceEnumerator();
            inputDevice=devices.GetDevice(virt.Id);outputDevice=devices.GetDevice(physical.Id);
            capture=new SystemLoopbackCapture(inputDevice);
            var sampleRate=capture.WaveFormat.SampleRate;
            capture.WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(sampleRate,2);
            buffer=new BufferedWaveProvider(capture.WaveFormat){BufferDuration=TimeSpan.FromMilliseconds(500),DiscardOnBufferOverflow=true,ReadFully=true};
            provider=new StereoDspProvider(buffer.ToSampleProvider());provider.Processor.Update(settings);
            output=new WasapiOut(outputDevice,AudioClientShareMode.Shared,true,40);output.Init(provider.ToWaveProvider());
            capture.DataAvailable+=CaptureData;
            capture.RecordingStopped+=(_,e)=>{if(e.Exception!=null&&running)ReportError(e.Exception);};
            output.PlaybackStopped+=(_,e)=>{if(e.Exception!=null&&running)ReportError(e.Exception);};
            lastAudioTicks=0;running=true;rendering=false;
            capture.StartRecording();
            if(changeDefault)backup=WindowsAudio.Activate(virt.Id,physical.Id);
            timer=new System.Threading.Timer(ControlOutput,null,10,10);
            StateStore.Log("Audio started: "+sampleRate+"Hz -> "+physical.Name);
        }
        catch{Stop();throw;}
    }
    void CaptureData(object? sender,WaveInEventArgs e)
    {
        if(!running||buffer==null)return;
        var samples=MemoryMarshal.Cast<byte,float>(e.Buffer.AsSpan(0,e.BytesRecorded));
        float peak=0;foreach(var sample in samples)peak=Math.Max(peak,Math.Abs(sample));
        InputPeak=peak;
        if(peak>0.0000003f)Interlocked.Exchange(ref lastAudioTicks,Stopwatch.GetTimestamp());
        // Bound latency if a device clock drifts or the output briefly stalls.
        if(buffer.BufferedDuration.TotalMilliseconds>220)buffer.ClearBuffer();
        buffer.AddSamples(e.Buffer,0,e.BytesRecorded);
    }
    void ControlOutput(object? state)
    {
        if(!Monitor.TryEnter(gate))return;
        try
        {
            if(!running||output==null||buffer==null)return;
            var stamp=Interlocked.Read(ref lastAudioTicks);
            var active=stamp>0&&(Stopwatch.GetTimestamp()-stamp)/(double)Stopwatch.Frequency<1.4;
            if(active&&!rendering&&buffer.BufferedBytes>0){output.Play();rendering=true;}
            else if(!active&&rendering){output.Stop();rendering=false;buffer.ClearBuffer();InputPeak=0;StateStore.Log("Output idle: render stream stopped");}
            else if(!active)buffer.ClearBuffer();
        }
        catch(Exception ex){ReportError(ex);}
        finally{Monitor.Exit(gate);}
    }
    void ReportError(Exception ex)
    {
        if(Interlocked.Exchange(ref faulted,1)!=0)return;
        Error=ex.Message;StateStore.Log("Audio error: "+ex);Failed?.Invoke(ex.Message);
    }
    public void Update(AudioSettings settings)=>provider?.Processor.Update(settings);
    public void Stop()
    {
        running=false;timer?.Dispose();timer=null;
        try{capture?.StopRecording();}catch(Exception ex){StateStore.Log(ex.Message);}
        lock(gate)
        {
            try{output?.Stop();}catch{}try{output?.Dispose();}catch{}try{capture?.Dispose();}catch{}
            output=null;capture=null;provider=null;buffer=null;rendering=false;InputPeak=0;
            inputDevice?.Dispose();outputDevice?.Dispose();inputDevice=null;outputDevice=null;
        }
        WindowsAudio.Restore(backup);backup=null;
    }
    public void Dispose()=>Stop();
}
