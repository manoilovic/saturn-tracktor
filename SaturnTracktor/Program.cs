using System.Diagnostics;
using System.Text.Json;

namespace SaturnTracktor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if(args.Contains("--install-driver"))return DriverSetup.InstallElevated();
        if(args.Contains("--rename-device"))
        {
            try{WindowsAudio.RenameVirtualEndpoint();return 0;}catch(Exception ex){StateStore.Log("Rename: "+ex);return 1;}
        }
        if(args.Length>1&&args[0]=="--watch")
        {
            try{Process.GetProcessById(int.Parse(args[1])).WaitForExit();}catch{}WindowsAudio.Recover();return 0;
        }
        if(args.Length>1&&args[0]=="--self-test")return Diagnostics.RunOffline(args[1]);
        if(args.Length>1&&args[0]=="--devices")
        {
            File.WriteAllText(args[1],JsonSerializer.Serialize(new{devices=WindowsAudio.Enumerate(),defaultId=WindowsAudio.Default()},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        if(args.Length>1&&args[0]=="--audio-test")return Diagnostics.RunLive(args[1]);
        using var mutex=new Mutex(true,"Local\\SaturnTracktor-v1",out var created);
        if(!created){MessageBox.Show("Saturn Tracktor уже запущен. Откройте его через значок в трее.","Saturn Tracktor");return 0;}
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException+=(_,e)=>StateStore.Log(e.Exception.ToString());
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{StateStore.Log(e.ExceptionObject.ToString()??"Unhandled");WindowsAudio.Recover();};
        WindowsAudio.Recover();
        Application.Run(new MainForm(args.Length>1&&args[0]=="--ui-check"?args[1]:null));return 0;
    }
}
