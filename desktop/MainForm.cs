using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCBoosterDesktop;

public sealed class MainForm : Form
{
    private readonly Panel sidebar = new();
    private readonly Panel content = new();
    private readonly Label pageTitle = new();
    private readonly Label status = new();
    private readonly Dictionary<string, Control> pages = new();
    private readonly List<Button> navigationButtons = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 6000 };
    private bool statsBusy;
    private bool processesBusy;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private bool light;
    private string currentPage = "Обзор";
    private Label cpuValue = new(), ramValue = new(), diskValue = new(), osValue = new();
    private Label scanSummary = new();
    private DataGridView processGrid = new();
    private FlowLayoutPanel drivesPanel = new(), tweaksPanel = new();
    private RichTextBox chatLog = new();
    private TextBox chatInput = new();
    private readonly List<(string role,string content)> chatHistory = new();
    private Label updateStatusLabel = new();
    private Button installUpdateButton = new();
    private UpdateManifest? availableUpdate;
    private string? pendingUpdatePath;
    private long pendingUpdateTransferSize;
    private bool updateBusy;
    private readonly Dictionary<string,List<RegValue>> rollback = new();
    private (ulong idle, ulong kernel, ulong user)? previousCpuSample;
    private readonly string rollbackPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PCBooster","desktop-rollback.json");
    private static readonly Tweak[] tweakItems = {
        new("show_file_extensions","Показывать расширения файлов","Проводник","Показывает .exe, .txt и другие расширения.",@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","HideFileExt",RegistryValueKind.DWord,0),
        new("show_hidden_files","Показывать скрытые файлы","Проводник","Включает отображение скрытых файлов и папок.",@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","Hidden",RegistryValueKind.DWord,1),
        new("disable_transparency","Отключить прозрачность","Внешний вид","Уменьшает визуальные эффекты интерфейса Windows.",@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","EnableTransparency",RegistryValueKind.DWord,0),
        new("performance_visual_effects","Приоритет быстродействия","Производительность","Выбирает профиль визуальных эффектов Windows на производительность.",@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects","VisualFXSetting",RegistryValueKind.DWord,2),
        new("disable_game_dvr","Отключить фоновую запись игр","Игры","Отключает фоновую запись игровых клипов для текущего пользователя.",@"Software\Microsoft\Windows\CurrentVersion\GameDVR","AppCaptureEnabled",RegistryValueKind.DWord,0),
        new("disable_mouse_acceleration","Отключить ускорение мыши","Управление","Отключает ускорение курсора мыши.",@"Control Panel\Mouse","MouseSpeed",RegistryValueKind.String,"0"),
        new("faster_menu_delay","Ускорить открытие меню","Интерфейс","Уменьшает задержку открытия меню Windows.",@"Control Panel\Desktop","MenuShowDelay",RegistryValueKind.String,"100")
    };

    public MainForm()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        UpdateStyles();
        Text = "PC Booster";
        MinimumSize = new Size(1020,700);
        Size = new Size(1240,820);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI",9.5f);
        LoadRollback();
        BuildShell();
        Shown += (_, _) => CheckForUpdates();
        ShowPage("Обзор");
        _ = RefreshStatsAsync();
        refreshTimer.Tick += async (_,_) => await RefreshStatsAsync();
        refreshTimer.Start();
        FormClosed += (_,_) =>
        {
            refreshTimer.Stop();
            http.Dispose();
            if (pendingUpdatePath != null)
                try { if (File.Exists(pendingUpdatePath)) File.Delete(pendingUpdatePath); } catch { }
        };
        ApplyTheme();
    }

    private void BuildShell()
    {
        sidebar.Dock=DockStyle.Left; sidebar.Width=236; sidebar.Padding=new Padding(18,22,18,18);
        content.Dock=DockStyle.Fill; content.Padding=new Padding(30,24,30,26);
        Controls.Add(content); Controls.Add(sidebar);
        var brand=new Label { Text="✦  PC BOOSTER", AutoSize=false, Height=54, Dock=DockStyle.Top, Font=new Font("Segoe UI",16,FontStyle.Bold), TextAlign=ContentAlignment.MiddleLeft };
        sidebar.Controls.Add(brand);
        var nav=new SmoothFlowPanel { Dock=DockStyle.Top, Height=410, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(0,22,0,0) };
        sidebar.Controls.Add(nav);
        var navigation = new (string Name,string Caption)[] {
            ("Обзор","◈   Обзор"),("Проверка ПК","⌕   Проверка ПК"),("Оптимизация","✧   Оптимизация"),
            ("Процессы","▦   Процессы"),("AI-помощник","✦   AI-помощник"),("Настройки","⚙   Настройки")
        };
        foreach(var item in navigation)
        {
            var name=item.Name;
            var b=new Button { Text=item.Caption, Width=198, Height=48, TextAlign=ContentAlignment.MiddleLeft, FlatStyle=FlatStyle.Flat, Tag=name, Margin=new Padding(0,0,0,8), Padding=new Padding(12,0,8,0), Font=new Font("Segoe UI",10,FontStyle.Bold), Cursor=Cursors.Hand };
            b.FlatAppearance.BorderSize=0;
            b.FlatAppearance.MouseOverBackColor=Color.FromArgb(35,61,43);
            b.Click+=(_,_)=>ShowPage(name);
            navigationButtons.Add(b);
            nav.Controls.Add(b);
        }
        var bottom=new Label { Text="WINDOWS 10 / 11\nЛокальное приложение", Dock=DockStyle.Bottom, Height=48, Font=new Font("Segoe UI",8), TextAlign=ContentAlignment.BottomLeft };
        sidebar.Controls.Add(bottom);
        var top=new Panel { Dock=DockStyle.Top, Height=62 };
        pageTitle.Dock=DockStyle.Left; pageTitle.AutoSize=true; pageTitle.Font=new Font("Segoe UI",22,FontStyle.Bold);
        status.Dock=DockStyle.Right; status.AutoSize=true; status.Text="●  Приложение запущено"; status.TextAlign=ContentAlignment.MiddleRight;
        top.Controls.Add(status); top.Controls.Add(pageTitle); content.Controls.Add(top);
        var body=new Panel { Dock=DockStyle.Fill, Padding=new Padding(0,18,0,0), AutoScroll=false };
        content.Controls.Add(body); body.BringToFront(); top.BringToFront();
        pages["__body"]=body;
        BuildPages(body);
        body.Resize += (_,_) => ResizePages(body);
    }

    private void BuildPages(Panel body)
    {
        var overview=MakePage();
        var hero=Card("Компьютер под контролем","Быстрая информация о состоянии системы. Проверка обновляется автоматически.");
        var stats=new TableLayoutPanel { Dock=DockStyle.Top, Height=142, ColumnCount=3, RowCount=1, Padding=new Padding(0,12,0,10) };
        for(int i=0;i<3;i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.33f));
        stats.Controls.Add(Metric("ПРОЦЕССОР",ref cpuValue),0,0);
        stats.Controls.Add(Metric("ОПЕРАТИВНАЯ ПАМЯТЬ",ref ramValue),1,0);
        stats.Controls.Add(Metric("ДИСК WINDOWS",ref diskValue),2,0);
        var info=Card("Система",""); info.Controls.Add(stats);
        var scan=ActionButton("Проверить компьютер",()=>ShowPage("Проверка ПК"));
        overview.Controls.Add(hero); overview.Controls.Add(info); overview.Controls.Add(scan);
        pages["Обзор"]=overview;

        var check=MakePage();
        var scanCard=Card("Диагностика компьютера","Сводка обновляется по нажатию кнопки. Сбор данных выполняется локально.");
        scanSummary=new Label { AutoSize=true, MaximumSize=new Size(780,0), Font=new Font("Segoe UI",10), Padding=new Padding(4,8,4,8) };
        scanCard.Controls.Add(ActionButton("Запустить быструю проверку",()=>_ = RefreshStatsAsync()));
        scanCard.Controls.Add(scanSummary);
        drivesPanel=new FlowLayoutPanel { Dock=DockStyle.Top, AutoSize=true, FlowDirection=FlowDirection.TopDown, WrapContents=false };
        var drives=Card("Все диски","Свободное и занятое место на подключённых томах."); drives.Controls.Add(drivesPanel);
        check.Controls.Add(scanCard); check.Controls.Add(drives); pages["Проверка ПК"]=check;

        var tweaks=MakePage();
        var tcard=Card("Безопасные настройки Windows","Каждый твик изменяет только известный параметр текущего пользователя. Исходное значение сохраняется для отмены.");
        tweaksPanel=new FlowLayoutPanel { Dock=DockStyle.Top, AutoSize=true, FlowDirection=FlowDirection.TopDown, WrapContents=false };
        tcard.Controls.Add(tweaksPanel);
        foreach(var item in tweakItems) AddTweakCard(item);
        tweaks.Controls.Add(tcard); pages["Оптимизация"]=tweaks;

        var processes=MakePage();
        var pcard=Card("Запущенные процессы","Список процессов и используемая ими оперативная память. Для завершения используйте Диспетчер задач Windows.");
        processGrid=new DataGridView {
            Height=420, ReadOnly=true, AllowUserToAddRows=false, AllowUserToDeleteRows=false,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible=false,
            SelectionMode=DataGridViewSelectionMode.FullRowSelect, BackgroundColor=Color.FromArgb(22,34,27),
            BorderStyle=BorderStyle.None, MultiSelect=false, AllowUserToResizeRows=false,
            ColumnHeadersHeight=38
        };
        processGrid.RowTemplate.Height=32;
        processGrid.Columns.Add("name","ПРОЦЕСС");
        processGrid.Columns.Add("memory","ОПЕРАТИВНАЯ ПАМЯТЬ");
        processGrid.Columns["memory"]!.DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleRight;
        pcard.Controls.Add(ActionButton("Обновить список",()=>_ = RefreshProcessesAsync())); pcard.Controls.Add(processGrid);
        processes.Controls.Add(pcard); pages["Процессы"]=processes;

        var ai=MakePage();
        var aicard=Card("PC Booster AI","Задавай вопросы по Windows, производительности и настройкам компьютера.");
        chatLog=new RichTextBox { Height=330, ReadOnly=true, BorderStyle=BorderStyle.None, Font=new Font("Segoe UI",10), Text="PC Booster AI\nПривет! Напиши, с чем помочь.\n\n" };
        var row=new Panel { Height=52, Padding=new Padding(0,8,0,0) };
        chatInput=new TextBox { Dock=DockStyle.Fill, PlaceholderText="Напиши вопрос..." };
        var send=ActionButton("Отправить",SendChat); send.Dock=DockStyle.Right; send.Width=120;
        row.Controls.Add(chatInput); row.Controls.Add(send);
        aicard.Controls.Add(row); aicard.Controls.Add(chatLog); ai.Controls.Add(aicard); pages["AI-помощник"]=ai;

        var settings=MakePage();
        var updateCard=Card("Обновление приложения","Проверяй новые версии PC Booster прямо здесь. Если обновление найдено, программа проверит файл, скачает его и перезапустится.");
        updateCard.Padding=new Padding(22);
        var updateVersionLabel=new Label { AutoSize=true, Text="УСТАНОВЛЕННАЯ ВЕРСИЯ  ·  "+CurrentVersion, Font=new Font("Segoe UI",9,FontStyle.Bold), Padding=new Padding(4,6,4,4) };
        updateStatusLabel=new Label { AutoSize=true, MaximumSize=new Size(780,0), Padding=new Padding(4,10,4,8), Text="Нажми «Проверить обновления», чтобы проверить наличие новой версии." };
        var updateActions=new FlowLayoutPanel { AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, FlowDirection=FlowDirection.LeftToRight, WrapContents=true, Padding=new Padding(0,6,0,0), Margin=new Padding(0,4,0,4) };
        var checkUpdateButton=ActionButton("ПРОВЕРИТЬ ОБНОВЛЕНИЯ",()=>CheckForUpdates());
        checkUpdateButton.Height=42;
        installUpdateButton=ActionButton("СКАЧАТЬ И УСТАНОВИТЬ",InstallUpdate);
        installUpdateButton.Height=42;
        installUpdateButton.Enabled=false;
        updateActions.Controls.Add(checkUpdateButton);
        updateActions.Controls.Add(installUpdateButton);
        updateCard.Controls.Add(updateActions);
        updateCard.Controls.Add(updateStatusLabel);
        updateCard.Controls.Add(updateVersionLabel);
        settings.Controls.Add(updateCard);

        var setcard=Card("Внешний вид","Настрой оформление приложения под себя. Изменение применяется сразу, без перезапуска.");
        setcard.Padding=new Padding(22);
        var themeInfo=new Label { Dock=DockStyle.Top, Height=38, Text="Тема приложения", Padding=new Padding(4,10,4,4), Font=new Font("Segoe UI",10,FontStyle.Bold) };
        var theme=ActionButton("ПЕРЕКЛЮЧИТЬ СВЕТЛУЮ / ТЁМНУЮ ТЕМУ",()=>{light=!light;ApplyTheme();});
        theme.Height=42;
        setcard.Controls.Add(theme);
        setcard.Controls.Add(themeInfo);
        settings.Controls.Add(setcard);
        pages["Настройки"]=settings;

        foreach(var pair in pages.Where(p=>p.Key!="__body")) { var c=pair.Value; c.Dock=DockStyle.Fill; c.Visible=false; body.Controls.Add(c); }
    }

    private Panel MakePage()
    {
        var page=new SmoothFlowPanel { Dock=DockStyle.Fill, AutoScroll=true, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(0,0,12,16), Margin=Padding.Empty, Tag="page" };
        page.Resize += (_,_) => ResizePage(page);
        return page;
    }
    private void ResizePages(Panel body)
    {
        foreach(var pair in pages.Where(p=>p.Key!="__body")) if(pair.Value is FlowLayoutPanel page) ResizePage(page);
    }
    private void ResizePage(FlowLayoutPanel page)
    {
        int width=Math.Max(360,page.ClientSize.Width-34);
        foreach(Control child in page.Controls)
        {
            child.Width=width;
            child.MaximumSize=new Size(width,0);
            if(child is FlowLayoutPanel card && card.Tag?.ToString()=="card")
                foreach(Control inner in card.Controls) FitCardControl(card,inner);
        }
    }
    private void FitCardControl(FlowLayoutPanel card,Control? child)
    {
        if(child is null) return;
        int width=Math.Max(280,card.ClientSize.Width-card.Padding.Horizontal-8);
        if(child is Label label)
        {
            label.AutoSize=true;
            label.MaximumSize=new Size(width,0);
        }
        else if(child is FlowLayoutPanel flow)
        {
            flow.AutoSize=true;
            flow.MaximumSize=new Size(width,0);
            flow.Width=width;
            foreach(Control nested in flow.Controls)
            {
                if(nested is FlowLayoutPanel nestedCard && nestedCard.Tag?.ToString()=="card")
                {
                    nestedCard.MaximumSize=new Size(width,0);
                    nestedCard.Width=width;
                    foreach(Control inner in nestedCard.Controls) FitCardControl(nestedCard,inner);
                }
            }
        }
        else
        {
            child.Width=width;
            if(child is Panel panel) panel.MaximumSize=new Size(width,0);
        }
    }
    private Panel Card(string title,string subtitle)
    {
        var p=new CardFlowPanel { FlowDirection=FlowDirection.TopDown, WrapContents=false, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, Width=860, Padding=new Padding(22), Margin=new Padding(0,0,0,18), BackColor=Color.FromArgb(20,31,24), Tag="card" };
        p.ControlAdded += (_,e) => FitCardControl(p,e.Control);
        p.SizeChanged += (_,_) => { foreach(Control child in p.Controls) FitCardControl(p,child); };
        var h=new Label { Text=title, AutoSize=true, Font=new Font("Segoe UI",14,FontStyle.Bold), Margin=new Padding(0,0,0,5) };
        p.Controls.Add(h);
        if(!string.IsNullOrWhiteSpace(subtitle)) p.Controls.Add(new Label { Text=subtitle, AutoSize=true, Font=new Font("Segoe UI",9.5f), ForeColor=Color.FromArgb(155,177,161), Margin=new Padding(0,0,0,12) });
        return p;
    }
    private Control Metric(string caption,ref Label value)
    {
        var p=new Panel { Dock=DockStyle.Fill, Padding=new Padding(12), Margin=new Padding(0,0,10,0), BackColor=Color.FromArgb(28,44,34), Tag="metric" };
        p.Controls.Add(value); value.Dock=DockStyle.Bottom; value.Height=42; value.Font=new Font("Segoe UI",19,FontStyle.Bold);
        p.Controls.Add(new Label { Text=caption, Dock=DockStyle.Top, Height=26, Font=new Font("Segoe UI",8,FontStyle.Bold) });
        return p;
    }
    private Button ActionButton(string text,Action action)
    {
        var b=new Button { Text=text, Height=42, MinimumSize=new Size(150,42), AutoSize=true, Padding=new Padding(16,5,16,5), FlatStyle=FlatStyle.Flat, Font=new Font("Segoe UI",9.5f,FontStyle.Bold), Margin=new Padding(0,8,10,8), Cursor=Cursors.Hand };
        b.Click+=(_,_)=>action(); return b;
    }
    private void AddTweakCard(Tweak item)
    {
        var p=new CardFlowPanel { Width=820, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(16), Margin=new Padding(0,0,0,12), BackColor=Color.FromArgb(25,42,31), Tag="card" };
        var name=new Label { Text=item.Name+"  ·  "+item.Category, AutoSize=true, Font=new Font("Segoe UI",10,FontStyle.Bold), Margin=new Padding(0,0,0,5) };
        var desc=new Label { Text=item.Description, AutoSize=true, Margin=new Padding(0,0,0,8) };
        var state=new Label { Text=rollback.ContainsKey(item.Id)?"Применено":"Не применено", AutoSize=true, TextAlign=ContentAlignment.MiddleLeft, Name="state", Padding=new Padding(4,10,8,0) };
        var undo=ActionButton("Отменить",()=>RollbackTweak(item,p));
        var apply=ActionButton("Применить",()=>ApplyTweak(item,p));
        var actions=new FlowLayoutPanel { AutoSize=true, FlowDirection=FlowDirection.LeftToRight, WrapContents=true, Margin=Padding.Empty };
        actions.Controls.Add(apply); actions.Controls.Add(undo); actions.Controls.Add(state);
        p.Controls.Add(name); p.Controls.Add(desc); p.Controls.Add(actions);
        p.ControlAdded += (_,e) => FitCardControl(p,e.Control);
        p.SizeChanged += (_,_) => { foreach(Control child in p.Controls) FitCardControl(p,child); };
        tweaksPanel.Controls.Add(p);
        UpdateTweakCard(item,p);
    }
    private void UpdateTweakCard(Tweak item,Panel card)
    {
        var state=card.Controls.Find("state",true).FirstOrDefault() as Label;
        if(state!=null) state.Text=rollback.ContainsKey(item.Id)?"● Применено":"○ Не применено";
        foreach(Control c in card.Controls) if(c is FlowLayoutPanel f) foreach(Control b in f.Controls) if(b is Button btn) btn.Enabled=btn.Text=="Применить"?!rollback.ContainsKey(item.Id):rollback.ContainsKey(item.Id);
    }
    private void LoadRollback()
    {
        try { if(File.Exists(rollbackPath)){var d=JsonSerializer.Deserialize<Dictionary<string,List<RegValue>>>(File.ReadAllText(rollbackPath)); if(d!=null) foreach(var kv in d) rollback[kv.Key]=kv.Value;} } catch {}
    }
    private void SaveRollback()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(rollbackPath)!);
        File.WriteAllText(rollbackPath,JsonSerializer.Serialize(rollback));
    }
    private void ApplyTweak(Tweak t,Panel card)
    {
        try
        {
            using var key=Registry.CurrentUser.CreateSubKey(t.Key,true) ?? throw new Exception("Не удалось открыть параметр Windows.");
            if(!rollback.ContainsKey(t.Id))
            {
                var old=key.GetValue(t.ValueName,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                rollback[t.Id]=new(){new RegValue(t.Key,t.ValueName,old!=null,old?.ToString(),(int)t.Kind)};
            }
            key.SetValue(t.ValueName,t.Value,t.Kind); SaveRollback(); UpdateTweakCard(t,card);
            MessageBox.Show("Настройка применена. Исходное значение сохранено — его можно восстановить кнопкой «Отменить».","PC Booster",MessageBoxButtons.OK,MessageBoxIcon.Information);
        } catch(Exception ex){MessageBox.Show(ex.Message,"Не удалось применить настройку",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    private void RollbackTweak(Tweak t,Panel card)
    {
        try
        {
            if(!rollback.TryGetValue(t.Id,out var values)) return;
            foreach(var v in values)
            {
                using var key=Registry.CurrentUser.CreateSubKey(v.Key,true) ?? throw new Exception("Не удалось открыть параметр для восстановления.");
                if(v.Existed) key.SetValue(v.Name,ConvertValue(v.Value!, (RegistryValueKind)v.Kind), (RegistryValueKind)v.Kind);
                else key.DeleteValue(v.Name,false);
            }
            rollback.Remove(t.Id); SaveRollback(); UpdateTweakCard(t,card);
            MessageBox.Show("Исходное значение восстановлено.","PC Booster",MessageBoxButtons.OK,MessageBoxIcon.Information);
        } catch(Exception ex){MessageBox.Show(ex.Message,"Не удалось отменить настройку",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    private static object ConvertValue(string value,RegistryValueKind kind)=>kind==RegistryValueKind.DWord?int.Parse(value):value;

    private void ShowPage(string name)
    {
        if(!pages.ContainsKey(name)) return;
        currentPage=name;
        pageTitle.Text=name;
        foreach(var pair in pages.Where(p=>p.Key!="__body"))
            pair.Value.Visible=pair.Key==name;
        UpdateNavigationVisuals();

        if(name=="Процессы") _ = RefreshProcessesAsync();
        if(name=="Проверка ПК") _ = RefreshStatsAsync();
    }

    private void UpdateNavigationVisuals()
    {
        foreach(var button in navigationButtons)
        {
            bool active=string.Equals(button.Tag as string,currentPage,StringComparison.Ordinal);
            button.BackColor=active?Accent():PanelColor();
            button.ForeColor=active?(light?Color.White:Color.FromArgb(8,29,16)):TextColor();
            button.FlatAppearance.MouseOverBackColor=light?Color.FromArgb(222,242,228):Color.FromArgb(35,61,43);
        }
    }

    private async Task RefreshStatsAsync()
    {
        if(statsBusy || IsDisposed || Disposing) return;
        statsBusy=true;
        bool includeDrives=currentPage=="Проверка ПК";
        try
        {
            var snapshot=await Task.Run(()=>ReadSystemSnapshot(includeDrives));
            if(IsDisposed || Disposing) return;

            cpuValue.Text=$"{snapshot.Cpu:0}%";
            ramValue.Text=$"{snapshot.Ram:0}%";
            diskValue.Text=$"{snapshot.Disk:0}%";
            osValue.Text=Environment.OSVersion.VersionString;
            status.Text="●  Система обновлена";

            if(includeDrives && currentPage=="Проверка ПК")
            {
                scanSummary.Text=snapshot.Details;
                UpdateDriveList(snapshot.Drives);
            }
        }
        catch(Exception ex)
        {
            if(!IsDisposed && !Disposing)
            {
                status.Text="●  Не удалось обновить данные";
                if(currentPage=="Проверка ПК") scanSummary.Text=ex.Message;
            }
        }
        finally { statsBusy=false; }
    }

    private SystemSnapshot ReadSystemSnapshot(bool includeDrives)
    {
        var sample=ReadCpuTimes();
        double cpu=0;
        if(previousCpuSample is { } previous)
        {
            ulong total=(sample.kernel-previous.kernel)+(sample.user-previous.user);
            ulong idle=sample.idle-previous.idle;
            cpu=total==0?0:Math.Clamp(100.0*(total-idle)/total,0,100);
        }
        previousCpuSample=sample;

        var mem=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};
        if(!GlobalMemoryStatusEx(ref mem)) throw new InvalidOperationException("Не удалось получить данные оперативной памяти.");
        var systemDrive=new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        double disk=100.0*(systemDrive.TotalSize-systemDrive.AvailableFreeSpace)/Math.Max(1,systemDrive.TotalSize);
        double ram=mem.TotalPhys==0?0:100.0*(mem.TotalPhys-mem.AvailPhys)/mem.TotalPhys;

        var drives=includeDrives
            ? DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed).Select(d=>
              {
                  try { return d.IsReady ? new DriveSnapshot(d.Name,d.TotalSize,d.AvailableFreeSpace) : null; }
                  catch { return null; }
              }).Where(d=>d!=null).Cast<DriveSnapshot>().ToArray()
            : Array.Empty<DriveSnapshot>();

        string details=$"Устройство: {Environment.MachineName}\\nWindows: {Environment.OSVersion.Version}   ·   Потоки CPU: {Environment.ProcessorCount}\\nПроверка завершена: {DateTime.Now:T}";
        return new SystemSnapshot(cpu,ram,disk,details,drives);
    }

    private void UpdateDriveList(IReadOnlyList<DriveSnapshot> drives)
    {
        var lines=drives.Select(d=>$"{d.Name}   {FormatBytes(d.Free)} свободно из {FormatBytes(d.Total)}").ToArray();
        if(drivesPanel.Controls.Count==lines.Length &&
           drivesPanel.Controls.Cast<Control>().Select(c=>c.Text).SequenceEqual(lines)) return;

        drivesPanel.SuspendLayout();
        try
        {
            drivesPanel.Controls.Clear();
            foreach(var line in lines)
                drivesPanel.Controls.Add(new Label{Width=760,Height=34,Text=line,Padding=new Padding(8,7,4,4),Margin=new Padding(0,0,0,5)});
        }
        finally { drivesPanel.ResumeLayout(true); }
    }

    private async Task RefreshProcessesAsync()
    {
        if(processesBusy || IsDisposed || Disposing) return;
        processesBusy=true;
        try
        {
            status.Text="●  Собираю список процессов...";
            var rows=await Task.Run(ReadProcessSnapshot);
            if(IsDisposed || Disposing || currentPage!="Процессы") return;

            processGrid.SuspendLayout();
            try
            {
                processGrid.Rows.Clear();
                var gridRows=rows.Select(item=>
                {
                    var row=new DataGridViewRow();
                    row.CreateCells(processGrid,item.Name,FormatBytes(item.Memory));
                    return row;
                }).ToArray();
                if(gridRows.Length>0) processGrid.Rows.AddRange(gridRows);
            }
            finally { processGrid.ResumeLayout(true); }
            status.Text=$"●  Найдено процессов: {rows.Count}";
        }
        catch(Exception ex)
        {
            if(!IsDisposed && !Disposing) status.Text="●  Не удалось загрузить процессы: "+ex.Message;
        }
        finally { processesBusy=false; }
    }

    private static List<ProcessSnapshot> ReadProcessSnapshot()
    {
        var result=new List<ProcessSnapshot>();
        foreach(var process in Process.GetProcesses())
        {
            try { result.Add(new ProcessSnapshot(process.ProcessName,process.WorkingSet64)); }
            catch { }
            finally { process.Dispose(); }
        }
        return result.OrderByDescending(p=>p.Memory).Take(100).ToList();
    }

    private async void SendChat()
    {
        var message=chatInput.Text.Trim(); if(message.Length==0)return;
        chatInput.Clear(); chatLog.AppendText($"\nВы: {message}\nPC Booster AI: Думаю...\n");
        chatHistory.Add(("user",message));
        try
        {
            var payload=new {message,history=chatHistory.TakeLast(12).Select(x=>new{role=x.role,content=x.content}).ToArray()};
            using var response=await http.PostAsJsonAsync("https://pc-booster-ai-api.onrender.com/api/assistant",payload);
            var json=await response.Content.ReadFromJsonAsync<AiReply>();
            if(!response.IsSuccessStatusCode) throw new Exception(json?.error??"AI временно недоступен.");
            var answer=json?.answer??"Пустой ответ от AI.";
            chatHistory.Add(("assistant",answer)); chatLog.AppendText(answer+"\n");
        } catch(Exception ex){chatLog.AppendText("Ошибка: "+ex.Message+"\n");}
    }

    private static string CurrentVersion => typeof(MainForm).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    private async void CheckForUpdates()
    {
        if (updateBusy) return;
        updateBusy = true;
        installUpdateButton.Enabled = false;
        updateStatusLabel.Text = "Проверяю наличие новой версии...";
        UpdateManifest? promptUpdate = null;
        try
        {
            var manifest = await http.GetFromJsonAsync<UpdateManifest>("https://pc-booster-ai-api.onrender.com/api/updates/latest");
            if (manifest == null || !Version.TryParse(manifest.version, out var latest) || !Version.TryParse(CurrentVersion, out var current))
                throw new Exception("Сервер вернул некорректную информацию о версии.");
            if (latest <= current)
            {
                availableUpdate = null;
                updateStatusLabel.Text = "Установлена последняя версия — " + CurrentVersion + ".";
                status.Text = "●  PC Booster обновлён";
            }
            else
            {
                availableUpdate = manifest;
                var canDelta = manifest.deltaFromVersion == CurrentVersion && manifest.deltaSize > 0 &&
                    manifest.deltaSha256.Length == 64 && File.Exists(Path.Combine(AppContext.BaseDirectory, "xdelta3.exe"));
                var downloadSize = canDelta ? manifest.deltaSize : manifest.size;
                updateStatusLabel.Text = "Доступна версия " + manifest.version + ". Размер загрузки: " + FormatBytes(downloadSize) +
                    (canDelta ? " (обновление только изменений)." : " (полный файл).");
                status.Text = "●  Доступно обновление " + manifest.version;
                installUpdateButton.Text = pendingUpdatePath != null && File.Exists(pendingUpdatePath)
                    ? "ОБНОВИТЬ СЕЙЧАС" : "СКАЧАТЬ И УСТАНОВИТЬ";
                installUpdateButton.Enabled = true;
                promptUpdate = manifest;
            }
        }
        catch (Exception ex)
        {
            updateStatusLabel.Text = "Не удалось проверить обновления: " + ex.Message;
            status.Text = "●  Не удалось проверить обновления";
        }
        finally { updateBusy = false; }

        if (promptUpdate != null && !IsDisposed && !Disposing)
        {
            var choice = MessageBox.Show(this,
                "Доступна новая версия PC Booster — " + promptUpdate.version +
                ". Установить её сейчас?",
                "Обновление PC Booster", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (choice == DialogResult.Yes) InstallUpdate();
        }
    }

    private async void InstallUpdate()
    {
        if (updateBusy || availableUpdate == null) return;

        var manifest = availableUpdate;
        if (manifest.size <= 0 || manifest.size > 200L * 1024 * 1024 ||
            manifest.sha256.Length != 64)
        {
            updateStatusLabel.Text = "Данные обновления не прошли проверку безопасности.";
            return;
        }

        updateBusy = true;
        installUpdateButton.Enabled = false;
        string? temporaryDownloadPath = null;
        string? patchPath = null;
        bool retainDownload = false;

        try
        {
            var targetPath = Application.ExecutablePath;
            var readyPath = pendingUpdatePath;
            if (string.IsNullOrWhiteSpace(readyPath) || !File.Exists(readyPath))
            {
                pendingUpdatePath = null;
                readyPath = null;
            }

            long transferSize = pendingUpdateTransferSize;
            if (readyPath == null)
            {
                var newExePath = Path.Combine(Path.GetTempPath(),
                    "PC-Booster-new-" + Guid.NewGuid().ToString("N") + ".exe");
                temporaryDownloadPath = newExePath;

                using var cancelSource = new CancellationTokenSource();
                using var progress = new UpdateProgressForm(manifest.version);
                progress.CancelRequested += () => cancelSource.Cancel();

                Enabled = false;
                progress.Show(this);
                progress.Activate();

                try
                {
                    var canDelta = manifest.deltaFromVersion == CurrentVersion &&
                        manifest.deltaSize > 0 && manifest.deltaSize < manifest.size &&
                        manifest.deltaSha256.Length == 64 &&
                        File.Exists(Path.Combine(AppContext.BaseDirectory, "xdelta3.exe"));

                    transferSize = 0;
                    var ready = false;
                    if (canDelta)
                    {
                        try
                        {
                            patchPath = Path.Combine(Path.GetTempPath(),
                                "PC-Booster-patch-" + Guid.NewGuid().ToString("N") + ".xdelta");
                            progress.SetStage("Скачиваю небольшое обновление...");
                            await DownloadUpdateFileAsync(
                                "https://pc-booster-ai-api.onrender.com/api/updates/download?from=" +
                                Uri.EscapeDataString(CurrentVersion),
                                patchPath, manifest.deltaSize, progress, cancelSource.Token);
                            transferSize += manifest.deltaSize;

                            var patchHash = await ComputeFileSha256Async(patchPath, cancelSource.Token);
                            if (!string.Equals(patchHash, manifest.deltaSha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Контрольная сумма патча не совпала.");

                            progress.SetStage("Применяю изменения и проверяю файл...");
                            var patcher = Path.Combine(AppContext.BaseDirectory, "xdelta3.exe");
                            var psi = new ProcessStartInfo
                            {
                                FileName = patcher,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            psi.ArgumentList.Add("-d");
                            psi.ArgumentList.Add("-s");
                            psi.ArgumentList.Add(targetPath);
                            psi.ArgumentList.Add(patchPath);
                            psi.ArgumentList.Add(newExePath);

                            using var patchProcess = Process.Start(psi)
                                ?? throw new Exception("Не удалось запустить модуль обновления.");
                            try
                            {
                                await patchProcess.WaitForExitAsync(cancelSource.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                try { if (!patchProcess.HasExited) patchProcess.Kill(entireProcessTree: true); } catch { }
                                throw;
                            }

                            if (patchProcess.ExitCode != 0 || !File.Exists(newExePath))
                                throw new InvalidDataException("Не удалось применить компактное обновление.");

                            var assembledHash = await ComputeFileSha256Async(newExePath, cancelSource.Token);
                            if (!string.Equals(assembledHash, manifest.sha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Проверка собранного файла не пройдена.");

                            ready = true;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
                            try { if (patchPath != null && File.Exists(patchPath)) File.Delete(patchPath); } catch { }
                            patchPath = null;
                            progress.SetStage("Компактный патч не подошёл. Перехожу на полную загрузку...");
                        }
                    }

                    if (!ready)
                    {
                        progress.SetStage("Скачиваю полную версию приложения...");
                        await DownloadUpdateFileAsync(
                            "https://pc-booster-ai-api.onrender.com/api/updates/download",
                            newExePath, manifest.size, progress, cancelSource.Token);
                        transferSize += manifest.size;

                        var fullHash = await ComputeFileSha256Async(newExePath, cancelSource.Token);
                        if (!string.Equals(fullHash, manifest.sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Контрольная сумма обновления не совпала.");
                    }

                    readyPath = newExePath;
                    pendingUpdatePath = newExePath;
                    pendingUpdateTransferSize = transferSize;
                    retainDownload = true;
                    progress.SetCompleted();
                }
                finally
                {
                    if (!progress.IsDisposed) progress.Close();
                    if (!IsDisposed && !Disposing)
                    {
                        Enabled = true;
                        Activate();
                    }
                }
            }
            else
            {
                readyPath = pendingUpdatePath;
                transferSize = pendingUpdateTransferSize;
                retainDownload = true;
            }

            var choice = MessageBox.Show(
                this,
                "Обновление PC Booster " + manifest.version + " скачано и проверено.\n\n" +
                "Всего скачано: " + FormatBytes(transferSize) + "\n\n" +
                "Обновить приложение и перезапустить его сейчас? Текущая программа закроется только после твоего подтверждения.",
                "PC Booster — обновление готово",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (choice != DialogResult.Yes)
            {
                updateStatusLabel.Text = "Обновление скачано и проверено. Нажми «ОБНОВИТЬ СЕЙЧАС», когда будешь готов.";
                installUpdateButton.Text = "ОБНОВИТЬ СЕЙЧАС";
                return;
            }

            updateStatusLabel.Text = "Запускаю установку обновления...";
            var scriptPath = Path.Combine(Path.GetTempPath(),
                "PC-Booster-updater-" + Guid.NewGuid().ToString("N") + ".ps1");
            string Ps(string value) => "'" + value.Replace("'", "''") + "'";
            var script = "$ErrorActionPreference = 'Stop'\n" +
                "$target = " + Ps(targetPath) + "\n" +
                "$source = " + Ps(readyPath!) + "\n" +
                "$processId = " + Environment.ProcessId + "\n" +
                "try {\n" +
                "  while (Get-Process -Id $processId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 400 }\n" +
                "  Copy-Item -LiteralPath $source -Destination $target -Force\n" +
                "  Start-Process -FilePath $target\n" +
                "  Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue\n" +
                "  Start-Sleep -Seconds 2\n" +
                "  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue\n" +
                "} catch { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'PC Booster — ошибка обновления') | Out-Null }";

            await File.WriteAllTextAsync(scriptPath, script, new System.Text.UTF8Encoding(false));
            var updater = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
                UseShellExecute = true,
                Verb = "runas"
            });
            if (updater == null) throw new Exception("Не удалось запустить установщик обновления.");

            pendingUpdatePath = null;
            temporaryDownloadPath = null;
            retainDownload = false;
            await Task.Delay(300);
            Application.Exit();
        }
        catch (OperationCanceledException)
        {
            updateStatusLabel.Text = "Загрузка обновления отменена.";
            if (pendingUpdatePath == temporaryDownloadPath) pendingUpdatePath = null;
        }
        catch (Exception ex)
        {
            updateStatusLabel.Text = "Не удалось подготовить обновление: " + ex.Message;
            installUpdateButton.Text = pendingUpdatePath != null && File.Exists(pendingUpdatePath)
                ? "ОБНОВИТЬ СЕЙЧАС" : "СКАЧАТЬ И УСТАНОВИТЬ";
        }
        finally
        {
            if (patchPath != null) try { File.Delete(patchPath); } catch { }
            if (!retainDownload && temporaryDownloadPath != null)
                try { if (File.Exists(temporaryDownloadPath)) File.Delete(temporaryDownloadPath); } catch { }

            updateBusy = false;
            if (!IsDisposed && !Disposing)
            {
                installUpdateButton.Enabled = availableUpdate != null;
            }
        }
    }

    private async Task DownloadUpdateFileAsync(
        string url,
        string destinationPath,
        long expectedSize,
        UpdateProgressForm progress,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value != expectedSize)
            throw new InvalidDataException("Размер загрузки с сервера не совпадает с манифестом.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[64 * 1024];
        long received = 0;
        var clock = Stopwatch.StartNew();
        progress.ReportProgress(0, expectedSize);

        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (count == 0) break;

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            received += count;

            if (clock.ElapsedMilliseconds >= 90)
            {
                progress.ReportProgress(received, expectedSize);
                clock.Restart();
            }
        }

        await output.FlushAsync(cancellationToken);
        if (received != expectedSize)
            throw new InvalidDataException("Загрузка завершилась не полностью: получено " +
                FormatBytes(received) + " из " + FormatBytes(expectedSize) + ".");

        progress.ReportProgress(received, expectedSize);
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class UpdateManifest
    {
        public string version { get; set; } = "";
        public string tag { get; set; } = "";
        public string sha256 { get; set; } = "";
        public long size { get; set; }
        public DateTime? publishedAt { get; set; }
        public string deltaFromVersion { get; set; } = "";
        public string deltaFile { get; set; } = "";
        public long deltaSize { get; set; }
        public string deltaSha256 { get; set; } = "";
    }

    private sealed class UpdateProgressForm : Form
    {
        private readonly Label heading = new();
        private readonly Label stage = new();
        private readonly Label amount = new();
        private readonly Label percent = new();
        private readonly ProgressBar progressBar = new();
        private readonly Button cancelButton = new();

        public event Action? CancelRequested;

        public UpdateProgressForm(string version)
        {
            Text = "Обновление PC Booster";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 236);
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(13, 23, 17);
            ForeColor = Color.FromArgb(237, 246, 239);
            Font = new Font("Segoe UI", 9.5f);

            var accent = new Panel
            {
                Dock = DockStyle.Top,
                Height = 5,
                BackColor = Color.FromArgb(105, 220, 145)
            };
            heading.SetBounds(28, 24, 460, 30);
            heading.Text = "Загружаю PC Booster " + version;
            heading.Font = new Font("Segoe UI", 15, FontStyle.Bold);
            heading.ForeColor = Color.FromArgb(237, 246, 239);

            stage.SetBounds(30, 67, 458, 24);
            stage.Text = "Подключаюсь к серверу обновлений...";
            stage.ForeColor = Color.FromArgb(180, 199, 185);

            progressBar.SetBounds(30, 106, 458, 16);
            progressBar.Minimum = 0;
            progressBar.Maximum = 1000;
            progressBar.Style = ProgressBarStyle.Continuous;

            amount.SetBounds(30, 136, 350, 24);
            amount.Text = "Подготовка загрузки...";
            amount.ForeColor = Color.FromArgb(180, 199, 185);

            percent.SetBounds(400, 136, 88, 24);
            percent.TextAlign = ContentAlignment.MiddleRight;
            percent.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            percent.ForeColor = Color.FromArgb(105, 233, 155);

            cancelButton.SetBounds(358, 178, 130, 38);
            cancelButton.Text = "Отменить";
            cancelButton.FlatStyle = FlatStyle.Flat;
            cancelButton.FlatAppearance.BorderColor = Color.FromArgb(64, 91, 71);
            cancelButton.BackColor = Color.FromArgb(26, 43, 32);
            cancelButton.ForeColor = Color.FromArgb(237, 246, 239);
            cancelButton.Cursor = Cursors.Hand;
            cancelButton.Click += (_, _) =>
            {
                cancelButton.Enabled = false;
                cancelButton.Text = "Отмена...";
                stage.Text = "Отменяю загрузку...";
                CancelRequested?.Invoke();
            };

            Controls.Add(accent);
            Controls.Add(heading);
            Controls.Add(stage);
            Controls.Add(progressBar);
            Controls.Add(amount);
            Controls.Add(percent);
            Controls.Add(cancelButton);
        }

        public void SetStage(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            stage.Text = text;
            percent.Text = "";
            amount.Text = "";
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.MarqueeAnimationSpeed = 24;
        }

        public void ReportProgress(long received, long total)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ReportProgress(received, total)));
                return;
            }

            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.MarqueeAnimationSpeed = 0;
            var ratio = total <= 0 ? 0 : Math.Clamp(received / (double)total, 0, 1);
            progressBar.Value = (int)Math.Round(ratio * progressBar.Maximum);
            var remaining = Math.Max(0, total - received);
            amount.Text = FormatBytes(received) + " из " + FormatBytes(total) +
                " · осталось " + FormatBytes(remaining);
            percent.Text = (ratio * 100).ToString("0") + "%";
        }

        public void SetCompleted()
        {
            if (IsDisposed || !IsHandleCreated) return;
            stage.Text = "Загрузка завершена. Файл проверен.";
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = progressBar.Maximum;
            amount.Text = "Все необходимые файлы готовы к обновлению.";
            percent.Text = "100%";
            cancelButton.Text = "Готово";
            cancelButton.Enabled = false;
        }
    }
    private void ApplyTheme()
    {
        var bg=light?Color.FromArgb(241,247,243):Color.FromArgb(10,17,13);
        var panel=light?Color.White:Color.FromArgb(20,32,25);
        var text=light?Color.FromArgb(25,43,31):Color.FromArgb(237,246,239);
        var muted=light?Color.FromArgb(92,112,98):Color.FromArgb(155,177,161);
        BackColor=bg; sidebar.BackColor=panel; content.BackColor=bg;
        foreach(Control c in Controls) PaintTree(c,bg,panel,text,muted);
        UpdateNavigationVisuals();
        foreach(var pair in pages.Where(p=>p.Key!="__body")) if(pair.Value is FlowLayoutPanel page) ResizePage(page);
    }
    private void PaintTree(Control c,Color bg,Color panel,Color text,Color muted)
    {
        c.ForeColor=text;
        if(c is Panel p)
        {
            p.BackColor=p.Tag?.ToString()=="card" || p.Tag?.ToString()=="metric" ? (light?Color.White:Color.FromArgb(22,36,27)) : p.Parent==sidebar?panel:bg;
        }
        if(c is Button b)
        {
            bool navigation=b.Parent==sidebar.Controls.Cast<Control>().FirstOrDefault(x=>x is FlowLayoutPanel);
            b.BackColor=navigation?PanelColor():Accent();
            b.ForeColor=navigation?TextColor():(light?Color.White:Color.FromArgb(7,29,15));
            b.FlatAppearance.BorderSize=0;
        }
        if(c is TextBox or RichTextBox){c.BackColor=panel;c.ForeColor=text;}
        if(c is Label label && label.Parent?.Tag?.ToString()=="card") label.ForeColor=text;
        if(c is DataGridView g){g.BackgroundColor=panel;g.DefaultCellStyle.BackColor=panel;g.DefaultCellStyle.ForeColor=text;g.ColumnHeadersDefaultCellStyle.BackColor=Accent();g.ColumnHeadersDefaultCellStyle.ForeColor=Color.Black;g.EnableHeadersVisualStyles=false;}
        foreach(Control child in c.Controls) PaintTree(child,bg,panel,text,muted);
    }
    private Color Accent()=>light?Color.FromArgb(26,151,79):Color.FromArgb(105,233,155);
    private Color PanelColor()=>light?Color.White:Color.FromArgb(20,31,24);
    private Color TextColor()=>light?Color.FromArgb(25,43,31):Color.FromArgb(237,246,239);
    private static string FormatBytes(long n){string[] u={"Б","КБ","МБ","ГБ","ТБ"};double v=n;int i=0;while(v>=1024&&i<u.Length-1){v/=1024;i++;}return $"{v:0.0} {u[i]}";}
    private static (ulong idle,ulong kernel,ulong user) ReadCpuTimes(){GetSystemTimes(out var i,out var k,out var u);return(ToUlong(i),ToUlong(k),ToUlong(u));}
    private static ulong ToUlong(FileTime t)=>((ulong)t.High<<32)|t.Low;
    [StructLayout(LayoutKind.Sequential)] private struct FileTime{public uint Low;public uint High;}
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus{public uint Length,Load;public ulong TotalPhys,AvailPhys,TotalPage,AvailPage,TotalVirtual,AvailVirtual,AvailExtended;}
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FileTime idle,out FileTime kernel,out FileTime user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    private sealed record SystemSnapshot(double Cpu,double Ram,double Disk,string Details,DriveSnapshot[] Drives);
    private sealed record DriveSnapshot(string Name,long Total,long Free);
    private sealed record ProcessSnapshot(string Name,long Memory);

    private sealed class SmoothFlowPanel : FlowLayoutPanel
    {
        public SmoothFlowPanel()
        {
            DoubleBuffered=true;
            SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
    }

    private sealed class CardFlowPanel : FlowLayoutPanel
    {
        public CardFlowPanel()
        {
            DoubleBuffered=true;
            SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if(Width<24 || Height<24) return;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using var path=new GraphicsPath();
            int r=10, d=r*2, w=Width-2, h=Height-2;
            path.AddArc(1,1,d,d,180,90);
            path.AddArc(w-d,1,d,d,270,90);
            path.AddArc(w-d,h-d,d,d,0,90);
            path.AddArc(1,h-d,d,d,90,90);
            path.CloseFigure();
            using var pen=new Pen(Color.FromArgb(48,105,233,155),1);
            e.Graphics.DrawPath(pen,path);
        }
    }

    private sealed record Tweak(string Id,string Name,string Category,string Description,string Key,string ValueName,RegistryValueKind Kind,object Value);
    private sealed record RegValue(string Key,string Name,bool Existed,string? Value,int Kind);
    private sealed class AiReply{public string? answer{get;set;} public string? error{get;set;}}
}