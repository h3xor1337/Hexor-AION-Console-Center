using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shell;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using System.Windows.Input;
using Microsoft.Win32;
namespace Hexor;
public sealed class MainWindow : Window
{
 public MainViewModel Model {get;}
 private readonly ContentControl content=new();private readonly ScrollViewer pageScroll=new(){Name="PageScroll",VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};private readonly StackPanel navigation=new(){Name="SidebarNavigation"};private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(8)};
 private int page;private readonly string[] pages=["General","Auto Task","Character Enhancement","Shugo Festival"];
 private readonly string windowId=Guid.NewGuid().ToString("N")[..8];
 private string closeOrigin="window-close-or-external";
 private readonly TextBlock title=new(){FontSize=30,FontWeight=FontWeights.SemiBold};
 private readonly MenuStatusService menuStatusService=new();
 private MenuStatusResult menuStatus=new(MenuStatusState.Checking,"Checking — installation discovery in progress");
 private TextBlock? menuStatusText;
 private CancellationTokenSource? menuRefresh;
 private readonly TaskCompletionSource initialLoadCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);
 internal Task InitialLoadCompleted=>initialLoadCompletion.Task;
 private readonly bool automaticallyCheckForUpdates;
 public MainWindow(MainViewModel? model=null, bool automaticallyCheckForUpdates=true)
 {
  this.automaticallyCheckForUpdates=automaticallyCheckForUpdates;
  Model=model??new();DataContext=Model;Title="HEXOR AION CONTROL CENTER";Width=Math.Min(1200,SystemParameters.WorkArea.Width);Height=Math.Min(760,SystemParameters.WorkArea.Height);MinWidth=920;MinHeight=620;ResizeMode=ResizeMode.CanResize;UseLayoutRounding=true;SnapsToDevicePixels=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brush("#0B0912");Foreground=Brushes.White;FontFamily=new("Segoe UI Variable");FontSize=14;
  WindowStyle=WindowStyle.None;
  WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=46,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(14),UseAeroCaptionButtons=false,NonClientFrameEdges=NonClientFrameEdges.None});
  var shell=new Grid();shell.RowDefinitions.Add(new(){Height=new GridLength(46)});shell.RowDefinitions.Add(new());Content=new Border{BorderBrush=Brush("#32294A"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),ClipToBounds=true,Child=shell};
  var chrome=new Grid{Name="HexorTitleBar",Background=Brush("#110E1B")};chrome.ColumnDefinitions.Add(new());chrome.ColumnDefinitions.Add(new(){Width=GridLength.Auto});shell.Children.Add(chrome);
  var caption=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(16,0,0,0),VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
  var suppliedIcon=BrandImage("Assets/Hex Logo-Transparent.ico",22,22);if(suppliedIcon!=null){Icon=suppliedIcon.Source;caption.Children.Add(suppliedIcon);}
  caption.Children.Add(new TextBlock{Text=Title,FontSize=11,FontWeight=FontWeights.SemiBold,Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center});chrome.Children.Add(caption);
  var windowButtons=new StackPanel{Orientation=Orientation.Horizontal};Grid.SetColumn(windowButtons,1);chrome.Children.Add(windowButtons);
  foreach(var kind in new[]{"Minimize","Maximize","Close"}){var b=new Button{Name=kind+"WindowButton",Content=kind=="Close"?"×":kind=="Maximize"?"□":"—",Width=48,Height=44,MinHeight=0,Padding=new Thickness(0),Margin=new Thickness(0),Background=Brush(kind=="Close"?"#25182C":"#110E1B"),ToolTip=kind=="Close"?"Close (Alt+F4)":kind,HorizontalContentAlignment=HorizontalAlignment.Center};AutomationProperties.SetName(b,kind+" window");WindowChrome.SetIsHitTestVisibleInChrome(b,true);b.Click+=(_,_)=>{if(kind=="Close"){closeOrigin="titlebar-close";App.RecordShutdownOrigin(closeOrigin);SystemCommands.CloseWindow(this);}else if(kind=="Minimize")WindowState=WindowState.Minimized;else if(WindowState==WindowState.Maximized)WindowState=WindowState.Normal;else WindowState=WindowState.Maximized;};windowButtons.Children.Add(b);}
  PreviewKeyDown+=(_,e)=>{if(e.Key==Key.System&&e.SystemKey==Key.F4){closeOrigin="alt-f4";App.RecordShutdownOrigin(closeOrigin);}};
  PreviewKeyDown+=(_,e)=>{if(e.Key==Key.System&&e.SystemKey==Key.Space){SystemCommands.ShowSystemMenu(this,PointToScreen(new Point(0,44)));e.Handled=true;}};
  var root=new Grid();root.ColumnDefinitions.Add(new(){Width=new(260)});root.ColumnDefinitions.Add(new());Grid.SetRow(root,1);shell.Children.Add(root);
  var sidebar=new DockPanel{Background=Brush("#100D19"),Margin=new(0)};root.Children.Add(sidebar);
  var brand=new StackPanel{Margin=new(22,24,22,20)};var suppliedLogo=BrandImage("Assets/Hex Logo-Transparent.png",196,64);if(suppliedLogo!=null)brand.Children.Add(suppliedLogo);else brand.Children.Add(Text("HEXOR",29,true));brand.Children.Add(Text("AION CONTROL CENTER",10,false,"#B9ADCA"));DockPanel.SetDock(brand,Dock.Top);sidebar.Children.Add(brand);
  var footer=new StackPanel{Margin=new(18,12,18,22)};var updaterButton=Button("Open AION Updater",async()=>await Model.OpenAionUpdaterAsync());updaterButton.Name="OpenAionUpdaterButton";updaterButton.HorizontalAlignment=HorizontalAlignment.Stretch;updaterButton.Background=Brush("#6942A8");updaterButton.ToolTip="Open the original AION updater";footer.Children.Add(updaterButton);
  var updaterStatus=Text("",13,true,"#DCC8FF");updaterStatus.Name="AionUpdaterStatus";updaterStatus.SetBinding(TextBlock.TextProperty,nameof(MainViewModel.UpdateStatus));AutomationProperties.SetLiveSetting(updaterStatus,AutomationLiveSetting.Polite);footer.Children.Add(updaterStatus);
  footer.Children.Add(Text("ORIGINAL UPDATER · ON DEMAND",9,false,"#AFA0C7"));DockPanel.SetDock(footer,Dock.Bottom);sidebar.Children.Add(footer);sidebar.Children.Add(navigation);
  var main=new DockPanel{Margin=new(32,26,32,20)};Grid.SetColumn(main,1);root.Children.Add(main);
  var head=new StackPanel();head.Children.Add(title);head.Margin=new(0,0,0,14);DockPanel.SetDock(head,Dock.Top);main.Children.Add(head);
  var status=new TextBlock{Margin=new(0,8,0,0),TextWrapping=TextWrapping.Wrap,Foreground=Brush("#CDB6F5")};status.SetBinding(TextBlock.TextProperty,"CustomerStatus");DockPanel.SetDock(status,Dock.Bottom);main.Children.Add(status);
  var monitor=new WrapPanel();foreach(var item in new[]{("GAME","GameStatus"),("CLIENT","Detection.Client"),("SETTINGS","CustomerConfigurationStatus"),("IN-GAME CONTROLS","Show / Hide Menu · F10")}){var box=new StackPanel{Margin=new(0,0,24,0),MinWidth=130};box.Children.Add(Text(item.Item1,9,false,"#A99ABF"));var value=Text(item.Item2=="Show / Hide Menu · F10"?"F10  ·  Show / Hide Menu":"",13,true,item.Item2=="Show / Hide Menu · F10"?"#DCC8FF":"#F1EFF8");value.MaxWidth=330;if(item.Item2!="Show / Hide Menu · F10")value.SetBinding(TextBlock.TextProperty,item.Item2);monitor.Children.Add(box);box.Children.Add(value);}var monitorCard=Card(monitor);monitorCard.Margin=new(0,0,0,18);DockPanel.SetDock(monitorCard,Dock.Top);main.Children.Add(monitorCard);
  pageScroll.Content=content;main.Children.Add(pageScroll);
   BuildNavigation();ShowPage(0);timer.Tick+=async(_,_)=>await Model.RefreshDetectionAsync();Loaded+=async(_,_)=>{try{await Model.DiscoverAsync();await RefreshMenuStatusAsync();ShowPage(page);await Model.RefreshDetectionAsync();await RefreshMenuStatusAsync();if(model==null && Model.NeedsRegistryInitialization())PromptRegistryInitialization();timer.Start();}finally{initialLoadCompletion.TrySetResult();}};Activated+=async(_,_)=>await RefreshMenuStatusAsync();Closed+=(_,_)=>{timer.Stop();menuRefresh?.Cancel();menuRefresh?.Dispose();Model.Dispose();};
 }
 protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
 {
  base.OnClosing(e);Diagnostics.Log("window-lifecycle",$"main-window-closing; window={windowId}; source={(closeOrigin=="window-close-or-external"?App.ShutdownOrigin:closeOrigin)}; cancelled={e.Cancel}");
 }
 protected override void OnClosed(EventArgs e)
 {
  Diagnostics.Log("window-lifecycle",$"main-window-closed; window={windowId}; source={(closeOrigin=="window-close-or-external"?App.ShutdownOrigin:closeOrigin)}");base.OnClosed(e);
 }
 private void PromptRegistryInitialization()
 {
  var prompt=new Window{Title="Hexor settings",Owner=this,Width=460,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#161620"),Foreground=Brushes.White};
  var panel=new StackPanel{Margin=new Thickness(24)};
  panel.Children.Add(new TextBlock{Text="Check supported Legion Commander settings and add only missing values? Existing customer preferences will be preserved.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18)});
  var initialize=new Button{Content="Sync Legion Commander Settings",IsDefault=true,Margin=new Thickness(0,4,0,4)};
  initialize.Click+=(_,_)=>{prompt.DialogResult=true;};panel.Children.Add(initialize);
  var later=new Button{Content="Not now",IsCancel=true,Margin=new Thickness(0,4,0,4)};panel.Children.Add(later);prompt.Content=panel;
  if(prompt.ShowDialog()==true)
  {
   var result=Model.InitializeRegistryDefaults();ShowPage(page);
   MessageBox.Show(this,result.Message,"Hexor settings",MessageBoxButton.OK,result.Success?MessageBoxImage.Information:MessageBoxImage.Warning);
  }
 }
 private static Image? BrandImage(string relativePath,double width,double height)
 {
  var uri=new Uri("pack://application:,,,/Hexor-AION-Control-Center;component/"+relativePath,UriKind.Absolute);
  try {return new Image{Source=BitmapFrame.Create(uri),Width=width,Height=height,Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Left};}
  catch(System.IO.IOException){return null;}
 }
 private static SolidColorBrush Brush(string hex)=>(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
 private static TextBlock Text(string text,int size=14,bool bold=false,string color="#F1EFF8")=>new(){Text=text,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Foreground=Brush(color),Margin=new(0,3,0,5)};
 private static Border Card(UIElement child)=>new(){Background=Brush("#181421"),CornerRadius=new(16),Padding=new(22),Margin=new(0,0,0,14),Child=child,Effect=new System.Windows.Media.Effects.DropShadowEffect{Color=Color.FromRgb(0,0,0),Opacity=.25,BlurRadius=16,ShadowDepth=5}};
 private StackPanel Section(StackPanel parent,string heading,string? note=null){var panel=new StackPanel();panel.Children.Add(Text(T(heading),18,true));if(note!=null)panel.Children.Add(Text(T(note),12,false,"#AEA5C0"));parent.Children.Add(Card(panel));return panel;}
 private string T(string s)=>Translations.Get(Model.Preferences.UiLanguage,s);
 private Button Button(string label,Action action){var b=new Button{Content=T(label),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=(_,_)=>action();return b;}
 private TextBox Input(Panel panel,string label,string path){panel.Children.Add(Text(T(label),12,false,"#BAB2CC"));var box=new TextBox{Width=320,MaxWidth=320,HorizontalAlignment=HorizontalAlignment.Left};box.SetBinding(TextBox.TextProperty,new Binding(path){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});panel.Children.Add(box);return box;}
 private CheckBox Check(Panel panel,string label,string path){var box=new CheckBox{Content=T(label)};box.SetBinding(CheckBox.IsCheckedProperty,new Binding(path){Mode=BindingMode.TwoWay});panel.Children.Add(box);return box;}
 private void BuildNavigation(){navigation.Children.Clear();for(var i=0;i<pages.Length;i++){var index=i;var b=Button(T(pages[i]),()=>ShowPage(index));var navText=Text(T(pages[i]),13,i==page,i==page?"#FFFFFF":"#C3B8D2");navText.TextWrapping=TextWrapping.NoWrap;b.Content=navText;b.Height=46;b.HorizontalAlignment=HorizontalAlignment.Stretch;b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new(14,3,14,3);b.Padding=new Thickness(16,0,10,0);b.Background=Brush(i==page?"#51377B":"#100D19");b.Name="Navigation"+i;AutomationProperties.SetName(b,T(pages[i]));navigation.Children.Add(b);}}
  public void ShowPage(int index){if(index<0||index>=pages.Length)throw new ArgumentOutOfRangeException(nameof(index));page=index;title.Text=T(pages[index]);BuildNavigation();if(index>0)Model.ReloadRegistry();var stack=new StackPanel();content.Content=stack;switch(index){case 0:General(stack);_ = RefreshMenuStatusAsync();break;case 1:AutoTask(stack);break;case 2:Enhance(stack);break;case 3:Festival(stack);break;}pageScroll.ScrollToTop();}
 private void General(StackPanel root)
 {
  root.Children.Add(Card(new Hexor.Updates.UpdatePanel(automaticallyCheckForUpdates)));
  var p=Section(root,"Language preferences");
  var uiRow=new Grid{Margin=new Thickness(0,4,0,8)};uiRow.ColumnDefinitions.Add(new());uiRow.ColumnDefinitions.Add(new(){Width=new GridLength(240)});p.Children.Add(uiRow);uiRow.Children.Add(Text(T("Control Center Language")));var ui=new ComboBox{ItemsSource=new[]{"English","简体中文","한국어"},SelectedItem=Model.Preferences.UiLanguage,HorizontalAlignment=HorizontalAlignment.Left};Grid.SetColumn(ui,1);uiRow.Children.Add(ui);ui.SelectionChanged+=(_,_)=>{if(ui.SelectedItem is string language){var previous=Model.Preferences.UiLanguage;new LanguageSettingsService().SetUi(Model.Preferences,language);if(!Model.Save())Model.Preferences.UiLanguage=previous;ShowPage(0);}};
  var gameRow=new Grid{Margin=new Thickness(0,4,0,0)};gameRow.ColumnDefinitions.Add(new());gameRow.ColumnDefinitions.Add(new(){Width=new GridLength(240)});p.Children.Add(gameRow);gameRow.Children.Add(Text(T("Preferred Game Language")));var game=new ComboBox{ItemsSource=new[]{"English","Traditional Chinese","Korean"},SelectedItem=Model.Preferences.GameLanguage,HorizontalAlignment=HorizontalAlignment.Left};Grid.SetColumn(game,1);gameRow.Children.Add(game);game.SelectionChanged+=(_,_)=>{if(game.SelectedItem is string language){Model.SetGameLanguage(language);}};

   var cleanup=Section(root,"Hexor file cleanup","Removes path-matched automation DLLs and verified updater-owned data. Hexor Control Center remains installed; use Windows Settings to uninstall it.");
   var clean=Button("Clean Up Hexor Files",async()=>await RunCleanupAsync());clean.Name="CleanUpHexorFilesButton";clean.Background=Brush("#6942A8");cleanup.Children.Add(clean);
   var menu=Section(root,"Menu status","File detection reports whether the verified menu DLL is installed. It does not claim the DLL is loaded into AION2.");
   var menuRow=new WrapPanel{Orientation=Orientation.Horizontal};var unload=Button("Unload Menu",async()=>await UnloadMenuAsync());unload.Name="UnloadMenuButton";unload.Background=Brush("#6942A8");menuRow.Children.Add(unload);menuStatusText=Text(menuStatus.Text,13,true,"#DCC8FF");menuStatusText.Name="MenuStatusText";menuStatusText.Margin=new Thickness(16,8,0,5);menuRow.Children.Add(menuStatusText);menu.Children.Add(menuRow);
   var diagnosticSupport=Section(root,"Diagnostics","Local support logs contain operational checkpoints only. Review the ZIP before sharing; nothing is uploaded automatically.");
   diagnosticSupport.Children.Add(Text("Active log: hexor.log",12,true,"#DCC8FF"));
   diagnosticSupport.Children.Add(Text($"Current size: {Diagnostics.ActiveLogSize/1024d/1024d:F2} MB",12,false,"#B9ADCA"));
   diagnosticSupport.Children.Add(Button("Open Log",()=>OpenPath(Diagnostics.ActiveLogPath)));
   diagnosticSupport.Children.Add(Button("Open Logs Folder",()=>OpenPath(Diagnostics.Root)));
   diagnosticSupport.Children.Add(Button("Copy Log Path",()=>{Clipboard.SetText(Diagnostics.ActiveLogPath);MessageBox.Show(this,"Log path copied.","Diagnostics",MessageBoxButton.OK,MessageBoxImage.Information);}));
  diagnosticSupport.Children.Add(Button("Copy Support ID",()=>{Clipboard.SetText(Diagnostics.SessionId);MessageBox.Show(this,"Support ID copied.","Diagnostics",MessageBoxButton.OK,MessageBoxImage.Information);}));
  diagnosticSupport.Children.Add(Button("Export Support Logs",()=>{var path=Diagnostics.ExportSupportLogs();if(string.IsNullOrEmpty(path)){MessageBox.Show(this,"Support log export could not be created.","Diagnostics",MessageBoxButton.OK,MessageBoxImage.Warning);return;}new SupportLogExportDialog(path){Owner=this}.ShowDialog();}));
  diagnosticSupport.Children.Add(Button("Open Export Folder",()=>{var path=Diagnostics.LastExportPath;if(!string.IsNullOrEmpty(path))SupportLogExportActions.OpenFolder(path);else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Diagnostics.Root){UseShellExecute=true});}));
  var update=Section(root,"UPDATE STATUS");var updateText=Text("",14,true,"#DCC8FF");updateText.SetBinding(TextBlock.TextProperty,"UpdateStatus");update.Children.Add(updateText);update.Children.Add(Text("Run the AION updater again whenever AION2 receives a game update.",12,false,"#B9ADCA"));
  var support=Section(root,"HEXOR SUPPORT & COMMUNITY");support.Children.Add(Text("Discord Server · discord.gg/pE6mZG7MJ\nDiscord Username · h3x0r1337",13,false,"#DCC8FF"));support.Children.Add(Button("Join Discord",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://discord.gg/pE6mZG7MJ"){UseShellExecute=true})));support.Children.Add(Button("Copy Username",()=>{Clipboard.SetText("h3x0r1337");MessageBox.Show(this,"Discord username copied.","Hexor Support",MessageBoxButton.OK,MessageBoxImage.Information);}));
  var next=Section(root,"NEXT STEPS");next.Children.Add(Text("1. Open AION2.\n2. Wait for the game loader to download the required files.\n3. Open Config and ensure Auto Auction is disabled.",13));

  var advanced=new StackPanel();
  advanced.Children.Add(Button("Sync Legion Commander Settings",PromptRegistryInitialization));
  var maintenance=Section(advanced,"Advanced cleanup diagnostics","Manual review is optional. Customer cleanup uses the automatic workflow above.");
  maintenance.Children.Add(Button("Review detected AION2 installations",ReviewAionInstallations));
  var discovery=Section(advanced,"Settings folder","Choose your existing settings folder. Changes are saved only when you edit or apply a setting.");discovery.Children.Add(Button("Find settings",async()=>{await Model.DiscoverAsync();await RefreshMenuStatusAsync();ShowPage(0);}));discovery.Children.Add(Button("Choose settings folder",()=>{var dialog=new OpenFolderDialog{Title="Select existing settings folder"};if(dialog.ShowDialog(this)==true){Model.ConfirmConfigurationFolder(dialog.FolderName);ShowPage(0);}}));
  var connected=Text("",12);connected.SetBinding(TextBlock.TextProperty,"DiagnosticsDataRoot");discovery.Children.Add(connected);discovery.Children.Add(Button("Reload settings",()=>{Model.ReloadConfiguration();ShowPage(0);}));
  foreach(var candidate in Model.Candidates.Where(c=>new Hexor.Compatibility.ConfigurationInspector().Inspect(c.Folder).HasEvidence))
  {
   var folder=candidate.Folder;
   discovery.Children.Add(Button("Use settings: "+folder,()=>{Model.ConfirmConfigurationFolder(folder);ShowPage(0);}));
  }
  var original=Section(advanced,"Application language");
  var culture=new ComboBox{ItemsSource=new[]{"zh-CN","en-US","ko-KR"},HorizontalAlignment=HorizontalAlignment.Left};culture.SetBinding(ComboBox.SelectedItemProperty,new Binding("OriginalUiCulture"){Mode=BindingMode.TwoWay});original.Children.Add(Text("LegionCommander interface language"));original.Children.Add(culture);original.Children.Add(Button("Save language preference",Model.SaveOriginalUiLanguage));
  var detectedPath=Section(advanced,"Detected game location","Review and confirm the running game location before saving.");
  var observedPath=Text("",12);observedPath.SetBinding(TextBlock.TextProperty,"Detection.ExecutablePath");detectedPath.Children.Add(observedPath);
  var pathTarget=new ComboBox{ItemsSource=new[]{"Global / Steam","Taiwan","Korea"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Left};detectedPath.Children.Add(pathTarget);
  detectedPath.Children.Add(Button("Review detected path…",async()=>{
   await Model.RefreshDetectionAsync();
   var key=new[]{"GamePath_Global","GamePath_TW","GamePath_KR"}[pathTarget.SelectedIndex];if(Model.ProposeDetectedPath(key) is not { } proposal)return;
   var previous=proposal.Expected.Value(key)??"(not configured)";
   if(MessageBox.Show(this,$"Save the detected game location for {pathTarget.SelectedItem}?\n\nCurrent: {previous}\nDetected: {proposal.ExecutablePath}\nPID: {proposal.ProcessId}\n\nA backup of the existing settings will be created.","Confirm game location",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
   await Model.RefreshDetectionAsync();Model.ConfirmDetectedPath(proposal);
  }));
  var diagnosticPanel=new StackPanel();
  foreach(var field in new[]{("Observed at","DiagnosticsObservedAt"),("AION2 processes / executable paths","DiagnosticsProcess"),("Resolved configuration data root","DiagnosticsDataRoot"),("Configuration files","DiagnosticsFiles"),("Configuration installation-path association","DiagnosticsAssociation"),("Discovery decisions","DiagnosticsCandidates"),("Setting details","DiagnosticsSettingDetails"),("Detection errors","DiagnosticsErrors")}){diagnosticPanel.Children.Add(Text(field.Item1,12,true,"#CDB6F5"));var data=Text("",12);data.SetBinding(TextBlock.TextProperty,field.Item2);diagnosticPanel.Children.Add(data);}
  var operation=Text("",12);operation.SetBinding(TextBlock.TextProperty,"Status");diagnosticPanel.Children.Add(operation);
  diagnosticPanel.Children.Add(Text("Saved preferences are not confirmation that the running automation has applied them. Some preferences take effect when a task or the game next starts. Live control is not available in this release.",12));
  diagnosticPanel.Children.Add(Text("Auto Task settings are stored in the existing configuration and registry. If a save is only partially completed, use the retry action below. No in-process refresh is sent.",12));
  diagnosticPanel.Children.Add(Button("Retry task settings synchronization",Model.RetryAutoTaskMirror));
  diagnosticPanel.Children.Add(Button("Refresh diagnostics",async()=>await Model.RefreshDetectionAsync()));
  diagnosticPanel.Children.Add(Text("Active log: hexor.log",12,true,"#DCC8FF"));
  diagnosticPanel.Children.Add(Button("Open Log",()=>OpenPath(Diagnostics.ActiveLogPath)));
  diagnosticPanel.Children.Add(Button("Open Logs Folder",()=>OpenPath(Diagnostics.Root)));
  diagnosticPanel.Children.Add(Button("Copy Log Path",()=>Clipboard.SetText(Diagnostics.ActiveLogPath)));
  advanced.Children.Add(diagnosticPanel);
  var diagnostics=new Expander{Name="DiagnosticsExpander",Header="Advanced settings & diagnostics",IsExpanded=false,Foreground=Brushes.White,Content=advanced};root.Children.Add(Card(diagnostics));
 }
  private static void OpenPath(string path){try{if(path==Diagnostics.ActiveLogPath&&!System.IO.File.Exists(path))Diagnostics.Log("diagnostics","active-log-created");System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});}catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){Diagnostics.Log("diagnostics","open-failed; type="+e.GetType().Name);}}
  private string MenuManifestPath(){var parent=System.IO.Directory.GetParent(AppContext.BaseDirectory)?.FullName;return parent==null?"":System.IO.Path.Combine(parent,"AION Auto Updater",VendorOverlayCleanupService.ManifestName);}
  private IReadOnlyList<AionInstallation> MenuInstallations()
  {
   var discovery=new AionInstallationDiscoveryService();var found=discovery.DiscoverFromSteamLibraries(discovery.ReadSteamLibraries()).ToList();
   if(Model.Detection.ExecutablePath is { } executable){var directory=System.IO.Path.GetDirectoryName(executable);for(var i=0;i<3&&directory!=null;i++)directory=System.IO.Path.GetDirectoryName(directory);if(directory!=null&&discovery.VerifyUserSelectedInstallation(directory) is { } selected)found.Add(selected);}
   return found.DistinctBy(x=>x.InstallationDirectory,StringComparer.OrdinalIgnoreCase).ToArray();
  }
  internal async Task RefreshMenuStatusAsync()
  {
   var next=new CancellationTokenSource();var previous=Interlocked.Exchange(ref menuRefresh,next);previous?.Cancel();previous?.Dispose();
   menuStatus=new(MenuStatusState.Checking,"Checking — installation discovery in progress");if(menuStatusText!=null)menuStatusText.Text=menuStatus.Text;
   try{var work=Task.Run(()=>menuStatusService.Check(MenuInstallations(),MenuManifestPath(),next.Token),next.Token);var result=await work.WaitAsync(TimeSpan.FromSeconds(3),next.Token);if(!next.IsCancellationRequested&&ReferenceEquals(menuRefresh,next)){menuStatus=result;if(menuStatusText!=null)menuStatusText.Text=result.Text;Diagnostics.Log("menu-status",$"state={result.State}");}}
   catch(TimeoutException){if(ReferenceEquals(menuRefresh,next)){menuStatus=new(MenuStatusState.Error,"Error — menu status could not be determined.");if(menuStatusText!=null)menuStatusText.Text=menuStatus.Text;}Diagnostics.Log("menu-status","status-check-timeout; deadline-ms=3000");}
   catch(OperationCanceledException){}
   catch(Exception e){menuStatus=new(MenuStatusState.Error,"Error — menu status could not be determined.");if(menuStatusText!=null)menuStatusText.Text=menuStatus.Text;Diagnostics.Log("menu-status","status-check-failed; type="+e.GetType().Name);}
  }
  private async Task UnloadMenuAsync()
  {
   await RefreshMenuStatusAsync();
   if(menuStatus.State==MenuStatusState.GameRunning){MessageBox.Show(this,"Close AION2 before unloading the menu.","Unload Menu",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
   if(menuStatus.State!=MenuStatusState.MenuDetected){MessageBox.Show(this,menuStatus.Text,"Unload Menu",MessageBoxButton.OK,MessageBoxImage.Information);return;}
   if(MessageBox.Show(this,$"Remove the verified menu file?\n\nTarget:\n{menuStatus.TargetPath}\n\nOnly this installation-scoped dxgi.dll will be changed.","Unload Menu",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
   var result=await Task.Run(()=>menuStatusService.Unload(menuStatus,MenuManifestPath()));Diagnostics.Log("menu-unload",$"state={result.State}; target={result.TargetPath}");await RefreshMenuStatusAsync();MessageBox.Show(this,result.Message,"Unload Menu",MessageBoxButton.OK,result.State is VendorOverlayCleanupState.Removed or VendorOverlayCleanupState.Restored?MessageBoxImage.Information:MessageBoxImage.Warning);
   if(result.State is VendorOverlayCleanupState.Removed or VendorOverlayCleanupState.Restored)await RefreshMenuStatusAsync();
  }
 internal static void LogCleanupCompleted(IReadOnlyList<CleanupPlanItem> results)
 {
  var removed=results.Count(x=>x.State is VendorOverlayCleanupState.Removed or VendorOverlayCleanupState.Restored);
  var failed=results.Count(x=>x.State is VendorOverlayCleanupState.Failed or VendorOverlayCleanupState.BlockedByRunningProcess);
  Diagnostics.Log("cleanup","completed; removed="+removed+"; failed="+failed,failed>0?DiagnosticSeverity.Error:DiagnosticSeverity.Information);
 }
 private async Task RunCleanupAsync()
 {
  var progress=Text("Finding installations",12,false,"#CDB6F5");
  try { var workflow=new CleanupWorkflow(); var plan=await workflow.PrepareUnifiedAsync(stage=>Dispatcher.Invoke(()=>progress.Text=stage));
   var dlls=plan.Items.Count(x=>x.Kind==CleanupItemKind.PathMatchedDxgi&&x.ExpectedFingerprint!=null);var data=plan.Items.Count(x=>x.Kind is CleanupItemKind.UpdaterData or CleanupItemKind.UpdaterRegistry);
    if(MessageBox.Show(this,$"AION2 installations: {dlls}\nAutomation DLLs: {dlls}\nUpdater settings, language, license, authorization, cache, and temporary data: {data}\n\nUpdater binaries and Hexor Control Center are preserved. This permanently erases the listed updater data. Confirm cleanup?","Clean Up Hexor Files",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
   var results=await workflow.ExecuteAsync(plan,stage=>Dispatcher.Invoke(()=>progress.Text=stage));LogCleanupCompleted(results);var changed=results.Count(x=>x.State is VendorOverlayCleanupState.Removed or VendorOverlayCleanupState.Restored);var blocked=results.Count(x=>x.State==VendorOverlayCleanupState.BlockedByRunningProcess);
     var failed=results.Count(x=>x.State is VendorOverlayCleanupState.Failed or VendorOverlayCleanupState.BlockedByRunningProcess);MessageBox.Show(this,$"AION2 DLLs removed: {results.Count(x=>x.Kind==CleanupItemKind.PathMatchedDxgi&&x.State==VendorOverlayCleanupState.Removed)}\nUpdater data removed: {results.Count(x=>(x.Kind is CleanupItemKind.UpdaterData or CleanupItemKind.UpdaterRegistry)&&x.State==VendorOverlayCleanupState.Removed)}\nSkipped: {results.Count(x=>x.State is VendorOverlayCleanupState.Skipped or VendorOverlayCleanupState.CouldNotVerify)}\nFailed: {failed}","Clean Up Hexor Files",MessageBoxButton.OK,(blocked>0||failed>0)?MessageBoxImage.Warning:MessageBoxImage.Information);
   } catch(OperationCanceledException) { MessageBox.Show(this,"Cleanup was cancelled before changes were made.","Clean Up AION Updater",MessageBoxButton.OK,MessageBoxImage.Information); }
     catch(Exception e) { MessageBox.Show(this,"Cleanup could not be prepared: "+e.GetType().Name,"Clean Up AION Updater",MessageBoxButton.OK,MessageBoxImage.Warning); }
 }
  private async Task RunCacheResetAsync()
  {
   try { var workflow=new CleanupWorkflow(); var plan=await workflow.PrepareCacheResetAsync(); var candidates=plan.Items.Where(x=>x.State==VendorOverlayCleanupState.Skipped&&!x.Message.StartsWith("Verified updater cache is already",StringComparison.Ordinal)).Count();
    if(candidates==0){MessageBox.Show(this,"No safe updater download-cache files are available to reset. Game files and unrelated application data were not included.","Reset Updater Download Cache",MessageBoxButton.OK,MessageBoxImage.Information);return;}
    if(MessageBox.Show(this,$"This explicitly resets {candidates} updater download-cache file(s). They may need to be downloaded again. AION2 and the updater must be closed. Game files, licenses, credentials, and unrelated data are not included.\n\nContinue?","Reset Updater Download Cache",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
    var results=await workflow.ExecuteAsync(plan);var removed=results.Count(x=>x.State==VendorOverlayCleanupState.Removed);var failed=results.Count(x=>x.State is VendorOverlayCleanupState.Failed or VendorOverlayCleanupState.BlockedByRunningProcess);MessageBox.Show(this,$"Removed: {removed}; skipped or failed: {results.Count-removed}."+(failed>0?" Close AION2 and the updater, then retry.":""),"Reset Updater Download Cache",MessageBoxButton.OK,failed>0?MessageBoxImage.Warning:MessageBoxImage.Information);
   }catch(Exception e){MessageBox.Show(this,"Cache reset could not be prepared: "+e.GetType().Name,"Reset Updater Download Cache",MessageBoxButton.OK,MessageBoxImage.Warning);}
  }
 private void ReviewAionInstallations()
 {
  var discovery=new AionInstallationDiscoveryService();var found=discovery.DiscoverFromSteamLibraries(discovery.ReadSteamLibraries()).ToList();
  var dialog=new OpenFolderDialog{Title="Optionally select another verified AION2 installation"};
  if(dialog.ShowDialog(this)==true){var selected=discovery.VerifyUserSelectedInstallation(dialog.FolderName);if(selected==null){MessageBox.Show(this,"The selected folder does not contain Aion2\\Binaries\\Win64\\Aion2.exe. No installation was added.","AION2 installation review",MessageBoxButton.OK,MessageBoxImage.Information);return;}found.Add(selected);}
  var report=found.Count==0?"No verified AION2 installations were found. Steam discovery uses installed library metadata only; other clients must be selected explicitly.":string.Join("\n\n",found.DistinctBy(x=>x.InstallationDirectory,StringComparer.OrdinalIgnoreCase).Select(x=>$"{x.Client}\n{x.InstallationDirectory}\n{x.Evidence}\nOverlay candidate: {x.OverlayPath}"));
  MessageBox.Show(this,report,"AION2 installation review",MessageBoxButton.OK,MessageBoxImage.Information);
 }
 private void ReviewUpdaterCache()
 {
  MessageBox.Show(this,"No updater cache file has verified disposable ownership evidence in the reviewed trace, so no cache file was removed. Authorization state, game files, and Windows records remain untouched.","AION Updater cache",MessageBoxButton.OK,MessageBoxImage.Information);
 }
 private void ReviewVendorOverlayCleanup()
 {
  var executable=Model.Detection.ExecutablePath;
  var root=System.IO.Directory.GetParent(AppContext.BaseDirectory)?.FullName;
  var manifest=root==null?"":System.IO.Path.Combine(root,"AION Auto Updater",VendorOverlayCleanupService.ManifestName);
  var service=new VendorOverlayCleanupService();var inspection=service.Inspect(executable??"",manifest);
  if(inspection.State!=VendorOverlayCleanupState.Skipped||!inspection.Message.StartsWith("Verified",StringComparison.Ordinal)) {MessageBox.Show(this,inspection.Message,"Optional AION overlay cleanup",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  if(MessageBox.Show(this,$"{inspection.Message}\n\nTarget:\n{inspection.TargetPath}\n\nOnly this exact verified file will be changed. Continue?","Optional AION overlay cleanup",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
  var result=service.Execute(executable!,manifest);MessageBox.Show(this,result.Message+"\n\nTarget:\n"+result.TargetPath,"Optional AION overlay cleanup",MessageBoxButton.OK,result.State is VendorOverlayCleanupState.Removed or VendorOverlayCleanupState.Restored?MessageBoxImage.Information:MessageBoxImage.Warning);
 }
 private void StartUninstall()
 {
  var location=UninstallerLocator.Find(AppContext.BaseDirectory);var uninstaller=location.ExecutablePath;
  if(uninstaller==null){MessageBox.Show(this,location.Message,"Uninstall Hexor",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  if(MessageBox.Show(this,"Start the AIO uninstaller? It removes installed Hexor/AIO files only and does not remove game files, authorization state, Windows logs, or execution history.","Uninstall Hexor",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
  try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uninstaller){UseShellExecute=true,WorkingDirectory=System.IO.Path.GetDirectoryName(uninstaller)!});MessageBox.Show(this,"The AIO uninstaller was started. Hexor does not remove game files or authorization records.","Uninstall Hexor",MessageBoxButton.OK,MessageBoxImage.Information);}catch(Exception e){MessageBox.Show(this,"The uninstaller could not be started: "+e.GetType().Name,"Uninstall Hexor",MessageBoxButton.OK,MessageBoxImage.Warning);}
 }
 private void RegistryOptions(Panel panel,string section,IEnumerable<string>? names=null){var allowed=names?.ToHashSet(StringComparer.Ordinal);foreach(var toggle in Model.Toggles.Where(t=>t.Section==section&&(allowed==null||allowed.Contains(t.Name)))){var row=new Grid{Margin=new Thickness(0,4,0,4)};row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var label=Text(T(toggle.Label),14,true);label.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(label);var check=new CheckBox{DataContext=toggle,IsThreeState=false,Style=(Style)FindResource("ToggleSwitchStyle")};AutomationProperties.SetName(check,toggle.Label);check.SetBinding(CheckBox.IsCheckedProperty,new Binding("Value"){Mode=BindingMode.TwoWay});check.SetBinding(CheckBox.IsEnabledProperty,"IsEditable");Grid.SetColumn(check,1);row.Children.Add(check);panel.Children.Add(row);var detail=Text("",11,false,"#D6B47F");detail.DataContext=toggle;detail.SetBinding(TextBlock.TextProperty,"CustomerDetail");var detailStyle=new Style(typeof(TextBlock),Application.Current.TryFindResource(typeof(TextBlock)) as Style);var emptyDetail=new DataTrigger{Binding=new Binding("CustomerDetail"),Value=""};emptyDetail.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed));detailStyle.Triggers.Add(emptyDetail);detail.Style=detailStyle;panel.Children.Add(detail);}}
 private void AutoTask(StackPanel root)
 {
  RegistryOptions(Section(root,"Task behavior"),"AutoTask");
  var identity=Section(root,"Character identity","Saved numeric values are preserved. Race and class names are not yet verified.");
  foreach(var item in new[]{("Character race (DWORD)","RegistryCharacterRace"),("Character job (DWORD)","RegistryCharacterJob")})
  {
   var value=Text("",13);value.SetBinding(TextBlock.TextProperty,new Binding(item.Item2){StringFormat=item.Item1+": {0}"});identity.Children.Add(value);
  }
  var resources=Section(root,"Resource Gathering");
  RegistryOptions(resources,"ResourceGrab",["AutoEnterAbyss","AbyssCounterAttack","GuardianLogoutAvoidance"]);
  resources.Children.Add(Text("Abyss layer",13,true));
  var layer=new ComboBox{Name="AbyssLayerSelector",ItemsSource=new[]{"Lower","Middle"},HorizontalAlignment=HorizontalAlignment.Left};
  AutomationProperties.SetName(layer,"Abyss layer");layer.SetBinding(ComboBox.SelectedIndexProperty,new Binding("AbyssLayer"){Mode=BindingMode.TwoWay});resources.Children.Add(layer);
  resources.Children.Add(Text("Preferred dungeon",13,true));
  var dungeon=new ComboBox{Name="PreferredDungeonSelector",ItemsSource=new[]{"Krao Cave","Draupnir Cave"},HorizontalAlignment=HorizontalAlignment.Left};
  AutomationProperties.SetName(dungeon,"Preferred dungeon");dungeon.SetBinding(ComboBox.SelectedIndexProperty,new Binding("PreferredDungeon"){Mode=BindingMode.TwoWay});resources.Children.Add(dungeon);
  var auction=Section(root,"Auto Auction","Each Hexor startup resets auto sell to OFF and price mode to Average.");
  RegistryOptions(auction,"ResourceGrab",["AuctionAutoSell"]);
  auction.Children.Add(Text("Auction price mode",13,true));
  var price=new ComboBox{Name="AuctionPriceModeSelector",ItemsSource=new[]{"Lowest","Average"},HorizontalAlignment=HorizontalAlignment.Left};
  AutomationProperties.SetName(price,"Auction price mode");price.SetBinding(ComboBox.SelectedIndexProperty,new Binding("AuctionPriceMode"){Mode=BindingMode.TwoWay});auction.Children.Add(price);
 }
 private void Enhance(StackPanel root)
 {
  var upgrade=Section(root,"Character Enhancement","Auto Equip / Enchant Combat Power Cap");
  upgrade.Children.Add(Text("Controls AutoUpgradeMaxBattlePowerK independently from the gear enhancement level cap below.",12,false,"#B9ADCA"));
  var selected=Text("",20,true,"#DCC8FF");selected.Name="AutoEquipCombatPowerValue";selected.SetBinding(TextBlock.TextProperty,new Binding("UpgradeMaxBattlePowerK"){StringFormat="{0}K"});upgrade.Children.Add(selected);
  var battleSlider=new Slider{Name="AutoEquipCombatPowerSlider",Minimum=1,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new(0,10,0,8)};AutomationProperties.SetName(battleSlider,"Auto Equip and Enchant Combat Power Cap");battleSlider.SetBinding(Slider.ValueProperty,new Binding("UpgradeMaxBattlePowerK"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});upgrade.Children.Add(battleSlider);
  var battleCurrent=Text("",12,false,"#B9ADCA");battleCurrent.SetBinding(TextBlock.TextProperty,new Binding("CurrentUpgradeMaxBattlePowerK"){StringFormat="Saved registry value: {0}K"});upgrade.Children.Add(battleCurrent);upgrade.Children.Add(Button("Apply combat power cap",Model.SaveUpgradeMaxBattlePowerK));

  var extraction=Section(root,"Equipment Extraction","When the bag contains 100,000 or more Enhancement Stones, equipment that would normally be extracted is destroyed instead. Below 100,000, extraction continues normally.");
  RegistryOptions(extraction,"Enhance",["DestroyInsteadOfExtract"]);

  RegistryOptions(Section(root,"Membership Benefits","Verified independent REG_DWORD settings under LegionCommander\\Enhance."),"Enhance",["AutoBuyOdEnergy","AutoBuyReviveStone"]);
  RegistryOptions(Section(root,"Auto Dungeon settings"),"Enhance",["AutoDungeonQuickJoinEnabled","AutoDailyWaveEnabled","AutoNightmareEnabled","AutoAwakenEnabled","AutoOdMorphEnabled"]);

  var gear=Section(root,"Gear enhancement level","AutoEnchantMaxPower remains a separate setting. Choose a value from 1100 to 1500.");var current=Text("",13);current.SetBinding(TextBlock.TextProperty,new Binding("CurrentCap"){StringFormat="Saved registry value: {0}"});gear.Children.Add(current);var input=Input(gear,"Gear enhancement level cap","EnhanceCap");var slider=new Slider{Name="AutoEnchantMaxPowerSlider",Minimum=1100,Maximum=1500,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new(0,10,0,16)};slider.SetBinding(Slider.ValueProperty,new Binding("EnhanceCap"){Mode=BindingMode.OneWay});slider.PreviewMouseLeftButtonUp+=(_,_)=>{Model.EnhanceCap=((int)slider.Value).ToString();input.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();};slider.PreviewKeyUp+=(_,_)=>{Model.EnhanceCap=((int)slider.Value).ToString();input.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();};gear.Children.Add(slider);gear.Children.Add(Button("Apply gear enhancement cap",Model.SaveCap));
 }
 private void Festival(StackPanel root){RegistryOptions(Section(root,"Shugo Festival"),"MiniGame");}
}

