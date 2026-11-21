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
 private int page;private readonly string[] pages=["General","Resource Grab","Auto Dungeon","Auto Task","Character Enhancement","Daily Activities","Work Schedule","Shugo Festival","CAPTCHA"];// "Live Control" hidden for now: no customer use yet; the page code stays below.
 private readonly string windowId=Guid.NewGuid().ToString("N")[..8];
 private string closeOrigin="window-close-or-external";
 private readonly TextBlock title=new(){FontSize=30,FontWeight=FontWeights.SemiBold,Focusable=true};

 private readonly MenuStatusService menuStatusService=new();
 private MenuStatusResult menuStatus=new(MenuStatusState.Checking,"Checking — installation discovery in progress");
 private TextBlock? menuStatusText;
 private CancellationTokenSource? menuRefresh;
 private readonly TaskCompletionSource initialLoadCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);
 internal Task InitialLoadCompleted=>initialLoadCompletion.Task;
 private readonly bool automaticallyCheckForUpdates;
 private readonly bool ownsModel;
 public MainWindow(MainViewModel? model=null, bool automaticallyCheckForUpdates=true, bool ownsModel=true)
 {
  this.automaticallyCheckForUpdates=automaticallyCheckForUpdates;this.ownsModel=ownsModel;
  Model=model??new();Translations.CurrentLanguage=Model.Preferences.UiLanguage;DataContext=Model;Title="HEXOR AION CONTROL CENTER";Width=Math.Min(1200,SystemParameters.WorkArea.Width);Height=Math.Min(800,SystemParameters.WorkArea.Height);MinWidth=920;MinHeight=620;ResizeMode=ResizeMode.CanResize;UseLayoutRounding=true;SnapsToDevicePixels=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brush("#0B0912");Foreground=Brushes.White;FontFamily=new("Segoe UI Variable");FontSize=14;
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
  var footer=new StackPanel{Margin=new(18,12,18,22)};var updaterButton=Button("Open AION Updater",async()=>await Model.OpenAionUpdaterAsync());updaterButton.Name="OpenAionUpdaterButton";updaterButton.HorizontalAlignment=HorizontalAlignment.Stretch;updaterButton.Background=Brush("#7849B5");updaterButton.Style=(Style)FindResource("AccentButtonStyle");updaterButton.ToolTip="Open the original AION updater";footer.Children.Add(updaterButton);
  var updaterStatus=Text("",12,false,"#B9ADCA");updaterStatus.Name="AionUpdaterStatus";updaterStatus.SetBinding(TextBlock.TextProperty,nameof(MainViewModel.UpdateStatus));AutomationProperties.SetLiveSetting(updaterStatus,AutomationLiveSetting.Polite);footer.Children.Add(updaterStatus);
  DockPanel.SetDock(footer,Dock.Bottom);sidebar.Children.Add(footer);
  sidebar.Children.Add(new ScrollViewer{Content=navigation,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
  var main=new DockPanel{Margin=new(32,26,32,20)};Grid.SetColumn(main,1);root.Children.Add(main);
  title.SetValue(KeyboardNavigation.IsTabStopProperty, false);
  AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
  var head=new StackPanel();head.Children.Add(title);head.Margin=new(0,0,0,14);DockPanel.SetDock(head,Dock.Top);main.Children.Add(head);
  var statusWrap=new Border{BorderBrush=Brush("#241D31"),BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,10,0,0),Margin=new Thickness(0,10,0,0)};var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=Brush("#B9A7D6"),FontSize=12.5};status.SetBinding(TextBlock.TextProperty,"CustomerStatus");statusWrap.Child=status;DockPanel.SetDock(statusWrap,Dock.Bottom);main.Children.Add(statusWrap);
  var monitor=new WrapPanel();foreach(var item in new[]{("GAME","GameStatus"),("CLIENT","Detection.Client"),("SETTINGS","CustomerConfigurationStatus"),("CAPTCHA","CaptchaStatusText"),("IN-GAME CONTROLS","Show / Hide Menu · F10")}){var box=new StackPanel{Margin=new(0,0,24,0),MinWidth=130};box.Children.Add(Text(item.Item1,9,false,"#A99ABF"));var value=Text(item.Item2=="Show / Hide Menu · F10"?"F10  ·  Show / Hide Menu":"",13,true,item.Item2=="Show / Hide Menu · F10"?"#DCC8FF":"#F1EFF8");value.MaxWidth=330;if(item.Item1=="CAPTCHA")value.Name="CaptchaStatusTile";if(item.Item2!="Show / Hide Menu · F10")value.SetBinding(TextBlock.TextProperty,new Binding(item.Item2){Converter=new LocalizeConverter()});monitor.Children.Add(box);box.Children.Add(value);}var monitorCard=Card(monitor);monitorCard.Margin=new(0,0,0,18);DockPanel.SetDock(monitorCard,Dock.Top);main.Children.Add(monitorCard);
  pageScroll.Content=content;main.Children.Add(pageScroll);
   BuildNavigation();ShowPage(0);timer.Tick+=async(_,_)=>{await Model.RefreshDetectionAsync();await Model.PollCaptchaAsync();};Loaded+=async(_,_)=>{try{await Model.DiscoverAsync();await RefreshMenuStatusAsync();ShowPage(page);await Model.RefreshDetectionAsync();await Model.PollCaptchaAsync();await RefreshMenuStatusAsync();if(model==null && Model.NeedsRegistryInitialization())PromptRegistryInitialization();timer.Start();}finally{initialLoadCompletion.TrySetResult();}};Activated+=async(_,_)=>await RefreshMenuStatusAsync();Closed+=(_,_)=>{timer.Stop();menuRefresh?.Cancel();menuRefresh?.Dispose();if(ownsModel)Model.Dispose();};
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
 private static TextBlock Text(string text,int size=14,bool bold=false,string color="#F1EFF8")=>new(){Text=Translations.Localize(text),FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Foreground=Brush(color),Margin=new(0,3,0,5)};
 private static Border Card(UIElement child)=>new(){Background=Brush("#181421"),CornerRadius=new(16),Padding=new(22),Margin=new(0,0,0,14),Child=child,Effect=new System.Windows.Media.Effects.DropShadowEffect{Color=Color.FromRgb(0,0,0),Opacity=.25,BlurRadius=16,ShadowDepth=5}};
 private StackPanel Section(StackPanel parent,string heading,string? note=null){var panel=new StackPanel();panel.Children.Add(Text(T(heading),18,true));if(note!=null)panel.Children.Add(Text(T(note),12,false,"#AEA5C0"));parent.Children.Add(Card(panel));return panel;}
 private string T(string s)=>Translations.Get(Model.Preferences.UiLanguage,s);
 private Button Button(string label,Action action){var b=new Button{Content=T(label),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=(_,_)=>action();return b;}
 private Button AccentButton(string label,Action action){var b=Button(label,action);b.Style=(Style)FindResource("AccentButtonStyle");return b;}
 private Button GhostButton(string label,Action action){var b=Button(label,action);b.Style=(Style)FindResource("GhostButtonStyle");return b;}
 private Button DangerButton(string label,Action action){var b=Button(label,action);b.Style=(Style)FindResource("DangerButtonStyle");return b;}
 private Grid FieldRow(Panel panel,string label,double controlWidth=280){var row=new Grid{Margin=new Thickness(0,4,0,4)};row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=new GridLength(controlWidth)});var l=Text(T(label),14,false,"#D8D0E8");l.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(l);panel.Children.Add(row);return row;}
 private TextBox Input(Panel panel,string label,string path)
 {
  panel.Children.Add(Text(T(label),12,false,"#BAB2CC"));
  var box=new TextBox{Width=220,HorizontalAlignment=HorizontalAlignment.Left};
  AutomationProperties.SetName(box, T(label));
  box.SetBinding(TextBox.TextProperty,new Binding(path){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
  panel.Children.Add(box);
  return box;
 }
 private CheckBox Check(Panel panel,string label,string path){var box=new CheckBox{Content=T(label),HorizontalContentAlignment=HorizontalAlignment.Left,VerticalContentAlignment=VerticalAlignment.Center};box.SetBinding(CheckBox.IsCheckedProperty,new Binding(path){Mode=BindingMode.TwoWay});panel.Children.Add(box);return box;}
 // LegionCommander-style helpers: plain checkbox rows bound to verified registry toggles, and
 // radio groups for verified enum values. All keep the purplish shell.
 private CheckBox CheckToggle(Panel panel,string toggleName,string? labelOverride=null)
 {
  var toggle=Model.Toggles.Single(t=>t.Name==toggleName);
  var box=new CheckBox{Content=T(labelOverride??toggle.Label),VerticalContentAlignment=VerticalAlignment.Center};
  AutomationProperties.SetName(box,T(labelOverride??toggle.Label));
  box.SetBinding(CheckBox.IsCheckedProperty,new Binding("Value"){Mode=BindingMode.TwoWay});
  box.SetBinding(CheckBox.IsEnabledProperty,"IsEditable");
  box.DataContext=toggle;panel.Children.Add(box);return box;
 }
 private static readonly EqualToBoolConverter RadioConverter=new();
 private StackPanel RadioRow(Panel panel,string label,string bindingPath,params (string Name,string Text,object Value)[] options)
 {
  var row=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,4,0,4)};
  var labelText=Text(T(label),13,false,"#BAB2CC");labelText.Margin=new(0,0,14,0);labelText.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(labelText);
  foreach(var option in options)
  {
   var radio=new RadioButton{GroupName="RadioRow-"+label,Content=T(option.Text),Margin=new(0,0,18,0),VerticalContentAlignment=VerticalAlignment.Center,Name=option.Name};
   AutomationProperties.SetName(radio,option.Text);
   radio.SetBinding(RadioButton.IsCheckedProperty,new Binding(bindingPath){Converter=RadioConverter,ConverterParameter=option.Value,Mode=BindingMode.TwoWay});
   row.Children.Add(radio);
  }
  panel.Children.Add(row);return row;
 }
 private static readonly string[] NavIconPaths=
 {"M2,4.5 L14,4.5 M2,8 L14,8 M2,11.5 L14,11.5 M5.5,2.8 A1.7,1.7 0 1 1 5.49,2.8 M10,6.3 A1.7,1.7 0 1 1 9.99,6.3 M4.5,9.8 A1.7,1.7 0 1 1 4.49,9.8",
  "M8,2.3 A5.7,5.7 0 1 1 7.99,2.3 M8,4.7 A3.3,3.3 0 1 1 7.99,4.7",
  "M3.5,13.5 L3.5,7.5 A4.5,4.5 0 0 1 12.5,7.5 L12.5,13.5 M1.5,13.5 L14.5,13.5",
  "M2.5,3.5 H13.5 V13.5 H2.5 Z M5.2,8.6 L7.4,10.8 L11.2,6.4",
  "M4.5,12.5 L8,9 L11.5,12.5 M4.5,8 L8,4.5 L11.5,8",
  "M3,5.5 H13 V13.5 H3 Z M5.5,3 V6.5 M10.5,3 V6.5 M3,8.3 H13",
  "M8,2.5 A5.5,5.5 0 1 1 7.99,2.5 M8,5.2 L8,8.4 L10.6,9.8",
  "M8,2.5 L9.4,6.6 L13.5,8 L9.4,9.4 L8,13.5 L6.6,9.4 L2.5,8 L6.6,6.6 Z",
  "M8,2 L13.2,4.2 V8.2 C13.2,11.4 10.9,13.4 8,14 C5.1,13.4 2.8,11.4 2.8,8.2 V4.2 Z M5.9,8.1 L7.5,9.7 L10.3,6.4"};
 private FrameworkElement NavIcon(int index,bool active){var p=new System.Windows.Shapes.Path{Data=Geometry.Parse(NavIconPaths[index]),Stroke=Brush(active?"#DCBFFF":"#9A8AB4"),StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Width=16,Height=16,Stretch=Stretch.None,VerticalAlignment=VerticalAlignment.Center};return p;}
 private void BuildNavigation(){navigation.Children.Clear();for(var i=0;i<pages.Length;i++){var index=i;var b=Button(T(pages[i]),()=>{ShowPage(index);Keyboard.Focus(title);});var indicator=new Border{Width=3,Height=18,CornerRadius=new CornerRadius(2),Background=Brush("#9667E7"),Visibility=i==page?Visibility.Visible:Visibility.Collapsed,Margin=new Thickness(0,0,12,0)};var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(indicator);row.Children.Add(NavIcon(i,i==page));var navText=Text(T(pages[i]),13,i==page,i==page?"#FFFFFF":"#C3B8D2");navText.TextWrapping=TextWrapping.NoWrap;navText.Margin=new Thickness(12,0,0,0);row.Children.Add(navText);b.Content=row;b.Height=46;b.HorizontalAlignment=HorizontalAlignment.Stretch;b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new(14,3,14,3);b.Padding=new Thickness(16,0,10,0);b.Background=Brush(i==page?"#4A326E":"#100D19");b.Name="Navigation"+i;AutomationProperties.SetName(b,T(pages[i]));if(i==page)FadeIn(b,120);navigation.Children.Add(b);}}
 private static void FadeIn(UIElement element,double ms=140,double slideY=0){element.Opacity=0;if(SystemParameters.ClientAreaAnimation){var fade=new System.Windows.Media.Animation.DoubleAnimation(0,1,TimeSpan.FromMilliseconds(ms)){EasingFunction=new System.Windows.Media.Animation.CubicEase{EasingMode=System.Windows.Media.Animation.EasingMode.EaseOut}};element.BeginAnimation(UIElement.OpacityProperty,fade);if(slideY>0){var slide=new System.Windows.Media.Animation.ThicknessAnimation{From=new Thickness(0,slideY,0,0),To=new Thickness(0),Duration=TimeSpan.FromMilliseconds(ms),EasingFunction=fade.EasingFunction};element.BeginAnimation(FrameworkElement.MarginProperty,slide);}}else element.Opacity=1;}
  public void ShowPage(int index){if(index<0||index>=pages.Length)throw new ArgumentOutOfRangeException(nameof(index));page=index;title.Text=T(pages[index]);BuildNavigation();if(index>0)Model.ReloadRegistry();var stack=new StackPanel();content.Content=stack;FadeIn(stack,140,8);switch(index){case 0:General(stack);_ = RefreshMenuStatusAsync();break;case 1:ResourceGrab(stack);break;case 2:AutoDungeonPage(stack);break;case 3:AutoTask(stack);break;case 4:Enhance(stack);break;case 5:DailyActivities(stack);break;case 6:WorkSchedule(stack);break;case 7:Festival(stack);break;case 8:CaptchaPage(stack);break;}pageScroll.ScrollToTop();}
 private void LiveControl(StackPanel root)
 {
  Model.LiveStopConfirmation = row => MessageBox.Show(this,$"Stop the daily workflow for session #{row.AccountId} (PID {row.ProcessId})?\n\nThe running automation stops at the next save point. This does not close the game.","Stop session",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
  Model.LiveResolvePrompt = row =>
  {
   var prompt=new Window{Title="Resolve incident",Owner=this,Width=480,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#161620"),Foreground=Brushes.White};
   var panel=new StackPanel{Margin=new Thickness(24)};
   panel.Children.Add(new TextBlock{Text=$"Incident {row.IncidentId} for account #{row.AccountId}\n{row.Summary}",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
   var outcome=new ComboBox{ItemsSource=new[]{"resume","close_only"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Left};
   var outcomeHelp=new TextBlock{Text="resume: resolve and resume the state machine. close_only: record the resolution without resuming.",TextWrapping=TextWrapping.Wrap,Foreground=Brush("#B9ADCA"),FontSize=11,Margin=new Thickness(0,4,0,12)};
   var summary=new TextBox{MinWidth=320,Text="Resolved from Hexor Control Center."};
   var apply=new Button{Content="Resolve",IsDefault=true,Margin=new Thickness(0,4,0,4)};
   var cancel=new Button{Content="Cancel",IsCancel=true,Margin=new Thickness(0,4,0,4)};
   panel.Children.Add(new TextBlock{Text="Outcome",FontSize=12,Foreground=Brush("#BAB2CC")});panel.Children.Add(outcome);panel.Children.Add(outcomeHelp);
   panel.Children.Add(new TextBlock{Text="Summary",FontSize=12,Foreground=Brush("#BAB2CC")});panel.Children.Add(summary);
   var buttons=new StackPanel{Orientation=Orientation.Horizontal};buttons.Children.Add(apply);buttons.Children.Add(cancel);panel.Children.Add(buttons);
   (string,string)? decision=null;apply.Click+=(_,_)=>{decision=(outcome.SelectedItem as string ?? "resume",summary.Text);prompt.Close();};
   prompt.Content=panel;prompt.ShowDialog();return decision is { } d ? (d.Item1,d.Item2) : null;
  };

  var direct=Section(root,"Game connection","Hexor connects to the game automatically while AION2 is running. Stop asks the running automation to halt; Hexor never starts automation itself.");
  var directStatus=Text("",13,true,"#DCC8FF");directStatus.Name="LiveGameStatusText";directStatus.SetBinding(TextBlock.TextProperty,"LiveGameStatus");AutomationProperties.SetLiveSetting(directStatus,AutomationLiveSetting.Polite);direct.Children.Add(directStatus);
  var refresh=Button("Refresh live status",async()=>await Model.RefreshLiveStatusAsync());refresh.Name="RefreshLiveStatusButton";refresh.Background=Brush("#6942A8");direct.Children.Add(refresh);

  var sessions=Section(root,"Live sessions","Pause and Continue hold or resume the running workflow; Stop ends it at the next save point. Automation is never started from Hexor.");
  var sessionList=new ItemsControl{Name="LiveSessionList"};sessionList.SetBinding(ItemsControl.ItemsSourceProperty,"LiveSessionItems");
  sessionList.ItemTemplate=(DataTemplate)System.Windows.Markup.XamlReader.Parse("""
   <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
    <Border Background="#131020" CornerRadius="10" Padding="14,10" Margin="0,0,0,8">
     <StackPanel>
      <TextBlock Text="{Binding Label}" FontWeight="SemiBold"/>
      <TextBlock Text="{Binding Ipc}" FontSize="11" Foreground="#B9ADCA" Margin="0,2,0,6"/>
      <StackPanel Orientation="Horizontal">
       <Button Content="Pause" Command="{Binding PauseCommand}" MinWidth="90" Margin="0,0,10,0" Visibility="{Binding BridgeControls}"/>
       <Button Content="Continue" Command="{Binding ContinueCommand}" MinWidth="90" Margin="0,0,10,0" Visibility="{Binding BridgeControls}"/>
       <Button Content="Stop" Command="{Binding StopCommand}" MinWidth="90" Background="#69284A" Margin="0,0,10,0"/>
       <Button Content="Load daily records" Command="{Binding RecordsCommand}" MinWidth="150" Visibility="{Binding BridgeControls}"/>
       <Button Content="Refresh status" Command="{Binding RefreshCommand}" MinWidth="120" Visibility="{Binding DirectControls}"/>
      </StackPanel>
     </StackPanel>
    </Border>
   </DataTemplate>
   """);
  sessions.Children.Add(sessionList);

  var records=Section(root,"Daily records","Reads the automation controller's daily records with the game's own day boundary (UTC+8, YYYY-MM-DD). Optional server ID narrows the result.");
  var recordsRow=new Grid{Margin=new Thickness(0,4,0,6)};recordsRow.ColumnDefinitions.Add(new());recordsRow.ColumnDefinitions.Add(new(){Width=new GridLength(160)});recordsRow.ColumnDefinitions.Add(new());recordsRow.ColumnDefinitions.Add(new(){Width=new GridLength(120)});
  recordsRow.Children.Add(Text("Game day (YYYY-MM-DD)"));
  var gameDay=new TextBox{Name="LiveRecordsGameDayInput",Width=140};Grid.SetColumn(gameDay,1);gameDay.SetBinding(TextBox.TextProperty,new Binding("LiveRecordsGameDay"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});recordsRow.Children.Add(gameDay);
  recordsRow.Children.Add(Text("Server ID",12,false,"#BAB2CC"));
  var serverId=new TextBox{Name="LiveRecordsServerIdInput",Width=100};Grid.SetColumn(serverId,3);serverId.SetBinding(TextBox.TextProperty,new Binding("LiveRecordsServerId"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});recordsRow.Children.Add(serverId);
  records.Children.Add(recordsRow);
  var recordsStatus=Text("",12,false,"#B9ADCA");recordsStatus.SetBinding(TextBlock.TextProperty,"LiveDailyRecordsStatus");records.Children.Add(recordsStatus);
  var recordList=new ItemsControl{Name="LiveDailyRecordList",FontSize=12,Foreground=Brush("#DCC8FF")};recordList.SetBinding(ItemsControl.ItemsSourceProperty,"LiveDailyRecords");records.Children.Add(recordList);

  var captcha=Section(root,"CAPTCHA operations","CAPTCHA activity recorded while an automation controller session was connected.");
  var captchaStatus=Text("",12,false,"#B9ADCA");captchaStatus.SetBinding(TextBlock.TextProperty,"LiveCaptchaStatus");captcha.Children.Add(captchaStatus);
  var captchaList=new ItemsControl{Name="LiveCaptchaOperationList",FontSize=12,Foreground=Brush("#DCC8FF")};captchaList.SetBinding(ItemsControl.ItemsSourceProperty,"LiveCaptchaRows");captcha.Children.Add(captchaList);

  var incidents=Section(root,"Automation incidents","Open problems the controller reported (stalled or errored workflows). Resolve records your outcome and optionally resumes.");
  var incidentList=new ItemsControl{Name="LiveIncidentList"};incidentList.SetBinding(ItemsControl.ItemsSourceProperty,"LiveIncidentItems");
  incidentList.ItemTemplate=(DataTemplate)System.Windows.Markup.XamlReader.Parse("""
   <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
    <Border Background="#131020" CornerRadius="10" Padding="14,10" Margin="0,0,0,8">
     <StackPanel>
      <TextBlock Text="{Binding Label}" TextWrapping="Wrap"/>
      <Button Content="Resolve…" Command="{Binding ResolveCommand}" MinWidth="110" Margin="0,8,0,0" HorizontalAlignment="Left"/>
     </StackPanel>
    </Border>
   </DataTemplate>
   """);
  incidents.Children.Add(incidentList);

 }
 private void General(StackPanel root)
{
  var updateWrap=new StackPanel();
  updateWrap.Children.Add(new Hexor.Updates.UpdatePanel(automaticallyCheckForUpdates));
  updateWrap.Children.Add(Text("Run the AION updater again whenever AION2 receives a game update.",12,false,"#B9ADCA"));
  root.Children.Add(Card(updateWrap));

  var language=Section(root,"Language","The console language applies immediately. The in-game interface language is saved with your settings.");
  var uiRow=new Grid{Margin=new Thickness(0,4,0,8)};uiRow.ColumnDefinitions.Add(new());uiRow.ColumnDefinitions.Add(new(){Width=new GridLength(240)});language.Children.Add(uiRow);
  var uiLabel=Text(T("Console language"));uiLabel.VerticalAlignment=VerticalAlignment.Center;uiRow.Children.Add(uiLabel);
  var ui=new ComboBox{ItemsSource=new[]{"English","简体中文","한국어","Deutsch","日本語"},SelectedItem=Model.Preferences.UiLanguage,HorizontalAlignment=HorizontalAlignment.Left};Grid.SetColumn(ui,1);uiRow.Children.Add(ui);
  ui.SelectionChanged+=(_,_)=>{if(ui.SelectedItem is string chosen){var previous=Model.Preferences.UiLanguage;var previousGame=Model.Preferences.GameLanguage;new LanguageSettingsService().SetUi(Model.Preferences,chosen);try{var gameLanguage=chosen switch{"English"=>"English","简体中文"=>"Traditional Chinese","한국어"=>"Korean",_=>null};if(gameLanguage!=null)Model.SetGameLanguage(gameLanguage);}catch{Model.Preferences.GameLanguage=previousGame;}if(!Model.Save()){Model.Preferences.UiLanguage=previous;Model.Preferences.GameLanguage=previousGame;}else{Translations.CurrentLanguage=chosen;Model.RefreshLocalizedStatuses();Hexor.Updates.UpdatePanel.ResetLanguage();var fresh=new MainWindow(Model,automaticallyCheckForUpdates,ownsModel:false);fresh.Show();Close();return;}}};
  var cultureRow=new Grid{Margin=new Thickness(0,4,0,0)};cultureRow.ColumnDefinitions.Add(new());cultureRow.ColumnDefinitions.Add(new(){Width=new GridLength(240)});language.Children.Add(cultureRow);
  var cultureLabel=Text(T("In-game interface language"));cultureLabel.VerticalAlignment=VerticalAlignment.Center;cultureRow.Children.Add(cultureLabel);
  var culture=new ComboBox{ItemsSource=new[]{"zh-CN","en-US","ko-KR"},HorizontalAlignment=HorizontalAlignment.Left};culture.SetBinding(ComboBox.SelectedItemProperty,new Binding("OriginalUiCulture"){Mode=BindingMode.TwoWay});Grid.SetColumn(culture,1);cultureRow.Children.Add(culture);
  language.Children.Add(Button("Save language preference",Model.SaveOriginalUiLanguage));

  var module=Section(root,"Bot module","File detection reports whether the verified menu DLL is installed. It does not claim the DLL is loaded into AION2.");
  var menuRow=new WrapPanel{Orientation=Orientation.Horizontal};var unload=Button("Unload menu (this installation)",async()=>await UnloadMenuAsync());unload.Name="UnloadMenuButton";unload.Margin=new Thickness(0,4,16,4);menuRow.Children.Add(unload);
  menuStatusText=Text(menuStatus.Text,13,true,"#DCC8FF");menuStatusText.Name="MenuStatusText";menuStatusText.Margin=new Thickness(16,10,0,5);menuStatusText.VerticalAlignment=VerticalAlignment.Center;menuRow.Children.Add(menuStatusText);module.Children.Add(menuRow);
  module.Children.Add(Text("Unloading removes or restores the automation module (dxgi.dll) only for the installation reported in the status line above.",12,false,"#AEA5C0"));
  module.Children.Add(Text("Removing covers every detected AION2 installation and also deletes verified updater-owned data and caches. Hexor Control Center remains installed; use Windows Settings to uninstall it.",12,false,"#AEA5C0"));
  var clean=Button("Remove Bot Module",async()=>await RunCleanupAsync());clean.Name="RemoveBotModuleButton";clean.Background=Brush("#69284A");clean.Margin=new Thickness(0,4,0,0);module.Children.Add(clean);

  var folder=Section(root,"Settings folder","Choose your existing settings folder. Changes are saved only when you edit or apply a setting.");
  var folderButtons=new WrapPanel{Orientation=Orientation.Horizontal};
  void FolderButton(string label,Action action){var b=Button(label,action);b.Margin=new Thickness(0,4,10,4);folderButtons.Children.Add(b);}
  FolderButton("Find settings",async()=>{await Model.DiscoverAsync();await RefreshMenuStatusAsync();ShowPage(0);});
  FolderButton("Choose settings folder",()=>{var dialog=new OpenFolderDialog{Title="Select existing settings folder"};if(dialog.ShowDialog(this)==true){Model.ConfirmConfigurationFolder(dialog.FolderName);ShowPage(0);}});
  FolderButton("Reload settings",()=>{Model.ReloadConfiguration();ShowPage(0);});
  FolderButton("Sync Legion Commander Settings",PromptRegistryInitialization);
  folder.Children.Add(folderButtons);
  var connected=Text("",12);connected.SetBinding(TextBlock.TextProperty,"DiagnosticsDataRoot");folder.Children.Add(connected);
  foreach(var candidate in Model.Candidates.Where(c=>new Hexor.Compatibility.ConfigurationInspector().Inspect(c.Folder).HasEvidence))
  {
   var candidateButtons=new WrapPanel{Orientation=Orientation.Horizontal};
   var folder2=candidate.Folder;
   var use=Button(Hexor.Translations.LocalizeFormat("Use settings: {0}",folder2),()=>{Model.ConfirmConfigurationFolder(folder2);ShowPage(0);});use.Margin=new Thickness(0,4,10,4);candidateButtons.Children.Add(use);
   folder.Children.Add(candidateButtons);
  }

  var location=Section(root,"Detected game location","Review and confirm the running game location before saving.");
  var observedPath=Text("",12);observedPath.SetBinding(TextBlock.TextProperty,"Detection.ExecutablePath");location.Children.Add(observedPath);
  var locationRow=new WrapPanel{Orientation=Orientation.Horizontal};
  var pathTarget=new ComboBox{ItemsSource=new[]{"Global / Steam","Taiwan","Korea"},SelectedIndex=0,MinWidth=150,VerticalAlignment=VerticalAlignment.Center};pathTarget.Margin=new Thickness(0,4,10,4);locationRow.Children.Add(pathTarget);
  var review=Button("Review detected path…",async()=>{
   await Model.RefreshDetectionAsync();
   var key=new[]{"GamePath_Global","GamePath_TW","GamePath_KR"}[pathTarget.SelectedIndex];if(Model.ProposeDetectedPath(key) is not { } proposal)return;
   var previous=proposal.Expected.Value(key)??"(not configured)";
   if(MessageBox.Show(this,$"Save the detected game location for {pathTarget.SelectedItem}?\n\nCurrent: {previous}\nDetected: {proposal.ExecutablePath}\nPID: {proposal.ProcessId}\n\nA backup of the existing settings will be created.","Confirm game location",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
   await Model.RefreshDetectionAsync();Model.ConfirmDetectedPath(proposal);
  });review.Margin=new Thickness(0,4,10,4);locationRow.Children.Add(review);
  var installations=Button("Review detected AION2 installations",ReviewAionInstallations);installations.Margin=new Thickness(0,4,0,4);locationRow.Children.Add(installations);
  location.Children.Add(locationRow);

  var diagnostics=Section(root,"Diagnostics","Local support logs contain operational checkpoints only. Review the ZIP before sharing; nothing is uploaded automatically.");
  diagnostics.Children.Add(Text("Active log: hexor.log",12,true,"#DCC8FF"));
  diagnostics.Children.Add(Text(Hexor.Translations.LocalizeFormat("Current size: {0} MB",(Diagnostics.ActiveLogSize/1024d/1024d).ToString("F2",System.Globalization.CultureInfo.InvariantCulture)),12,false,"#B9ADCA"));
  var diagnosticButtons=new WrapPanel{Orientation=Orientation.Horizontal,ItemWidth=236,Margin=new Thickness(0,8,0,0)};
  void DiagnosticButton(string label,Action action){var b=Button(label,action);b.HorizontalAlignment=HorizontalAlignment.Stretch;b.Margin=new Thickness(0,4,8,4);diagnosticButtons.Children.Add(b);}
  DiagnosticButton("Open Log",()=>OpenPath(Diagnostics.ActiveLogPath));
  DiagnosticButton("Open Logs Folder",()=>OpenPath(Diagnostics.Root));
  DiagnosticButton("Copy Log Path",()=>{Clipboard.SetText(Diagnostics.ActiveLogPath);MessageBox.Show(this,"Log path copied.","Diagnostics",MessageBoxButton.OK,MessageBoxImage.Information);});
  DiagnosticButton("Copy Support ID",()=>{Clipboard.SetText(Diagnostics.SessionId);MessageBox.Show(this,"Support ID copied.","Diagnostics",MessageBoxButton.OK,MessageBoxImage.Information);});
  DiagnosticButton("Export Support Logs",()=>{var path=Diagnostics.ExportSupportLogs();if(string.IsNullOrEmpty(path)){MessageBox.Show(this,"Support log export could not be created.","Diagnostics",MessageBoxButton.OK,MessageBoxImage.Warning);return;}new SupportLogExportDialog(path){Owner=this}.ShowDialog();});
  DiagnosticButton("Open Export Folder",()=>{var path=Diagnostics.LastExportPath;if(!string.IsNullOrEmpty(path))SupportLogExportActions.OpenFolder(path);else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Diagnostics.Root){UseShellExecute=true});});
  DiagnosticButton("Refresh diagnostics",async()=>await Model.RefreshDetectionAsync());
  DiagnosticButton("Retry task settings synchronization",Model.RetryAutoTaskMirror);
  diagnostics.Children.Add(diagnosticButtons);

  var diagnosticPanel=new StackPanel();
  foreach(var field in new[]{("Observed at","DiagnosticsObservedAt"),("AION2 processes / executable paths","DiagnosticsProcess"),("Resolved configuration data root","DiagnosticsDataRoot"),("Configuration files","DiagnosticsFiles"),("Configuration installation-path association","DiagnosticsAssociation"),("Discovery decisions","DiagnosticsCandidates"),("Setting details","DiagnosticsSettingDetails"),("Detection errors","DiagnosticsErrors"),("CAPTCHA monitor","CaptchaDiagnostics")}){diagnosticPanel.Children.Add(Text(field.Item1,12,true,"#CDB6F5"));var data=Text("",12);data.SetBinding(TextBlock.TextProperty,field.Item2);diagnosticPanel.Children.Add(data);}
  var operation=Text("",12);operation.SetBinding(TextBlock.TextProperty,"Status");diagnosticPanel.Children.Add(operation);
  diagnosticPanel.Children.Add(Text("Saved preferences are not confirmation that the running automation has applied them. Some preferences take effect when a task or the game next starts.",12));
  diagnosticPanel.Children.Add(Text("Auto Task settings are stored in the existing configuration and registry. If a save is only partially completed, use the retry action above. No in-process refresh is sent.",12));
  var diagnosticsExpander=new Expander{Name="DiagnosticsExpander",Header="Diagnostics details",IsExpanded=false,Foreground=Brushes.White,Content=diagnosticPanel};root.Children.Add(Card(diagnosticsExpander));

  var support=Section(root,"Support & community");
  support.Children.Add(Text("Discord Server · discord.gg/PsDUVazRT3\nDiscord Username · h3x0r1337",13,false,"#DCC8FF"));
  var supportButtons=new WrapPanel{Orientation=Orientation.Horizontal};
  var join=Button("Join Discord",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://discord.gg/PsDUVazRT3"){UseShellExecute=true}));join.Margin=new Thickness(0,4,10,4);supportButtons.Children.Add(join);
  var copyUser=Button("Copy Username",()=>{Clipboard.SetText("h3x0r1337");MessageBox.Show(this,"Discord username copied.","Hexor Support",MessageBoxButton.OK,MessageBoxImage.Information);});copyUser.Margin=new Thickness(0,4,0,4);supportButtons.Children.Add(copyUser);
  support.Children.Add(supportButtons);
 }
  private static void OpenPath(string path){try{if(path==Diagnostics.ActiveLogPath&&!System.IO.File.Exists(path))Diagnostics.Log("diagnostics","active-log-created");System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});}catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){Diagnostics.Log("diagnostics","open-failed; type="+e.GetType().Name);}}
  private string MenuManifestPath()=>VendorUpdaterInstallation.OwnershipManifestPath()??"";
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
 private void AddCaptchaRecords(Panel parent)
 {
  var content=new StackPanel{Margin=new Thickness(4,6,0,0)};
  var summary=Text("",12,false,"#B9ADCA");
  summary.Name="CaptchaRecordsSummary";
  summary.SetBinding(TextBlock.TextProperty,nameof(MainViewModel.CaptchaRecordsSummary));
  content.Children.Add(summary);
  var records=new ItemsControl{Name="CaptchaRecordList",FontSize=12,Foreground=Brush("#DCC8FF")};
  records.SetBinding(ItemsControl.ItemsSourceProperty,nameof(MainViewModel.CaptchaRecordLines));
  content.Children.Add(records);
  parent.Children.Add(new Expander
  {
   Name="CaptchaRecordsExpander",Header=T("CAPTCHA records"),Foreground=Brushes.White,
   Margin=new Thickness(0,10,0,0),Content=content
  });
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
    if(MessageBox.Show(this,$"AION2 installations: {plan.Items.Count(x=>x.Kind==CleanupItemKind.PathMatchedDxgi)}\nAutomation module (dxgi.dll) copies: {dlls}\nUpdater settings, language, license, authorization, cache, and temporary data: {data}\n\nUpdater binaries and Hexor Control Center are preserved. This permanently erases the listed updater data. Confirm removal?","Remove Bot Module",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
   var results=await workflow.ExecuteAsync(plan,stage=>Dispatcher.Invoke(()=>progress.Text=stage));LogCleanupCompleted(results);var changed=results.Count(x=>x.State is VendorOverlayCleanupState.Removed or VendorOverlayCleanupState.Restored);var blocked=results.Count(x=>x.State==VendorOverlayCleanupState.BlockedByRunningProcess);
     var failed=results.Count(x=>x.State is VendorOverlayCleanupState.Failed or VendorOverlayCleanupState.BlockedByRunningProcess);MessageBox.Show(this,$"Automation modules removed: {results.Count(x=>x.Kind==CleanupItemKind.PathMatchedDxgi&&x.State==VendorOverlayCleanupState.Removed)}\nUpdater data removed: {results.Count(x=>(x.Kind is CleanupItemKind.UpdaterData or CleanupItemKind.UpdaterRegistry)&&x.State==VendorOverlayCleanupState.Removed)}\nSkipped: {results.Count(x=>x.State is VendorOverlayCleanupState.Skipped or VendorOverlayCleanupState.CouldNotVerify)}\nFailed: {failed}","Remove Bot Module",MessageBoxButton.OK,(blocked>0||failed>0)?MessageBoxImage.Warning:MessageBoxImage.Information);
   } catch(OperationCanceledException) { MessageBox.Show(this,"Removal was cancelled before changes were made.","Remove Bot Module",MessageBoxButton.OK,MessageBoxImage.Information); }
     catch(Exception e) { MessageBox.Show(this,"Removal could not be prepared: "+e.GetType().Name,"Remove Bot Module",MessageBoxButton.OK,MessageBoxImage.Warning); }
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
  var root=AppLayout.InstallParent();
  var manifest=VendorUpdaterInstallation.OwnershipManifestPath()??"";
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
  var identity = Section(root, "Character defaults");
  CharacterDefault(identity, "Character race", "CharacterRace", nameof(MainViewModel.AutoTaskRace), LegionCommanderFeatureSettings.Races);
  CharacterDefault(identity, "Character class", "CharacterJob", nameof(MainViewModel.AutoTaskJob), LegionCommanderFeatureSettings.Jobs);
 }

 private void ResourceGrab(StackPanel root)
 {
  var abyss=Section(root,"Abyss");
  RegistryOptions(abyss,"ResourceGrab",["AutoEnterAbyss"]);
  RadioRow(abyss,"Target layer","AbyssLayer",("AbyssLayerLower","Lower",0),("AbyssLayerMiddle","Middle",1));
  RegistryOptions(abyss,"ResourceGrab",["AbyssCounterAttack"]);

  var auction=Section(root,"Auction","Each Hexor startup resets auto sell to OFF and price mode to Average.");
  RegistryOptions(auction,"ResourceGrab",["AuctionAutoSell"]);
  var priceRow=RadioRow(auction,"Price","AuctionPriceMode",("AuctionPriceModeLowest","Lowest",0),("AuctionPriceModeAverage","Average",1));
 }

 private void AutoDungeonPage(StackPanel root)
 {
  var selection=Section(root,"Dungeon selection","The first enterable dungeon from the top is used: unchecked dungeons or those whose requirement is not met are skipped. The chosen dungeon is queued until it starts; list changes apply from the next round.");
  RadioRow(selection,"Mode","DungeonMode",("DungeonModeSmart","Smart",0),("DungeonModeOrder","Priority order",1));
  selection.Children.Add(Text(T("Dungeon priority"),13,true));
  var header=new Grid{Margin=new Thickness(0,6,8,2)};header.ColumnDefinitions.Add(new(){Width=new(26)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  header.Children.Add(Text(T("#"),12,false,"#A99ABF"));
  var onHeader=Text(T("On"),12,false,"#A99ABF");Grid.SetColumn(onHeader,1);header.Children.Add(onHeader);
  var dungeonHeader=Text(T("Dungeon"),12,false,"#A99ABF");Grid.SetColumn(dungeonHeader,2);header.Children.Add(dungeonHeader);
  var orderHeader=Text(T("Order"),12,false,"#A99ABF");Grid.SetColumn(orderHeader,3);header.Children.Add(orderHeader);
  selection.Children.Add(header);
  var dungeonList=new ItemsControl{Name="DungeonPriorityList"};dungeonList.SetBinding(ItemsControl.ItemsSourceProperty,new Binding("DungeonPriority"));
  dungeonList.ItemTemplate=(DataTemplate)System.Windows.Markup.XamlReader.Parse("""
   <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
    <Grid Margin="0,3,8,3">
     <Grid.ColumnDefinitions><ColumnDefinition Width="26"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
     <TextBlock Text="{Binding Ordinal}" VerticalAlignment="Center" Foreground="#A99ABF"/>
     <CheckBox Grid.Column="1" VerticalAlignment="Center" IsChecked="{Binding Enabled, Mode=TwoWay}" AutomationProperties.Name="{Binding Label}"/>
     <TextBlock Grid.Column="2" Text="{Binding Label}" VerticalAlignment="Center" TextWrapping="Wrap" Margin="0,0,12,0"/>
     <StackPanel Grid.Column="3" Orientation="Horizontal">
      <Button Content="Up" Command="{Binding MoveUp}" MinWidth="56" MinHeight="32" Margin="0,0,6,0"/>
      <Button Content="Down" Command="{Binding MoveDown}" MinWidth="56" MinHeight="32"/>
     </StackPanel>
    </Grid>
   </DataTemplate>
   """);
  selection.Children.Add(dungeonList);
  var reset=Button("Reset Default",Model.ResetDungeonPriority);reset.Name="ResetDungeonPriorityButton";selection.Children.Add(reset);
  selection.Children.Add(Text(T("Shared by all characters; the in-game Auto Dungeon page edits the same list."),12,false,"#A99ABF"));

  var flow=Section(root,"Dungeon flow");
  CheckToggle(flow,"AutoDailyWaveEnabled","Auto-run Daily Dungeon (rewards Enchantment Stones)");
  CheckToggle(flow,"AutoNightmareEnabled","Auto-challenge Daily Nightmare (rewards Daevanion and Stigma points)");
  CheckToggle(flow,"AutoAwakenEnabled","Auto-challenge Ascension Trial (rewards Enchantment Stones and Amplification Fragments)");
  CheckToggle(flow,"AutoOdMorphEnabled","Auto-morph Odyle Energy");
  CheckToggle(flow,"AutoDungeonQuickJoinEnabled","Quick Join existing rooms at 60K+ battle power");
 }

 private void CaptchaPage(StackPanel root)
 {
  var auto=Section(root,"Auto-resolve","When the game shows a CAPTCHA challenge, Hexor solves it and submits the answer automatically. Nothing for you to run or watch.");
  var autoStatus=Text("",13,true,"#DCC8FF");autoStatus.Name="CaptchaAutoStatusText";autoStatus.SetBinding(TextBlock.TextProperty,nameof(MainViewModel.CaptchaAutoStatus));AutomationProperties.SetLiveSetting(autoStatus,AutomationLiveSetting.Polite);auto.Children.Add(autoStatus);

  var status=Section(root,"Activity log","Every challenge, answer and result while Hexor is open — newest first. Challenge images are archived in the CaptchaSamples folder for review.");
  var summary=Text("",12,false,"#B9ADCA");summary.Name="CaptchaRecordsSummary";summary.SetBinding(TextBlock.TextProperty,nameof(MainViewModel.CaptchaRecordsSummary));status.Children.Add(summary);
  var records=new ItemsControl{Name="CaptchaRecordList",FontSize=12,Foreground=Brush("#DCC8FF")};records.SetBinding(ItemsControl.ItemsSourceProperty,nameof(MainViewModel.CaptchaRecordLines));status.Children.Add(records);

  var alerts=Section(root,"Alert preferences","Hexor plays a sound and shows a tile when the game shows a challenge. The auto-resolve answers it automatically — nothing is paused and no confirmation is needed.");
  Check(alerts,"Alert me when a captcha appears","CaptchaNotifications");
 }
 private void Enhance(StackPanel root)
 {
  var upgrade=Section(root,"Auto equip/enchant combat power cap","Controls AutoUpgradeMaxBattlePowerK independently from the gear enhancement level cap below.");
  var capRow=new Grid{Margin=new Thickness(0,6,0,4)};capRow.ColumnDefinitions.Add(new());capRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  var battleSlider=new Slider{Name="AutoEquipCombatPowerSlider",Minimum=1,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(battleSlider,"Auto Equip and Enchant Combat Power Cap");battleSlider.SetBinding(Slider.ValueProperty,new Binding("UpgradeMaxBattlePowerK"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});capRow.Children.Add(battleSlider);
  var selected=Text("",18,true,"#DCC8FF");selected.Name="AutoEquipCombatPowerValue";selected.SetBinding(TextBlock.TextProperty,new Binding("UpgradeMaxBattlePowerK"){StringFormat="{0}K"});selected.Margin=new Thickness(16,0,0,0);Grid.SetColumn(selected,1);capRow.Children.Add(selected);
  upgrade.Children.Add(capRow);
  CheckToggle(upgrade,"EnchantBoundKinahOnly","Auto-enchant with bound Kinah only (keep at least 500,000)");
  var battleCurrent=Text("",12,false,"#B9ADCA");battleCurrent.SetBinding(TextBlock.TextProperty,new Binding("CurrentUpgradeMaxBattlePowerK"){StringFormat=Hexor.Translations.Localize("Saved registry value: {0}K")});upgrade.Children.Add(battleCurrent);
  upgrade.Children.Add(Button("Apply combat power cap",Model.SaveUpgradeMaxBattlePowerK));

  var extraction=Section(root,"Equipment Extraction","When the bag contains 100,000 or more Enhancement Stones, equipment that would normally be extracted is destroyed instead. Below 100,000, extraction continues normally.");
  CheckToggle(extraction,"DestroyInsteadOfExtract","Destroy instead of extracting at 100,000 Enchantment Stones");

  var membership=Section(root,"Membership Benefits");
  var memberRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,2,0,2)};
  CheckToggle(memberRow,"AutoBuyOdEnergy","Automatically buy Od Energy").Margin=new Thickness(0,0,32,0);
  CheckToggle(memberRow,"AutoBuyReviveStone","Automatically buy Resurrection Stones");
  membership.Children.Add(memberRow);

  var shop=Section(root,"Shop Exchange","Coin-shop exchanges shared by the verified five toggles.");
  var shopGrid=new Grid();shopGrid.RowDefinitions.Add(new(){Height=GridLength.Auto});shopGrid.ColumnDefinitions.Add(new(){Width=new GridLength(170)});shopGrid.ColumnDefinitions.Add(new());shopGrid.ColumnDefinitions.Add(new());
  var stigmaHeader=Text(T("Stigma Skill Points (Stigma Shards)"),12,false,"#BAB2CC");Grid.SetColumn(stigmaHeader,1);shopGrid.Children.Add(stigmaHeader);
  var daevHeader=Text(T("Daevanion Points (up to 10)"),12,false,"#BAB2CC");Grid.SetColumn(daevHeader,2);shopGrid.Children.Add(daevHeader);
  AddShopRow(shopGrid,0,"Nightmare Coins","CoinShopNightmareStigma","CoinShopNightmareDaevanion");
  AddShopRow(shopGrid,1,"Shugo Coins (Festival)","CoinShopFestivalStigma","CoinShopFestivalDaevanion");
  AddShopRow(shopGrid,2,"Abyss Points","CoinShopAbyssStigma",null);
  shop.Children.Add(shopGrid);

  var pet=Section(root,"Pet");
  CheckToggle(pet,"GenusInsightAutoAnalyze","Auto-reroll pet (Genus Insight analysis)");
 }

 private void AddShopRow(Grid grid,int index,string rowLabel,string stigmaToggle,string? daevanionToggle)
 {
  var row=index+1;
  grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
  var label=Text(T(rowLabel),13,false,"#F1EFF8");label.VerticalAlignment=VerticalAlignment.Center;Grid.SetRow(label,row);grid.Children.Add(label);
  var holding=new StackPanel();
  var stigma=CheckToggle(holding,stigmaToggle);holding.Children.Remove(stigma);
  stigma.Margin=new(0,2,24,2);Grid.SetRow(stigma,row);Grid.SetColumn(stigma,1);grid.Children.Add(stigma);
  if(daevanionToggle!=null)
  {
   var daev=CheckToggle(holding,daevanionToggle);holding.Children.Remove(daev);
   daev.Margin=new(0,2,0,2);Grid.SetRow(daev,row);Grid.SetColumn(daev,2);grid.Children.Add(daev);
  }
 }
 private ComboBox SelectOption<TValue>(Panel panel, string label, string name, IReadOnlyList<(TValue Value, string Label)> options,
     string path, BindingMode mode = BindingMode.TwoWay)
 {
  panel.Children.Add(Text(T(label), 13, true));
  var selector = new ComboBox
  {
   Name = name, ItemsSource = options.Select(option => new SelectionOption<TValue>(option.Value, T(option.Label))).ToArray(),
   SelectedValuePath = nameof(SelectionOption<TValue>.Value), HorizontalAlignment = HorizontalAlignment.Left
  };
  AutomationProperties.SetName(selector, T(label));
  selector.SetBinding(ComboBox.SelectedValueProperty, new Binding(path) { Mode = mode });
  panel.Children.Add(selector);
  return selector;
 }

 private sealed record SelectionOption<T>(T Value, string Label)
 {
  public override string ToString() => Label;
 }

 private void CharacterDefault(Panel panel, string label, string key, string property, IReadOnlyList<(int, string)> options)
 {
  var selector = SelectOption(panel, label, key + "Selector", options, property, BindingMode.OneWay);
  selector.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.OriginalConfigurationReady)));
  var feedback = Text("", 13, false, "#CDB6F5");
  AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
  var apply = Button(Translations.Localize("Apply ") + Translations.Localize(label).ToLowerInvariant(), () =>
  {
   if (selector.SelectedValue is int value)
   {
    Model.SaveCharacterDefault(key, value);
    selector.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateTarget();
    feedback.Text = Model.Status;
   }
  });
  apply.Name = "Apply" + key;
  apply.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.OriginalConfigurationReady)));
  panel.Children.Add(apply);
  panel.Children.Add(feedback);
 }

 private void AddEditorStatus(Panel panel, string path)
 {
  var statusText = Text("", 13, false, "#CDB6F5");
  statusText.SetBinding(TextBlock.TextProperty, path);
  AutomationProperties.SetLiveSetting(statusText, AutomationLiveSetting.Polite);
  panel.Children.Add(statusText);
 }

 private void DailyActivities(StackPanel root)
 {
  // The vendor editor reads top to bottom in one column: templates, the step list, the
  // selected step's options, the completion choice, the files row and the run controls.
  // Every row wraps, so no button is ever cut off by the window size.
  var sequence = Section(root, "Instruction sequence");
  var notice = Text("", 12, false, "#B9ADCA");
  notice.SetBinding(TextBlock.TextProperty, nameof(MainViewModel.WorkflowNotice));
  sequence.Children.Add(notice);
  var templates = new WrapPanel();
  foreach (var (label, action) in new (string, Action)[]
  {
   ("Restore default", () => Model.LoadDefaultWorkflow()),
   ("Gold + gather", () => Model.Preset(1)),
   ("Gather only", () => Model.Preset(2)),
   ("Task + gold + gather", () => Model.Preset(3)),
   ("Task + Krao + gold", () => Model.Preset(4)),
  })
   templates.Children.Add(Button(label, action));
  sequence.Children.Add(templates);
  var steps = new ListBox { Name = "DailyWorkflowSteps", MinHeight = 140, MaxHeight = 420, Margin = new Thickness(0, 10, 0, 0),
      Style = (Style)FindResource("WorkflowListStyle") };
  ScrollViewer.SetHorizontalScrollBarVisibility(steps, ScrollBarVisibility.Disabled);
  AutomationProperties.SetName(steps, T("Instruction sequence"));
  steps.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Preferences.Instructions"));
  steps.SetBinding(ListBox.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedInstruction)) { Mode = BindingMode.TwoWay });
  steps.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
   <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
     <Grid>
       <Grid.ColumnDefinitions><ColumnDefinition Width="36"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
       <TextBlock Foreground="#B9ADCA" TextWrapping="NoWrap" Text="{Binding SequenceNumber}" />
       <TextBlock Grid.Column="1" Text="{Binding ActivityLabel}" />
     </Grid>
   </DataTemplate>
   """);
  sequence.Children.Add(steps);
  var actions = new WrapPanel();
  foreach (var (label, command) in new[]
  {
   ("Add gathering", Model.AddGatherCommand), ("Add kinah farming", Model.AddKinahCommand),
   ("Add auto dungeon", Model.AddAutoDungeonCommand), ("Add Auto Krao Dungeon", Model.AddKraoCommand),
   ("Add switch character", Model.AddSwitchCommand),
   ("Duplicate", Model.DuplicateCommand), ("Delete", Model.DeleteCommand),
   ("Move up", Model.UpCommand), ("Move down", Model.DownCommand)
  })
   actions.Children.Add(new Button { Content = T(label), Command = command, Margin = new Thickness(0, 7, 6, 0), Padding = new Thickness(10, 6, 10, 6), MinHeight = 36 });
  sequence.Children.Add(actions);

  // The selected step's options stay visible and are disabled when they do not apply,
  // exactly like the vendor editor (minutes and Reset timer only for Gathering,
  // Smart patrol only for Kinah Farming).
  var detail = Section(root, "Selected step settings");
  detail.SetBinding(DataContextProperty, new Binding(nameof(MainViewModel.SelectedInstruction)));
  var showSelected = new Style(typeof(StackPanel));
  var noSelection = new DataTrigger { Binding = new Binding("."), Value = null };
  noSelection.Setters.Add(new Setter(IsEnabledProperty, false));
  showSelected.Triggers.Add(noSelection);
  detail.Style = showSelected;
  Check(detail, "Enabled", nameof(Instruction.Enabled));
  SelectOption(detail, "Activity type", "DailyActivityType",
      InstructionPresentation.Options.Select(option => (option.Value, option.Label)).ToArray(), nameof(Instruction.Type));
  var minutesMin = Input(detail, "Minimum minutes", nameof(Instruction.MinimumText));
  var minutesMax = Input(detail, "Maximum minutes", nameof(Instruction.MaximumText));
  var gatheringGate = new Binding(nameof(Instruction.Type)) { Converter = new StepTypeEnabledConverter(), ConverterParameter = "Gathering" };
  minutesMin.SetBinding(TextBox.IsEnabledProperty, gatheringGate);
  minutesMax.SetBinding(TextBox.IsEnabledProperty, gatheringGate);
  var patrol = Check(detail, "Smart patrol", nameof(Instruction.SmartPatrol));
  patrol.SetBinding(CheckBox.IsEnabledProperty, new Binding(nameof(Instruction.Type)) { Converter = new StepTypeEnabledConverter(), ConverterParameter = "KinahFarming" });
  var resetTimer = Check(detail, "Reset timer", nameof(Instruction.ResetTimer));
  resetTimer.SetBinding(CheckBox.IsEnabledProperty, gatheringGate);
  detail.Children.Add(PrimaryEditorButton("ApplyDailyWorkflow", "Apply instruction settings", Model.SaveDailyCommand));
  AddEditorStatus(detail, nameof(MainViewModel.WorkflowEditorStatus));

  var completion = Section(root, "After all characters finish", "Choose what happens once every character has completed the sequence.");
  SelectOption(completion, "Completion", "DailyWorkflowCompletion",
      InstructionPresentation.CompletionOptions.Select(option => (option.Value, option.Label)).ToArray(), "Preferences.AfterFinish");

  var workflowFiles = new WrapPanel();
  workflowFiles.Children.Add(Button("Import workflow", ImportWorkflow));
  workflowFiles.Children.Add(Button("Export workflow", ExportWorkflow));
  workflowFiles.Children.Add(Button("Open settings folder", () => Model.OpenConfigurationFolder()));
  workflowFiles.Children.Add(Button("Load default sequence", () =>
  {
   if (MessageBox.Show(this, "Replace the current draft sequence with the default sequence?",
       "Default workflow", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
    Model.LoadDefaultWorkflow();
  }));
  root.Children.Add(new Expander { Header = "Workflow files and defaults", Content = workflowFiles, Margin = new Thickness(2, 10, 0, 0) });

  var sequenceControls = Section(root, "Run controls", "Starts or stops the saved task sequence inside the running game — the same commands the in-game menu uses. The game must be running; the sequence follows the instruction order above.");
  var runRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
  var startSequence = Button("Start sequence", async () => await Model.StartDailySequenceAsync());
  startSequence.Name = "StartDailySequenceButton";
  startSequence.Background = Brush("#6942A8");
  startSequence.Margin = new Thickness(0, 0, 10, 6);
  runRow.Children.Add(startSequence);
  var stopSequence = Button("Stop", async () => await Model.StopDailySequenceAsync());
  stopSequence.Name = "StopDailySequenceButton";
  stopSequence.Background = Brush("#69284A");
  stopSequence.Margin = new Thickness(0, 0, 10, 6);
  runRow.Children.Add(stopSequence);
  var applyDefault = Button("Use default config", async () =>
  {
      var session = Model.ConnectedGameSession();
      if (session == null) { Model.Status = "Start AION2 first — the default config applies inside the running game."; return; }
      await Model.ApplyDefaultConfigAsync(session.ProcessId);
  });
  applyDefault.Name = "ApplyDefaultConfigButton";
  applyDefault.Background = Brush("#6942A8");
  applyDefault.Margin = new Thickness(0, 0, 10, 6);
  runRow.Children.Add(applyDefault);
  var startGathering = Button("Start gathering", async () => await Model.StartGatheringAsync());
  startGathering.Name = "StartGatheringButton";
  startGathering.Background = Brush("#6942A8");
  startGathering.Margin = new Thickness(0, 0, 10, 6);
  runRow.Children.Add(startGathering);
  var stopGathering = Button("Stop gathering", async () => await Model.StopGatheringAsync());
  stopGathering.Name = "StopGatheringButton";
  stopGathering.Background = Brush("#69284A");
  stopGathering.Margin = new Thickness(0, 0, 0, 6);
  runRow.Children.Add(stopGathering);
  sequenceControls.Children.Add(runRow);
  var runStatus = Text("", 12, false, "#B9ADCA");
  runStatus.SetBinding(TextBlock.TextProperty, "Status");
  sequenceControls.Children.Add(runStatus);
 }

 private void ImportWorkflow()
 {
  var dialog = new OpenFileDialog { Filter = "JSON workflow (*.json)|*.json" };
  if (dialog.ShowDialog(this) != true) return;
  if (MessageBox.Show(this, "Replace the current draft sequence with the selected workflow?",
      "Import workflow", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
   Model.ImportWorkflow(dialog.FileName);
 }

 private void ExportWorkflow()
 {
  var dialog = new SaveFileDialog
  {
   Filter = "JSON workflow (*.json)|*.json", FileName = "Hexor-workflow.json", OverwritePrompt = false
  };
  if (dialog.ShowDialog(this) == true) Model.ExportWorkflow(dialog.FileName);
 }

 private void WorkSchedule(StackPanel root)
 {
  var schedule = Section(root, "Schedule limits", "The saved schedule is applied when the game or the next task starts.");
  Check(schedule, "Enable Work Schedule", "Preferences.ScheduleEnabled");
  Input(schedule, "Maximum accounts online", nameof(MainViewModel.MaxAccounts));
  AddScheduleRange(schedule, "Work duration", nameof(MainViewModel.WorkMin), nameof(MainViewModel.WorkMax));
  AddScheduleRange(schedule, "Rest duration", nameof(MainViewModel.RestMin), nameof(MainViewModel.RestMax));
  schedule.Children.Add(PrimaryEditorButton("SaveWorkSchedule", "Save schedule", Model.SaveScheduleCommand));
  AddEditorStatus(schedule, nameof(MainViewModel.ScheduleEditorStatus));
 }

 private Button PrimaryEditorButton(string name, string label, ICommand command) => new()
 {
  Name = name, Content = T(label), Command = command, HorizontalAlignment = HorizontalAlignment.Left,
  Background = Brush("#6942A8"), Margin = new Thickness(0, 12, 0, 6)
 };

 private void AddScheduleRange(Panel panel, string label, string minimumPath, string maximumPath)
 {
  var pair = new Grid();
  pair.ColumnDefinitions.Add(new ColumnDefinition());
  pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
  pair.ColumnDefinitions.Add(new ColumnDefinition());
  var left = new StackPanel();
  var right = new StackPanel();
  Grid.SetColumn(right, 2);
  pair.Children.Add(left);
  pair.Children.Add(right);
  Input(left, label + " minimum (minutes)", minimumPath);
  Input(right, label + " maximum (minutes)", maximumPath);
  panel.Children.Add(pair);
 }

 private void Festival(StackPanel root){RegistryOptions(Section(root,"Shugo Festival"),"MiniGame");}
}

