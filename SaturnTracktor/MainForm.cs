using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NAudio.CoreAudioApi;

namespace SaturnTracktor;

public sealed class MainForm:Form
{
    readonly WebView2 view=new();
    readonly AudioEngine engine=new();
    readonly StoredState state=StateStore.Load();
    readonly System.Windows.Forms.Timer meter=new(){Interval=100};
    readonly System.Windows.Forms.Timer persist=new(){Interval=600};
    readonly NotifyIcon tray=new();
    bool busy,closing,ready;
    readonly string? uiCheck;
    Rectangle normalBounds;
    Process? watchdog;
    readonly SemaphoreSlim lifecycle=new(1,1);

    public MainForm(string? uiCheckPath=null)
    {
        uiCheck=uiCheckPath;Text="Saturn Tracktor";BackColor=Color.FromArgb(19,21,25);FormBorderStyle=FormBorderStyle.None;
        StartPosition=FormStartPosition.CenterScreen;AutoScaleMode=AutoScaleMode.Dpi;
        Size=new Size(1580,880);MinimumSize=new Size(1460,780);
        using(var s=typeof(MainForm).Assembly.GetManifestResourceStream("SaturnTracktor.Assets.Saturn.ico"))if(s!=null)Icon=new Icon(s);
        var area=Screen.PrimaryScreen!.WorkingArea;Size=new Size(Math.Min(1580,area.Width-24),Math.Min(880,area.Height-24));
        view.Dock=DockStyle.Fill;view.DefaultBackgroundColor=BackColor;Controls.Add(view);
        Shown+=async (_,_)=>await InitializeBrowser();
        FormClosing+=OnClosing;
        engine.Failed+=message=>{if(!closing&&IsHandleCreated)BeginInvoke(async()=>{await StopAudio();Send(new{type="error",message});});};
        meter.Tick+=(_,_)=>Send(new{type="meter",active=engine.Running,rendering=engine.Rendering,rate=engine.SampleRate,spectrum=engine.Spectrum,peak=engine.OutputPeak,limiter=engine.LimiterReduction});
        persist.Tick+=(_,_)=>{persist.Stop();try{StateStore.Save(state);}catch(Exception ex){StateStore.Log(ex.Message);}};
        tray.Icon=Icon;tray.Text="Saturn Tracktor";tray.Visible=true;
        var menu=new ContextMenuStrip();menu.Items.Add("Открыть",null,(_,_)=>{Show();WindowState=FormWindowState.Normal;Activate();});
        menu.Items.Add("Выключить обработку",null,async(_,_)=>await StopAudio());menu.Items.Add("Выход",null,(_,_)=>Close());tray.ContextMenuStrip=menu;
        tray.DoubleClick+=(_,_)=>{Show();WindowState=FormWindowState.Normal;Activate();};
        Microsoft.Win32.SystemEvents.PowerModeChanged+=PowerChanged;
    }
    async Task InitializeBrowser()
    {
        try
        {
            foreach(var asset in new[]{"app.html","app.js","logo.png","readable.css"})AppAssets.Extract(asset);
            var options=new CoreWebView2EnvironmentOptions("--autoplay-policy=user-gesture-required");
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(StateStore.Root,"WebView"),options);
            await view.EnsureCoreWebView2Async(environment);
            view.ZoomFactor=1;
            view.CoreWebView2.Settings.AreDevToolsEnabled=false;view.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            view.CoreWebView2.Settings.IsStatusBarEnabled=false;view.CoreWebView2.Settings.IsZoomControlEnabled=false;
            view.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled=false;
            view.CoreWebView2.SetVirtualHostNameToFolderMapping("saturn.local",AppAssets.DirectoryPath,CoreWebView2HostResourceAccessKind.DenyCors);
            view.CoreWebView2.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith("https://saturn.local/",StringComparison.OrdinalIgnoreCase)&&e.Uri!="about:blank")e.Cancel=true;};
            view.CoreWebView2.NewWindowRequested+=(_,e)=>e.Handled=true;
            view.CoreWebView2.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            view.CoreWebView2.WebMessageReceived+=MessageReceived;
            view.CoreWebView2.NavigationCompleted+=async(_,e)=>
            {
                if(!e.IsSuccess){StateStore.Log("UI navigation: "+e.WebErrorStatus);return;}
                ready=true;SetCompact(state.Compact);SendInitial();meter.Start();
                if(uiCheck!=null){await Task.Delay(500);await CaptureUi(uiCheck);}
            };
            view.Source=new Uri("https://saturn.local/app.html");
        }
        catch(WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("Для интерфейса нужен Microsoft Edge WebView2. Установите его по официальной ссылке и запустите Saturn Tracktor снова.",Text,MessageBoxButtons.OK,MessageBoxIcon.Information);
            Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703"){UseShellExecute=true});Close();
        }
        catch(Exception ex){StateStore.Log(ex.ToString());MessageBox.Show("Не удалось открыть интерфейс: "+ex.Message,Text);Close();}
    }
    void SendInitial()
    {
        var devices=WindowsAudio.Enumerate();
        if(!devices.Any(d=>d.Id==state.DeviceId&&!d.IsVirtual))state.DeviceId=devices.FirstOrDefault(d=>d.Id==SafeDefault()&&!d.IsVirtual)?.Id??devices.FirstOrDefault(d=>!d.IsVirtual)?.Id??"";
        var profiles=Profiles.Factory.ToDictionary(p=>p.Key,p=>p.Value.Copy());foreach(var p in state.UserProfiles)profiles[p.Key]=p.Value.Copy();
        Send(new{type="init",state,profiles,devices=devices.Where(d=>!d.IsVirtual),driver=devices.Any(d=>d.IsVirtual),on=engine.Running});
    }
    static string SafeDefault(){try{return WindowsAudio.Default();}catch{return "";}}
    void Send(object data){if(ready&&!closing)try{view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data));}catch(Exception ex){StateStore.Log(ex.Message);}}
    async void MessageReceived(object? sender,CoreWebView2WebMessageReceivedEventArgs args)
    {
        if(!args.Source.StartsWith("https://saturn.local/",StringComparison.OrdinalIgnoreCase))return;
        try
        {
            using var doc=JsonDocument.Parse(args.WebMessageAsJson);var root=doc.RootElement;var type=root.GetProperty("type").GetString();
            switch(type)
            {
                case "ready": SendInitial();break;
                case "settings":
                    var s=root.GetProperty("settings").Deserialize<AudioSettings>();if(s==null)break;s.Validate();state.Current=s;engine.Update(s);QueueSave();break;
                case "profile":
                    var name=root.GetProperty("name").GetString()??"";
                    if(state.UserProfiles.TryGetValue(name,out var user))state.Current=user.Copy();else if(Profiles.Factory.TryGetValue(name,out var factory))state.Current=factory.Copy();else break;
                    state.Profile=name;engine.Update(state.Current);QueueSave();SendInitial();break;
                case "reset":
                    state.Current=(state.UserProfiles.TryGetValue(state.Profile,out var preset)?preset:Profiles.Factory.GetValueOrDefault(state.Profile,new())).Copy();engine.Update(state.Current);QueueSave();SendInitial();break;
                case "saveProfile":
                    var label=(root.GetProperty("name").GetString()??"").Trim();if(label.Length<1||label.Length>60||Profiles.Factory.ContainsKey(label))throw new InvalidOperationException("Укажите уникальное название профиля до 60 символов.");
                    state.UserProfiles[label]=state.Current.Copy();state.Profile=label;QueueSave();SendInitial();break;
                case "theme":
                    var color=root.GetProperty("color").GetString()??"";if(new[]{"#ff1525","#4bdb87","#ffe127"}.Contains(color)){state.Theme=color;QueueSave();}break;
                case "device":
                    var id=root.GetProperty("id").GetString()??"";if(!WindowsAudio.Enumerate().Any(d=>d.Id==id&&!d.IsVirtual))break;
                    var wasOn=engine.Running;await StopAudio();state.DeviceId=id;QueueSave();if(wasOn)await StartAudio();break;
                case "power":if(engine.Running)await StopAudio();else await StartAudio();break;
                case "compact":SetCompact(root.GetProperty("value").GetBoolean());QueueSave();break;
                case "minimize":WindowState=FormWindowState.Minimized;break;
                case "maximize":WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;break;
                case "close":Close();break;
                case "drag":ReleaseCapture();SendMessage(Handle,0xA1,(IntPtr)2,IntPtr.Zero);break;
                case "installDriver":await InstallDriver();break;
                case "refreshDevices":SendInitial();break;
            }
        }
        catch(Exception ex){StateStore.Log(ex.ToString());Send(new{type="error",message=ex.Message});}
    }
    void QueueSave(){persist.Stop();persist.Start();}
    async Task StartAudio()
    {
        if(busy)return;busy=true;Send(new{type="busy",value=true});
        await lifecycle.WaitAsync();
        try
        {
            if(closing)return;
            await Task.Run(()=>engine.Start(state.Current,state.DeviceId));
            watchdog=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--watch "+Environment.ProcessId){UseShellExecute=false,CreateNoWindow=true});
            Send(new{type="power",on=true});
        }
        catch(Exception ex){Send(new{type="power",on=false});Send(new{type="error",message=ex.Message});}
        finally{lifecycle.Release();busy=false;Send(new{type="busy",value=false});}
    }
    async Task StopAudio()
    {
        await lifecycle.WaitAsync();
        try{await Task.Run(()=>engine.Stop());}finally{lifecycle.Release();}
        Send(new{type="power",on=false});
        try{if(watchdog!=null&&!watchdog.HasExited)watchdog.Kill();}catch{}watchdog?.Dispose();watchdog=null;
    }
    async Task InstallDriver()
    {
        if(busy)return;busy=true;Send(new{type="busy",value=true});
        try
        {
            var code=await DriverSetup.InstallAsync();
            await Task.Delay(1600);SendInitial();
            if(code==1)Send(new{type="error",message="Компонент установлен. Перезагрузите Windows, затем включите обработку."});
            else if(code!=0)throw new InvalidOperationException("Установка не завершена (код "+code+"). Подробности в журнале установки.");
            else Send(new{type="notice",message="Аудиокомпонент установлен. Можно включать обработку."});
        }
        catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){Send(new{type="notice",message="Установка отменена. Звук Windows не изменён."});}
        finally{busy=false;Send(new{type="busy",value=false});}
    }
    void SetCompact(bool value)
    {
        if(value&&!state.Compact)normalBounds=Bounds;
        state.Compact=value;
        if(WindowState!=FormWindowState.Normal)WindowState=FormWindowState.Normal;
        var area=Screen.FromControl(this).WorkingArea;
        var scale=DeviceDpi/96d;
        Size Fit(int width,int height)=>new(Math.Min((int)Math.Round(width*scale),area.Width-24),Math.Min((int)Math.Round(height*scale),area.Height-24));
        if(value){MinimumSize=Fit(600,180);Size=MinimumSize;}
        else
        {
            MinimumSize=Fit(1460,800);
            Size=normalBounds.Width>=MinimumSize.Width?new Size(Math.Min(normalBounds.Width,area.Width-24),Math.Min(normalBounds.Height,area.Height-24)):Fit(1600,900);
        }
        MaximizedBounds=area;
        if(Right>area.Right)Left=Math.Max(area.Left,area.Right-Width);if(Bottom>area.Bottom)Top=Math.Max(area.Top,area.Bottom-Height);
        Send(new{type="compact",value});
    }
    async Task CaptureUi(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var original=JsonSerializer.Deserialize<StoredState>(JsonSerializer.Serialize(state))!;
        var results=new List<object>();
        async Task<string> Script(string script)=>await view.CoreWebView2.ExecuteScriptAsync(script);
        async Task<bool> Verify(string name,string expression)
        {
            var value=await Script("Boolean("+expression+")");var pass=value=="true";results.Add(new{name,passed=pass});return pass;
        }
        try
        {
            await Verify("All bands and circular knobs", "document.querySelectorAll('.band').length===31 && document.querySelectorAll('.knob circle').length===31");
            await Verify("No separate Save button", "!document.getElementById('save')");
            await Script("$('profile').value='Music (Музыка)';$('profile').onchange();");await Task.Delay(150);
            await Verify("Distinct Music preset", "Number($('c3').value)===2 && Number(eq[0].value)===2 && Number(eq[15].value)===-.5");
            await Script("controls.forEach((e,i)=>e.value=i===2?3:8);$('balance').value=-60;eq.forEach(e=>e.value=10);update();");await Task.Delay(120);
            await Script("document.querySelector('[data-color=\"#ffe127\"]').click();$('reset').click();");await Task.Delay(150);
            await Verify("Reset restores every regulator from profile", "controls.every((e,i)=>Number(e.value)===[0,0,1,2,0,0,0,0][i])&&Number($('balance').value)===0&&Number(eq[0].value)===2&&Number(eq[15].value)===-.5");
            await Verify("Reset preserves theme and device", "state.Theme==='#ffe127'&&state.DeviceId==="+JsonSerializer.Serialize(original.DeviceId));
            await Script("eq[0].value=12;eq[1].value=4;update();$('plus').click();");await Task.Delay(100);
            await Verify("EQ group shifts by 1 dB and clamps", "Number(eq[0].value)===12&&Number(eq[1].value)===5");
            await Script("$('compactGain').value=20;$('compactGain').dispatchEvent(new Event('input'));");await Task.Delay(100);
            await Verify("Compact gain converts 20 dB to 900 percent", "$('percent').textContent==='+900%'&&Number($('c0').value)===20");
            await Script("$('c0').value=4;update();");await Task.Delay(100);
            await Verify("Slider fill follows value", "parseFloat($('c0').style.getPropertyValue('--fill'))<25 && parseFloat($('compactGain').style.getPropertyValue('--fill'))<25");
            await Script("$('profile').value='__create__';$('profile').onchange();");await Task.Delay(100);
            await Script("$('profileName').value='UI-test preset';$('profileCreate').click();");await Task.Delay(150);
            await Verify("Custom preset creation through profile menu", "state.Profile==='UI-test preset'&&Number($('c0').value)===4");
            await Script("$('c0').value=12;update();");await Task.Delay(100);await Script("$('reset').click();");await Task.Delay(120);
            await Verify("Custom preset resets to saved gain", "Number($('c0').value)===4");
            SetCompact(true);await Task.Delay(150);
            await Verify("Compact view contains one visible slider", "[...document.querySelectorAll('input[type=range]')].filter(e=>e.getBoundingClientRect().height>0).length===1");
        }
        finally
        {
            state.Profile=original.Profile;state.Theme=original.Theme;state.Current=original.Current;state.DeviceId=original.DeviceId;state.UserProfiles=original.UserProfiles;
            SetCompact(false);SendInitial();await Task.Delay(150);
        }
        using(var file=File.Create(path))await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
        var geometry=await Script("({ready:!!document.querySelector('.knob'),bands:document.querySelectorAll('.band').length,driver:window.appDriver,bodyWidth:document.body.scrollWidth,viewWidth:innerWidth})");
        SetCompact(true);SendInitial();await Task.Delay(150);
        var compactPath=Path.Combine(Path.GetDirectoryName(path)!,"SaturnTracktor-native-compact.png");
        using(var f=File.Create(compactPath))await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,f);
        var compactGeometry=await Script("({width:innerWidth,height:innerHeight,bodyWidth:document.body.scrollWidth,contentBottom:document.querySelector('.compact .scale').getBoundingClientRect().bottom})");
        File.WriteAllText(Path.ChangeExtension(path,".json"),JsonSerializer.Serialize(new{results,geometry,compactGeometry},new JsonSerializerOptions{WriteIndented=true}));
        SetCompact(original.Compact);Close();
    }
    async void OnClosing(object? sender,FormClosingEventArgs args)
    {
        if(closing)return;args.Cancel=true;closing=true;meter.Stop();persist.Stop();
        await lifecycle.WaitAsync();try{await Task.Run(()=>engine.Stop());}finally{lifecycle.Release();}
        try{StateStore.Save(state);}catch{}tray.Visible=false;tray.Dispose();
        Microsoft.Win32.SystemEvents.PowerModeChanged-=PowerChanged;
        try{if(watchdog!=null&&!watchdog.HasExited)watchdog.Kill();}catch{}
        view.Dispose();args.Cancel=false;BeginInvoke(Close);
    }
    async void PowerChanged(object sender,Microsoft.Win32.PowerModeChangedEventArgs e)
    {if(e.Mode==Microsoft.Win32.PowerModes.Suspend&&!closing)await StopAudio();}
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);int dark=1,corner=2;DwmSetWindowAttribute(Handle,20,ref dark,4);DwmSetWindowAttribute(Handle,33,ref corner,4);
    }
    protected override void WndProc(ref Message m)
    {
        const int HitTest=0x84;const int NcCalcSize=0x83;
        if(m.Msg==NcCalcSize&&m.WParam!=IntPtr.Zero){m.Result=IntPtr.Zero;return;}
        base.WndProc(ref m);
        if(m.Msg==HitTest&&WindowState==FormWindowState.Normal)
        {
            var point=PointToClient(new Point((short)(m.LParam.ToInt64()&0xffff),(short)((m.LParam.ToInt64()>>16)&0xffff)));const int edge=7;
            var left=point.X<edge;var right=point.X>ClientSize.Width-edge;var top=point.Y<edge;var bottom=point.Y>ClientSize.Height-edge;
            m.Result=(IntPtr)(top&&left?13:top&&right?14:bottom&&left?16:bottom&&right?17:left?10:right?11:top?12:bottom?15:m.Result.ToInt32());
        }
    }
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.Style|=0x00040000|0x00020000|0x00010000;p.ClassStyle|=0x20000;return p;}}
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr h,int a,ref int v,int size);
    [DllImport("user32.dll")]static extern bool ReleaseCapture();
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
}
