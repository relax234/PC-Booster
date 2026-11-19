using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
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
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 2500 };
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
        Text = "PC Booster";
        MinimumSize = new Size(980,680);
        Size = new Size(1180,790);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI",9.5f);
        LoadRollback();
        BuildShell();
        ShowPage("Обзор");
        RefreshStats();
        refreshTimer.Tick += (_,_) => RefreshStats();
        refreshTimer.Start();
        FormClosed += (_,_) => { refreshTimer.Stop(); http.Dispose(); };
        ApplyTheme();
    }

    private void BuildShell()
    {
        sidebar.Dock=DockStyle.Left; sidebar.Width=224; sidebar.Padding=new Padding(16,20,16,16);
        content.Dock=DockStyle.Fill; content.Padding=new Padding(28,22,28,24);
        Controls.Add(content); Controls.Add(sidebar);
        var brand=new Label { Text="✦  PC BOOSTER", AutoSize=false, Height=48, Dock=DockStyle.Top, Font=new Font("Segoe UI",16,FontStyle.Bold), TextAlign=ContentAlignment.MiddleLeft };
        sidebar.Controls.Add(brand);
        var nav=new FlowLayoutPanel { Dock=DockStyle.Top, Height=390, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(0,20,0,0) };
        sidebar.Controls.Add(nav);
        foreach(var name in new[]{"Обзор","Проверка ПК","Оптимизация","Процессы","AI-помощник","Настройки"})
        {
            var b=new Button { Text="   "+name, Width=190, Height=44, TextAlign=ContentAlignment.MiddleLeft, FlatStyle=FlatStyle.Flat, Tag=name, Margin=new Padding(0,0,0,7), Font=new Font("Segoe UI",10,FontStyle.Bold), Cursor=Cursors.Hand };
            b.FlatAppearance.BorderSize=0; b.Click+=(_,_)=>ShowPage(name); nav.Controls.Add(b);
        }
        var bottom=new Label { Text="WINDOWS 10 / 11\nЛокальное приложение", Dock=DockStyle.Bottom, Height=48, Font=new Font("Segoe UI",8), TextAlign=ContentAlignment.BottomLeft };
        sidebar.Controls.Add(bottom);
        var top=new Panel { Dock=DockStyle.Top, Height=56 };
        pageTitle.Dock=DockStyle.Left; pageTitle.AutoSize=true; pageTitle.Font=new Font("Segoe UI",21,FontStyle.Bold);
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
        var scan=ActionButton("Проверить компьютер",()=>{RefreshStats();ShowPage("Проверка ПК");});
        overview.Controls.Add(hero); overview.Controls.Add(info); overview.Controls.Add(scan);
        pages["Обзор"]=overview;

        var check=MakePage();
        var scanCard=Card("Диагностика компьютера","Сводка обновляется по нажатию кнопки. Сбор данных выполняется локально.");
        scanSummary=new Label { AutoSize=true, MaximumSize=new Size(780,0), Font=new Font("Segoe UI",10), Padding=new Padding(4,8,4,8) };
        scanCard.Controls.Add(ActionButton("Запустить быструю проверку",RefreshStats));
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
        processGrid=new DataGridView { Height=380, ReadOnly=true, AllowUserToAddRows=false, AllowUserToDeleteRows=false, AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible=false, SelectionMode=DataGridViewSelectionMode.FullRowSelect, BackgroundColor=Color.FromArgb(22,34,27), BorderStyle=BorderStyle.None };
        processGrid.Columns.Add("name","Процесс"); processGrid.Columns.Add("memory","Память");
        pcard.Controls.Add(ActionButton("Обновить список",RefreshProcesses)); pcard.Controls.Add(processGrid);
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
        var page=new FlowLayoutPanel { Dock=DockStyle.Fill, AutoScroll=true, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(0,0,12,16), Margin=Padding.Empty, Tag="page" };
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
            if(child is FlowLayoutPanel card && card.Tag?.ToString()=="card")
                foreach(Control inner in card.Controls) FitCardControl(card,inner);
        }
    }
    private void FitCardControl(FlowLayoutPanel card,Control child)
    {
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
        }
        else
        {
            child.Width=width;
            if(child is Panel panel) panel.MaximumSize=new Size(width,0);
        }
    }
    private Panel Card(string title,string subtitle)
    {
        var p=new FlowLayoutPanel { FlowDirection=FlowDirection.TopDown, WrapContents=false, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, Width=820, Padding=new Padding(20), Margin=new Padding(0,0,0,16), BackColor=Color.FromArgb(20,31,24), Tag="card" };
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
        var p=new FlowLayoutPanel { Width=790, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(14), Margin=new Padding(0,0,0,10), BackColor=Color.FromArgb(28,44,34), Tag="card" };
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
        currentPage=name; pageTitle.Text=name;
        foreach(var pair in pages.Where(p=>p.Key!="__body")) pair.Value.Visible=pair.Key==name;
        foreach(Control c in sidebar.Controls) if(c is FlowLayoutPanel f) foreach(Control b in f.Controls) if(b is Button btn) { bool active=(string)btn.Tag! == name; btn.BackColor=active?Accent():PanelColor(); btn.ForeColor=TextColor(); }
        if(name=="Процессы") RefreshProcesses();
        if(name=="Проверка ПК") RefreshStats();
        if(name=="Оптимизация") foreach(var item in tweakItems) { var p=tweaksPanel.Controls.Cast<Control>().FirstOrDefault(x=>x.Tag?.ToString()==item.Id); }
        ApplyTheme();
    }
    private void RefreshStats()
    {
        try
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
            var mem=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()}; GlobalMemoryStatusEx(ref mem);
            var drive=new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            cpuValue.Text=$"{cpu:0}%"; ramValue.Text=$"{(mem.TotalPhys==0?0:(100.0*(mem.TotalPhys-mem.AvailPhys)/mem.TotalPhys)):0}%";
            diskValue.Text=$"{100.0*(drive.TotalSize-drive.AvailableFreeSpace)/Math.Max(1,drive.TotalSize):0}%";
            osValue.Text=Environment.OSVersion.VersionString;
            scanSummary.Text=$"Устройство: {Environment.MachineName}\nWindows: {Environment.OSVersion.Version}   ·   Потоки CPU: {Environment.ProcessorCount}\nПроверка завершена: {DateTime.Now:T}";
            drivesPanel.Controls.Clear();
            foreach(var d in DriveInfo.GetDrives().Where(x=>x.IsReady))
            {
                var l=new Label{Width=700,Height=30,Text=$"{d.Name}   {FormatBytes(d.TotalSize-d.AvailableFreeSpace)} свободно из {FormatBytes(d.TotalSize)}",Padding=new Padding(4)};
                drivesPanel.Controls.Add(l);
            }
            status.Text="●  Система обновлена";
        } catch(Exception ex){status.Text="●  Не удалось обновить данные"; scanSummary.Text=ex.Message;}
    }
    private void RefreshProcesses()
    {
        try
        {
            processGrid.Rows.Clear();
            foreach(var p in Process.GetProcesses().OrderByDescending(x=>SafeMemory(x)).Take(100))
            {
                try { processGrid.Rows.Add(p.ProcessName,FormatBytes(p.WorkingSet64)); } catch {}
                finally { p.Dispose(); }
            }
        } catch {}
    }
    private static long SafeMemory(Process p){try{return p.WorkingSet64;}catch{return 0;}}
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
        try
        {
            var manifest = await http.GetFromJsonAsync<UpdateManifest>("https://pc-booster-ai-api.onrender.com/api/updates/latest");
            if (manifest == null || !Version.TryParse(manifest.version, out var latest) || !Version.TryParse(CurrentVersion, out var current))
                throw new Exception("Сервер вернул некорректную информацию о версии.");
            if (latest <= current)
            {
                availableUpdate = null;
                updateStatusLabel.Text = "Установлена последняя версия — " + CurrentVersion + ".";
            }
            else
            {
                availableUpdate = manifest;
                updateStatusLabel.Text = "Доступна версия " + manifest.version + " (сейчас " + CurrentVersion + "). Размер: " + FormatBytes(manifest.size) + ".";
                installUpdateButton.Enabled = true;
            }
        }
        catch (Exception ex) { updateStatusLabel.Text = "Не удалось проверить обновления: " + ex.Message; }
        finally { updateBusy = false; }
    }

    private async void InstallUpdate()
    {
        if (updateBusy || availableUpdate == null) return;
        updateBusy = true;
        installUpdateButton.Enabled = false;
        updateStatusLabel.Text = "Скачиваю обновление...";
        string? downloadedPath = null;
        try
        {
            var manifest = availableUpdate;
            if (manifest.size <= 0 || manifest.size > 200L * 1024 * 1024) throw new Exception("Размер обновления не прошёл проверку безопасности.");
            var bytes = await http.GetByteArrayAsync("https://pc-booster-ai-api.onrender.com/api/updates/download");
            if (bytes.LongLength != manifest.size) throw new Exception("Размер загруженного файла не совпадает с данными сервера.");
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actualHash), System.Text.Encoding.ASCII.GetBytes(manifest.sha256.ToLowerInvariant())))
                throw new Exception("Контрольная сумма обновления не совпала. Файл не будет установлен.");
            downloadedPath = Path.Combine(Path.GetTempPath(), "PC-Booster-update-" + Guid.NewGuid().ToString("N") + ".exe");
            await File.WriteAllBytesAsync(downloadedPath, bytes);
            var targetPath = Application.ExecutablePath;
            var scriptPath = Path.Combine(Path.GetTempPath(), "PC-Booster-updater-" + Guid.NewGuid().ToString("N") + ".ps1");
            string Ps(string value) => "'" + value.Replace("'", "''") + "'";
            var script = "$ErrorActionPreference = 'Stop'\n" +
                "$target = " + Ps(targetPath) + "\n" +
                "$source = " + Ps(downloadedPath) + "\n" +
                "$processId = " + Environment.ProcessId + "\n" +
                "try {\n" +
                "  while (Get-Process -Id $processId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }\n" +
                "  Copy-Item -LiteralPath $source -Destination $target -Force\n" +
                "  Start-Process -FilePath $target\n" +
                "  Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue\n" +
                "  Start-Sleep -Seconds 2\n" +
                "  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue\n" +
                "} catch { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'PC Booster — ошибка обновления') | Out-Null }";
            await File.WriteAllTextAsync(scriptPath, script, new System.Text.UTF8Encoding(false));
            Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"", UseShellExecute = false, CreateNoWindow = true });
            updateStatusLabel.Text = "Обновление загружено. PC Booster сейчас перезапустится...";
            await Task.Delay(700);
            Application.Exit();
        }
        catch (Exception ex)
        {
            if (downloadedPath != null) try { File.Delete(downloadedPath); } catch { }
            updateStatusLabel.Text = "Не удалось установить обновление: " + ex.Message;
            installUpdateButton.Enabled = availableUpdate != null;
            updateBusy = false;
        }
    }

    private sealed class UpdateManifest { public string version { get; set; } = ""; public string tag { get; set; } = ""; public string sha256 { get; set; } = ""; public long size { get; set; } public DateTime? publishedAt { get; set; } }
    private void ApplyTheme()
    {
        var bg=light?Color.FromArgb(241,247,243):Color.FromArgb(11,19,14);
        var panel=light?Color.White:Color.FromArgb(20,32,25);
        var text=light?Color.FromArgb(25,43,31):Color.FromArgb(237,246,239);
        var muted=light?Color.FromArgb(92,112,98):Color.FromArgb(155,177,161);
        BackColor=bg; sidebar.BackColor=panel; content.BackColor=bg;
        foreach(Control c in Controls) PaintTree(c,bg,panel,text,muted);
        foreach(Control c in sidebar.Controls) if(c is FlowLayoutPanel f) foreach(Control b in f.Controls) if(b is Button btn){btn.FlatAppearance.MouseOverBackColor=light?Color.FromArgb(222,242,228):Color.FromArgb(35,61,43); btn.BackColor=(string)btn.Tag! == currentPage?Accent():PanelColor(); btn.ForeColor=(string)btn.Tag! == currentPage?(light?Color.White:Color.FromArgb(7,29,15)):TextColor();}
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
    private sealed record Tweak(string Id,string Name,string Category,string Description,string Key,string ValueName,RegistryValueKind Kind,object Value);
    private sealed record RegValue(string Key,string Name,bool Existed,string? Value,int Kind);
    private sealed class AiReply{public string? answer{get;set;} public string? error{get;set;}}
}