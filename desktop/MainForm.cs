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
    private bool animationsEnabled = true;
    private Button? animationToggleButton;
    private readonly Dictionary<Button, System.Windows.Forms.Timer> hoverTimers = new();
    private static readonly string appearancePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCBooster", "appearance.json");
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
        LoadAppearance();
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
        sidebar.Dock=DockStyle.Left; sidebar.Width=260; sidebar.Padding=new Padding(18,22,18,18);
        content.Dock=DockStyle.Fill; content.Padding=new Padding(28,25,28,24);
        Controls.Add(content); Controls.Add(sidebar);
        var brand=new Label { Text="✦  PC BOOSTER", AutoSize=false, Height=68, Dock=DockStyle.Top, Font=new Font("Segoe UI",17,FontStyle.Bold), TextAlign=ContentAlignment.MiddleLeft, ForeColor=Color.FromArgb(226,244,255) };
        sidebar.Controls.Add(brand);
        var bottom=new Label { Text="▦   WINDOWS 10 / 11", Dock=DockStyle.Bottom, Height=48, Font=new Font("Segoe UI",9,FontStyle.Bold), TextAlign=ContentAlignment.MiddleLeft, ForeColor=Color.FromArgb(130,177,216) };
        sidebar.Controls.Add(bottom);
        var protection=new Panel { Dock=DockStyle.Bottom, Height=76, Padding=new Padding(12), BackColor=Color.FromArgb(13,42,66), Margin=new Padding(0,0,0,12) };
        var shield=new Label { Text="✓", Dock=DockStyle.Left, Width=42, Font=new Font("Segoe UI",22,FontStyle.Bold), ForeColor=Color.FromArgb(70,220,170), TextAlign=ContentAlignment.MiddleCenter };
        var protectionText=new Label { Text="Защита включена\nВаш ПК в безопасности", Dock=DockStyle.Fill, Font=new Font("Segoe UI",9,FontStyle.Bold), ForeColor=Color.FromArgb(220,240,255), TextAlign=ContentAlignment.MiddleLeft };
        protection.Controls.Add(protectionText); protection.Controls.Add(shield); sidebar.Controls.Add(protection);
        var nav=new SmoothFlowPanel { Dock=DockStyle.Top, Height=438, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(0,20,0,0) };
        sidebar.Controls.Add(nav);
        var navigation = new (string Name,string Caption)[] {
            ("Обзор","⌂   Обзор"),("Проверка ПК","⌕   Проверка ПК"),("Оптимизация","⚡   Оптимизация"),
            ("Процессы","⌁   Процессы"),("AI-помощник","✦   AI-помощник"),("Настройки","⚙   Настройки")
        };
        foreach(var item in navigation)
        {
            var name=item.Name;
            var b=new Button { Text=item.Caption, Width=222, Height=56, TextAlign=ContentAlignment.MiddleLeft, FlatStyle=FlatStyle.Flat, Tag=name, Margin=new Padding(0,0,0,8), Padding=new Padding(16,0,8,0), Font=new Font("Segoe UI",10.5f,FontStyle.Bold), Cursor=Cursors.Hand };
            b.FlatAppearance.BorderSize=0; b.FlatAppearance.MouseOverBackColor=Color.FromArgb(20,62,99);
            b.Click+=(_,_)=>ShowPage(name); AttachButtonEffects(b); navigationButtons.Add(b); nav.Controls.Add(b);
        }
        var top=new Panel { Dock=DockStyle.Top, Height=76 };
        pageTitle.Dock=DockStyle.Left; pageTitle.AutoSize=true; pageTitle.Font=new Font("Segoe UI",25,FontStyle.Bold);
        status.Dock=DockStyle.Right; status.AutoSize=true; status.Text="●  Приложение запущено"; status.TextAlign=ContentAlignment.MiddleRight;
        status.Font=new Font("Segoe UI",9.5f,FontStyle.Bold); status.ForeColor=Color.FromArgb(64,220,164);
        top.Controls.Add(status); top.Controls.Add(pageTitle); content.Controls.Add(top);
        var body=new Panel { Dock=DockStyle.Fill, Padding=new Padding(0,14,0,0), AutoScroll=false };
        content.Controls.Add(body); body.BringToFront(); top.BringToFront();
        pages["__body"]=body; BuildPages(body); body.Resize += (_,_) => ResizePages(body);
    }

    private void BuildPages(Panel body)
    {
        var overview=MakePage();
        overview.Controls.Add(BuildHeroBanner());
        var stats=new TableLayoutPanel { Dock=DockStyle.Top, Height=176, ColumnCount=3, RowCount=1, Padding=new Padding(0,12,0,12), BackColor=Color.Transparent };
        for(int i=0;i<3;i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.33f));
        stats.Controls.Add(Metric("ПРОЦЕССОР",ref cpuValue),0,0);
        stats.Controls.Add(Metric("ОПЕРАТИВНАЯ ПАМЯТЬ",ref ramValue),1,0);
        stats.Controls.Add(Metric("ДИСК WINDOWS",ref diskValue),2,0);
        overview.Controls.Add(stats);
        var tip=Card("⚡  Совет от PC Booster","Регулярная очистка помогает поддерживать порядок и освобождать место на диске.");
        var tipButton=ActionButton("Узнать больше  ›",()=>ShowPage("Оптимизация")); tipButton.Height=40; tip.Controls.Add(tipButton);
        overview.Controls.Add(tip);
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
            SelectionMode=DataGridViewSelectionMode.FullRowSelect, BackgroundColor=Color.FromArgb(13,32,54),
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

        var setcard=Card("Вид","Выбери оформление приложения. Настройка сохраняется и применяется сразу.");
        setcard.Padding=new Padding(22);
        var themeInfo=new Label { Dock=DockStyle.Top, Height=38, Text="Цветовая тема", Padding=new Padding(4,10,4,4), Font=new Font("Segoe UI",10,FontStyle.Bold) };
        var themeRow=new FlowLayoutPanel { AutoSize=true, FlowDirection=FlowDirection.LeftToRight, WrapContents=true, Dock=DockStyle.Top, Padding=new Padding(0,4,0,8) };
        var darkTheme=ActionButton("ТЁМНАЯ ТЕМА",()=>SetTheme(false)); darkTheme.Height=44;
        var lightTheme=ActionButton("СВЕТЛАЯ ТЕМА",()=>SetTheme(true)); lightTheme.Height=44;
        themeRow.Controls.Add(darkTheme); themeRow.Controls.Add(lightTheme);
        setcard.Controls.Add(themeRow); setcard.Controls.Add(themeInfo);
        settings.Controls.Add(setcard);

        var animationCard=Card("Анимации","Плавная подсветка кнопок при наведении. Можно отключить для более строгого интерфейса.");
        animationCard.Padding=new Padding(22);
        animationToggleButton=ActionButton("АНИМАЦИИ: ВКЛЮЧЕНЫ",ToggleAnimations);
        animationToggleButton.Height=44;
        animationCard.Controls.Add(animationToggleButton);
        settings.Controls.Add(animationCard);
        pages["Настройки"]=settings;

        foreach(var pair in pages.Where(p=>p.Key!="__body")) { var c=pair.Value; c.Dock=DockStyle.Fill; c.Visible=false; body.Controls.Add(c); }
    }

    private Panel BuildHeroBanner()
    {
        var banner=new Panel { Height=246, Width=900, Margin=new Padding(0,0,0,8), BackColor=Color.FromArgb(8,42,83), Padding=new Padding(24), Tag="hero" };
        var title=new Label { Text="Компьютер под контролем", AutoSize=false, Font=new Font("Segoe UI",21,FontStyle.Bold), ForeColor=Color.FromArgb(239,248,255), BackColor=Color.Transparent };
        var subtitle=new Label { Text="Система работает стабильно и эффективно.", AutoSize=false, Font=new Font("Segoe UI",11.5f), ForeColor=Color.FromArgb(171,208,239), BackColor=Color.Transparent };
        var scan=ActionButton("⌕   Проверить компьютер",()=>ShowPage("Проверка ПК")); scan.Height=52; scan.Font=new Font("Segoe UI",11,FontStyle.Bold);
        banner.Controls.Add(title); banner.Controls.Add(subtitle); banner.Controls.Add(scan);
        void LayoutBanner(){int right=Math.Max(360,banner.ClientSize.Width/2); title.SetBounds(right,48,Math.Max(280,banner.ClientSize.Width-right-24),42); subtitle.SetBounds(right,96,Math.Max(260,banner.ClientSize.Width-right-24),34); scan.SetBounds(right,150,Math.Min(310,Math.Max(230,banner.ClientSize.Width-right-24)),52);}
        banner.Resize+=(_,_)=>LayoutBanner(); LayoutBanner();
        banner.Paint+=(_,e)=>{
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
            using var brush=new LinearGradientBrush(banner.ClientRectangle,Color.FromArgb(9,75,143),Color.FromArgb(5,27,59),35f); g.FillRectangle(brush,banner.ClientRectangle);
            using var glow=new SolidBrush(Color.FromArgb(42,32,151,245)); g.FillEllipse(glow,-90,35,360,270);
            using var pen=new Pen(Color.FromArgb(100,65,174,255),2); g.DrawArc(pen,banner.Width-340,-160,430,430,40,110); g.DrawArc(pen,banner.Width-290,-120,390,390,40,110);
            var laptop=new Rectangle(55,48,235,130);
            using var screenBrush=new LinearGradientBrush(laptop,Color.FromArgb(4,14,34),Color.FromArgb(17,86,151),35f);
            using var screenPath=RoundedRect(laptop,12); g.FillPath(screenBrush,screenPath);
            using var screenPen=new Pen(Color.FromArgb(100,112,207,255),3); g.DrawPath(screenPen,screenPath);
            using var winBrush=new SolidBrush(Color.FromArgb(60,185,255)); int wx=150,wy=88;
            g.FillRectangle(winBrush,wx,wy,28,24); g.FillRectangle(winBrush,wx+33,wy,28,24); g.FillRectangle(winBrush,wx,wy+29,28,24); g.FillRectangle(winBrush,wx+33,wy+29,28,24);
            Point[] basePoints={new Point(48,181),new Point(297,181),new Point(322,196),new Point(25,196)};
            using var baseBrush=new LinearGradientBrush(new Rectangle(25,181,297,16),Color.FromArgb(83,169,235),Color.FromArgb(15,63,115),90f); g.FillPolygon(baseBrush,basePoints);
            using var basePen=new Pen(Color.FromArgb(120,150,215,255),1); g.DrawPolygon(basePen,basePoints);
        };
        return banner;
    }
    private static GraphicsPath RoundedRect(Rectangle bounds,int radius)
    {
        var path=new GraphicsPath(); int d=radius*2;
        path.AddArc(bounds.X,bounds.Y,d,d,180,90); path.AddArc(bounds.Right-d,bounds.Y,d,d,270,90);
        path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90); path.AddArc(bounds.X,bounds.Bottom-d,d,d,90,90);
        path.CloseFigure(); return path;
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
        var p=new CardFlowPanel { FlowDirection=FlowDirection.TopDown, WrapContents=false, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, Width=860, Padding=new Padding(22), Margin=new Padding(0,0,0,18), BackColor=Color.FromArgb(13,29,48), Tag="card" };
        p.ControlAdded += (_,e) => FitCardControl(p,e.Control);
        p.SizeChanged += (_,_) => { foreach(Control child in p.Controls) FitCardControl(p,child); };
        var h=new Label { Text=title, AutoSize=true, Font=new Font("Segoe UI",14,FontStyle.Bold), Margin=new Padding(0,0,0,5) };
        p.Controls.Add(h);
        if(!string.IsNullOrWhiteSpace(subtitle)) p.Controls.Add(new Label { Text=subtitle, AutoSize=true, Font=new Font("Segoe UI",9.5f), ForeColor=Color.FromArgb(158,188,216), Margin=new Padding(0,0,0,12) });
        return p;
    }
    private Control Metric(string caption,ref Label value)
    {
        var p=new Panel { Dock=DockStyle.Fill, Padding=new Padding(18,16,14,14), Margin=new Padding(0,0,12,0), BackColor=Color.FromArgb(14,34,57), Tag="metric" };
        var glyph=caption.Contains("ПРОЦЕССОР")?"▦":caption.Contains("ПАМЯТЬ")?"▤":"▱";
        var icon=new Label { Text=glyph, Dock=DockStyle.Left, Width=48, Font=new Font("Segoe UI Symbol",25,FontStyle.Bold), ForeColor=Color.FromArgb(61,190,255), TextAlign=ContentAlignment.MiddleLeft };
        var heading=new Label { Text=caption, Dock=DockStyle.Top, Height=28, Font=new Font("Segoe UI",8.5f,FontStyle.Bold), ForeColor=Color.FromArgb(166,204,236), TextAlign=ContentAlignment.MiddleLeft };
        value.Dock=DockStyle.Fill; value.Height=54; value.Font=new Font("Segoe UI",21,FontStyle.Bold); value.TextAlign=ContentAlignment.MiddleLeft; value.ForeColor=Color.FromArgb(241,248,255);
        p.Controls.Add(value); p.Controls.Add(icon); p.Controls.Add(heading);
        return p;
    }
    private Button ActionButton(string text,Action action)
    {
        var b=new Button { Text=text, Height=42, MinimumSize=new Size(150,42), AutoSize=true, Padding=new Padding(16,5,16,5), FlatStyle=FlatStyle.Flat, Font=new Font("Segoe UI",9.5f,FontStyle.Bold), Margin=new Padding(0,8,10,8), Cursor=Cursors.Hand };
        b.Click+=(_,_)=>action(); AttachButtonEffects(b); return b;
    }
    private void AddTweakCard(Tweak item)
    {
        var p=new CardFlowPanel { Width=820, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(16), Margin=new Padding(0,0,0,12), BackColor=Color.FromArgb(15,37,61), Tag="card" };
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
        var body=pages["__body"];
        body.SuspendLayout();
        try
        {
            foreach(var pair in pages.Where(p=>p.Key!="__body"))
                pair.Value.Visible=pair.Key==name;
        }
        finally { body.ResumeLayout(true); }
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
            button.ForeColor=active?(light?Color.White:Color.FromArgb(4,24,42)):TextColor();
            button.FlatAppearance.MouseOverBackColor=light?Color.FromArgb(220,239,255):Color.FromArgb(25,65,104);
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

        string details=$"Устройство: {Environment.MachineName}\nWindows: {Environment.OSVersion.Version}   ·   Потоки CPU: {Environment.ProcessorCount}\nПроверка завершена: {DateTime.Now:T}";
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
                    manifest.deltaSize < manifest.size && manifest.deltaSha256.Length == 64 &&
                    File.Exists(Path.Combine(AppContext.BaseDirectory, "xdelta3.exe"));
                if (canDelta)
                {
                    updateStatusLabel.Text = "Доступна версия " + manifest.version + ". Будет скачано только изменений: " +
                        FormatBytes(manifest.deltaSize) + ". Полный файл приложения загружаться не будет.";
                    status.Text = "●  Доступно компактное обновление " + manifest.version;
                    installUpdateButton.Text = pendingUpdatePath != null && File.Exists(pendingUpdatePath)
                        ? "ОБНОВИТЬ СЕЙЧАС" : "СКАЧАТЬ ОБНОВЛЕНИЕ";
                    installUpdateButton.Enabled = true;
                    promptUpdate = manifest;
                }
                else
                {
                    updateStatusLabel.Text = "Версия " + manifest.version + " найдена, но компактный пакет для " +
                        CurrentVersion + " пока недоступен. Полная версия не будет скачана.";
                    status.Text = "●  Ожидается частичный пакет обновления";
                    installUpdateButton.Text = "ЧАСТИЧНЫЙ ПАКЕТ НЕДОСТУПЕН";
                    installUpdateButton.Enabled = false;
                }
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
                        catch (Exception ex)
                        {
                            try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
                            try { if (patchPath != null && File.Exists(patchPath)) File.Delete(patchPath); } catch { }
                            patchPath = null;
                            throw new InvalidDataException(
                                "Частичное обновление не удалось применить. Полная версия не скачивалась. " +
                                "Проверь обновление позже.", ex);
                        }
                    }

                    if (!ready)
                        throw new InvalidDataException("Для этой версии нет подходящего частичного пакета. Полная версия не будет скачана.");

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
                "  $staged = $target + '.new'\n" +
                "  $backup = $target + '.previous'\n" +
                "  Copy-Item -LiteralPath $source -Destination $staged -Force\n" +
                "  $stagedHash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant()\n" +
                "  if ($stagedHash -ne " + Ps(manifest.sha256) + ") { throw 'Проверка скачанного EXE не пройдена.' }\n" +
                "  if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }\n" +
                "  Move-Item -LiteralPath $target -Destination $backup -Force\n" +
                "  try { Move-Item -LiteralPath $staged -Destination $target -Force } catch { Move-Item -LiteralPath $backup -Destination $target -Force; throw }\n" +
                "  $installedHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()\n" +
                "  if ($installedHash -ne " + Ps(manifest.sha256) + ") { Remove-Item -LiteralPath $target -Force; Move-Item -LiteralPath $backup -Destination $target -Force; throw 'Новый файл не прошёл проверку после установки. Старая версия восстановлена.' }\n" +
                "  Start-Process -FilePath $target\n" +
                "  Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue\n" +
                "  Start-Sleep -Seconds 2\n" +
                "  Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue\n" +
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
            BackColor = Color.FromArgb(6, 18, 34);
            ForeColor = Color.FromArgb(239, 247, 255);
            Font = new Font("Segoe UI", 9.5f);

            var accent = new Panel
            {
                Dock = DockStyle.Top,
                Height = 5,
                BackColor = Color.FromArgb(75, 183, 255)
            };
            heading.SetBounds(28, 24, 460, 30);
            heading.Text = "Загружаю PC Booster " + version;
            heading.Font = new Font("Segoe UI", 15, FontStyle.Bold);
            heading.ForeColor = Color.FromArgb(239, 247, 255);

            stage.SetBounds(30, 67, 458, 24);
            stage.Text = "Подключаюсь к серверу обновлений...";
            stage.ForeColor = Color.FromArgb(164, 190, 216);

            progressBar.SetBounds(30, 106, 458, 16);
            progressBar.Minimum = 0;
            progressBar.Maximum = 1000;
            progressBar.Style = ProgressBarStyle.Continuous;

            amount.SetBounds(30, 136, 350, 24);
            amount.Text = "Подготовка загрузки...";
            amount.ForeColor = Color.FromArgb(164, 190, 216);

            percent.SetBounds(400, 136, 88, 24);
            percent.TextAlign = ContentAlignment.MiddleRight;
            percent.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            percent.ForeColor = Color.FromArgb(89, 202, 255);

            cancelButton.SetBounds(358, 178, 130, 38);
            cancelButton.Text = "Отменить";
            cancelButton.FlatStyle = FlatStyle.Flat;
            cancelButton.FlatAppearance.BorderColor = Color.FromArgb(48, 91, 130);
            cancelButton.BackColor = Color.FromArgb(17, 43, 69);
            cancelButton.ForeColor = Color.FromArgb(239, 247, 255);
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
    private void LoadAppearance()
    {
        try
        {
            if (!File.Exists(appearancePath)) return;
            var saved = JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(appearancePath));
            if (saved == null) return;
            light = saved.Light;
            animationsEnabled = saved.AnimationsEnabled;
        }
        catch { light = false; animationsEnabled = true; }
    }

    private void SaveAppearance()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(appearancePath)!);
            File.WriteAllText(appearancePath, JsonSerializer.Serialize(new AppearanceSettings(light, animationsEnabled)));
        }
        catch { }
    }

    private void SetTheme(bool useLight)
    {
        light = useLight;
        SaveAppearance();
        ApplyTheme();
    }

    private void ToggleAnimations()
    {
        animationsEnabled = !animationsEnabled;
        SaveAppearance();
        if (animationToggleButton != null)
            animationToggleButton.Text = animationsEnabled ? "АНИМАЦИИ: ВКЛЮЧЕНЫ" : "АНИМАЦИИ: ВЫКЛЮЧЕНЫ";
        foreach (var pair in hoverTimers.ToArray())
        {
            pair.Value.Stop();
            pair.Value.Dispose();
            hoverTimers.Remove(pair.Key);
            if (!pair.Key.IsDisposed) pair.Key.BackColor = ButtonBaseColor(pair.Key);
        }
    }

    private void AttachButtonEffects(Button button)
    {
        button.MouseEnter += (_, _) => AnimateButtonHover(button, true);
        button.MouseLeave += (_, _) => AnimateButtonHover(button, false);
    }

    private Color ButtonBaseColor(Button button)
    {
        bool nav = navigationButtons.Contains(button);
        if (!nav) return Accent();
        return string.Equals(button.Tag as string, currentPage, StringComparison.Ordinal) ? Accent() : PanelColor();
    }

    private void AnimateButtonHover(Button button, bool entering)
    {
        if (button.IsDisposed) return;
        var target = entering
            ? (navigationButtons.Contains(button)
                ? (light ? Color.FromArgb(222, 241, 255) : Color.FromArgb(25, 67, 105))
                : (light ? Color.FromArgb(42, 145, 215) : Color.FromArgb(91, 194, 255)))
            : ButtonBaseColor(button);
        if (!animationsEnabled)
        {
            button.BackColor = target;
            return;
        }
        if (hoverTimers.TryGetValue(button, out var old))
        {
            old.Stop();
            old.Dispose();
            hoverTimers.Remove(button);
        }
        var start = button.BackColor;
        int step = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        hoverTimers[button] = timer;
        timer.Tick += (_, _) =>
        {
            if (button.IsDisposed)
            {
                timer.Stop(); timer.Dispose(); hoverTimers.Remove(button); return;
            }
            step++;
            float t = Math.Min(1f, step / 7f);
            button.BackColor = Color.FromArgb(
                (int)(start.R + (target.R - start.R) * t),
                (int)(start.G + (target.G - start.G) * t),
                (int)(start.B + (target.B - start.B) * t));
            if (step >= 7)
            {
                timer.Stop(); timer.Dispose(); hoverTimers.Remove(button);
            }
        };
        timer.Start();
    }

    private void ApplyTheme()
    {
        var bg=light?Color.FromArgb(239,247,255):Color.FromArgb(6,18,34);
        var panel=light?Color.White:Color.FromArgb(12,29,49);
        var text=light?Color.FromArgb(18,48,75):Color.FromArgb(237,246,239);
        var muted=light?Color.FromArgb(83,119,153):Color.FromArgb(158,188,216);
        BackColor=bg; sidebar.BackColor=panel; content.BackColor=bg;
        foreach(Control c in Controls) PaintTree(c,bg,panel,text,muted);
        UpdateNavigationVisuals();
        if (animationToggleButton != null) animationToggleButton.Text = animationsEnabled ? "АНИМАЦИИ: ВКЛЮЧЕНЫ" : "АНИМАЦИИ: ВЫКЛЮЧЕНЫ";
        foreach(var pair in pages.Where(p=>p.Key!="__body")) if(pair.Value is FlowLayoutPanel page) ResizePage(page);
    }
    private void PaintTree(Control c,Color bg,Color panel,Color text,Color muted)
    {
        c.ForeColor=text;
        if(c is Panel p)
        {
            p.BackColor=p.Tag?.ToString()=="card" || p.Tag?.ToString()=="metric" ? (light?Color.White:Color.FromArgb(14,34,57)) : p.Parent==sidebar?panel:bg;
        }
        if(c is Button b)
        {
            bool navigation=b.Parent==sidebar.Controls.Cast<Control>().FirstOrDefault(x=>x is FlowLayoutPanel);
            b.BackColor=navigation?PanelColor():Accent();
            b.ForeColor=navigation?TextColor():(light?Color.White:Color.FromArgb(4,24,42));
            b.FlatAppearance.BorderSize=0;
        }
        if(c is TextBox or RichTextBox){c.BackColor=panel;c.ForeColor=text;}
        if(c is Label label && label.Parent?.Tag?.ToString()=="card") label.ForeColor=text;
        if(c is DataGridView g){g.BackgroundColor=panel;g.DefaultCellStyle.BackColor=panel;g.DefaultCellStyle.ForeColor=text;g.ColumnHeadersDefaultCellStyle.BackColor=Accent();g.ColumnHeadersDefaultCellStyle.ForeColor=Color.Black;g.EnableHeadersVisualStyles=false;}
        foreach(Control child in c.Controls) PaintTree(child,bg,panel,text,muted);
    }
    private Color Accent()=>light?Color.FromArgb(24,126,205):Color.FromArgb(62,177,255);
    private Color PanelColor()=>light?Color.White:Color.FromArgb(13,29,48);
    private Color TextColor()=>light?Color.FromArgb(18,48,75):Color.FromArgb(237,246,239);
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
            using var pen=new Pen(Color.FromArgb(48,75,183,255),1);
            e.Graphics.DrawPath(pen,path);
        }
    }

    private sealed record AppearanceSettings(bool Light, bool AnimationsEnabled);
    private sealed record Tweak(string Id,string Name,string Category,string Description,string Key,string ValueName,RegistryValueKind Kind,object Value);
    private sealed record RegValue(string Key,string Name,bool Existed,string? Value,int Kind);
    private sealed class AiReply{public string? answer{get;set;} public string? error{get;set;}}
}