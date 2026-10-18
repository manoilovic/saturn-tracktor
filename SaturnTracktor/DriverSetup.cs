using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

namespace SaturnTracktor;

public static class AppAssets
{
    public static string DirectoryPath=>Path.Combine(StateStore.Root,"App","1.0.2");
    public static string Extract(string name,string? directory=null)
    {
        var target=directory??DirectoryPath;Directory.CreateDirectory(target);var path=Path.Combine(target,name);
        var asm=Assembly.GetExecutingAssembly();
        var resource=asm.GetManifestResourceNames().Single(r=>r.EndsWith(".Assets."+name,StringComparison.Ordinal));
        using var source=asm.GetManifestResourceStream(resource)!;using var dest=File.Create(path);source.CopyTo(dest);return path;
    }
}

public static class DriverSetup
{
    public static async Task<int> InstallAsync()
    {
        // The helper is this same executable. Only the signed third-party driver is installed.
        var start=new ProcessStartInfo(Environment.ProcessPath!,"--install-driver"){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось открыть установку аудиокомпонента.");
        await process.WaitForExitAsync();return process.ExitCode;
    }
    public static int InstallElevated()
    {
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))return 5;
        var originalDefaults=new Dictionary<NAudio.CoreAudioApi.Role,string>();
        foreach(var role in new[]{NAudio.CoreAudioApi.Role.Console,NAudio.CoreAudioApi.Role.Multimedia})
            try{originalDefaults[role]=WindowsAudio.Default(role);}catch{}
        try
        {
            if(WindowsAudio.Enumerate().Any(d=>d.IsVirtual)){WindowsAudio.RenameVirtualEndpoint();return 0;}
            var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"SaturnTracktor","Driver");
            foreach(var name in new[]{"fxvad.inf","fxvad.sys","fxvadntamd64.cat","fxdevcon64.exe"})AppAssets.Extract(name,dir);
            var priorLog=Path.Combine(dir,"installation.log");
            if(File.Exists(priorLog)&&File.ReadAllText(priorLog).Contains("installation failed",StringComparison.OrdinalIgnoreCase))
            {
                // Remove only the failed installation left by this application's own previous attempt.
                var cleanup=new ProcessStartInfo(Path.Combine(dir,"fxdevcon64.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=dir};
                cleanup.ArgumentList.Add("remove");cleanup.ArgumentList.Add(@"Root\FXVAD");
                using var prior=Process.Start(cleanup);prior?.WaitForExit();
            }
            var start=new ProcessStartInfo(Path.Combine(dir,"fxdevcon64.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=dir};
            start.ArgumentList.Add("install");start.ArgumentList.Add(Path.Combine(dir,"fxvad.inf"));start.ArgumentList.Add(@"Root\FXVAD");
            using var p=Process.Start(start)!;var stdout=p.StandardOutput.ReadToEnd();var stderr=p.StandardError.ReadToEnd();p.WaitForExit();
            File.WriteAllText(Path.Combine(dir,"installation.log"),stdout+Environment.NewLine+stderr);
            if(p.ExitCode==0)
            {
                for(var attempt=0;attempt<25;attempt++)
                {
                    if(WindowsAudio.Enumerate().Any(d=>d.IsVirtual)){WindowsAudio.RenameVirtualEndpoint();return 0;}
                    Thread.Sleep(200);
                }
                return 1;
            }
            return p.ExitCode;
        }
        catch(Exception ex){StateStore.Log("Driver setup: "+ex);return 2;}
        finally
        {
            // Newly installed devices can become Windows' default. Preserve the user's route.
            foreach(var item in originalDefaults)try
            {
                var current=WindowsAudio.Default(item.Key);
                if(WindowsAudio.Enumerate().Any(d=>d.Id==current&&d.IsVirtual))WindowsAudio.SetDefault(item.Value,item.Key);
            }catch{}
        }
    }
}
