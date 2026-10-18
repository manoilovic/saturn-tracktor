using System.Text.Json;

namespace SaturnTracktor;

public sealed class AudioSettings
{
    public double Gain { get; set; }
    public double Leveling { get; set; }
    public double Q { get; set; } = 1;
    public double Clarity { get; set; }
    public double Ambience { get; set; }
    public double Surround { get; set; }
    public double Dynamics { get; set; }
    public double Bass { get; set; }
    public double Balance { get; set; }
    public double[] Eq { get; set; } = new double[31];
    public AudioSettings Copy() => JsonSerializer.Deserialize<AudioSettings>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        double Clamp(double v,double lo,double hi) => double.IsFinite(v) ? Math.Clamp(v,lo,hi) : 0;
        Gain=Clamp(Gain,0,20);Leveling=Clamp(Leveling,0,10);Q=Clamp(Q,.5,4);
        Clarity=Clamp(Clarity,0,10);Ambience=Clamp(Ambience,0,10);Surround=Clamp(Surround,0,10);
        Dynamics=Clamp(Dynamics,0,10);Bass=Clamp(Bass,0,10);Balance=Clamp(Balance,-100,100);
        if(Eq.Length!=31) Eq=new double[31];
        Eq=Eq.Select(v=>Clamp(v,-12,12)).ToArray();
    }
}

public sealed class StoredState
{
    public string Profile { get; set; } = "General (Универсальный)";
    public string Theme { get; set; } = "#ff1525";
    public string DeviceId { get; set; } = "";
    public bool Compact { get; set; }
    public AudioSettings Current { get; set; } = new();
    public Dictionary<string, AudioSettings> UserProfiles { get; set; } = new();
}

public static class StateStore
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SaturnTracktor");
    public static string StatePath => Path.Combine(Root,"settings.json");
    public static StoredState Load()
    {
        try { var s=JsonSerializer.Deserialize<StoredState>(File.ReadAllText(StatePath)); if(s!=null){s.Current.Validate();foreach(var p in s.UserProfiles.Values)p.Validate();return s;} }catch{}
        return new StoredState { Current=Profiles.Factory["General (Универсальный)"].Copy() };
    }
    public static void Save(StoredState state)
    {
        Directory.CreateDirectory(Root);var tmp=StatePath+".tmp";
        File.WriteAllText(tmp,JsonSerializer.Serialize(state,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,StatePath,true);
    }
    public static void Log(string text)
    {
        try {Directory.CreateDirectory(Root);File.AppendAllText(Path.Combine(Root,"app.log"),DateTime.UtcNow.ToString("O")+" "+text+Environment.NewLine);}catch{}
    }
}

public static class Profiles
{
    public static readonly double[] Frequencies={20,25,31.5,40,50,63,80,100,125,160,200,250,315,400,500,630,800,1000,1250,1600,2000,2500,3150,4000,5000,6300,8000,10000,12500,16000,20000};
    public static Dictionary<string,AudioSettings> Factory { get; }=Build();
    static Dictionary<string,AudioSettings> Build()
    {
        var d=new Dictionary<string,AudioSettings>();
        AudioSettings Shape(double low,double mid,double high) => new(){Eq=Frequencies.Select(f=>f<180?low:f<2000?mid:high).ToArray()};
        d["General (Универсальный)"]=new();
        d["Music (Музыка)"]=Shape(2,-.5,1);d["Music (Музыка)"].Clarity=2;
        d["Movies (Кино)"]=Shape(2,1,1);d["Movies (Кино)"].Ambience=2;d["Movies (Кино)"].Leveling=3;
        d["TV (Телевидение)"]=Shape(-1,2,0);d["TV (Телевидение)"].Leveling=4;
        d["Transcription (Разбор речи)"]=Shape(-5,3,-2);
        d["Voice (Голос)"]=Shape(-3,2,-1);d["Voice (Голос)"].Leveling=2;
        d["Volume Boost (Усиление)"]=new(){Gain=6,Dynamics=2};
        d["Gaming (Игры)"]=Shape(-1,1,2);d["Gaming (Игры)"].Surround=2;
        d["Classic Processing (Классический)"]=new(){Clarity=2,Bass=2,Dynamics=2};
        d["Light Processing (Мягкий)"]=new(){Clarity=1,Bass=1};
        d["Bass Boost (Бас)"]=Shape(4,0,0);d["Bass Boost (Бас)"].Bass=2;
        d["Streaming Video (Онлайн-видео)"]=Shape(0,1,0);d["Streaming Video (Онлайн-видео)"].Leveling=3;
        d["Default (Без эффектов)"]=new();return d;
    }
}
