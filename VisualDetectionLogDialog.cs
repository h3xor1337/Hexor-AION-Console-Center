using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Hexor;
public sealed class VisualDetectionLogDialog : Window
{
 public VisualDetectionLogDialog(MainViewModel model)
 {
  Title="Visual detection logs";Width=560;Height=420;SizeToContent=SizeToContent.Height;MaxHeight=560;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  Background=Brush("#161620");Foreground=Brushes.White;
  var panel=new DockPanel{Margin=new Thickness(22)};
  var buttons=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,16,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);
  var refresh=new Button{Content="Refresh",Margin=new Thickness(0,0,8,0)};refresh.Click+=(_,_)=>Load(model);
  var close=new Button{Content="Close"};close.Click+=(_,_)=>Close();
  buttons.Children.Add(refresh);buttons.Children.Add(close);
  var list=new StackPanel();
  panel.Children.Add(buttons);panel.Children.Add(new ScrollViewer{MaxHeight=320,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=list});
  Content=panel;
  Load(model);
 }
 private void Load(MainViewModel model)
 {
  var list=(StackPanel)((ScrollViewer)((DockPanel)Content).Children[1]).Content;
  list.Children.Clear();
  list.Children.Add(new TextBlock{Text="Detection events (oldest first). Detection is visual only: Hexor never reads challenge answer content and never generates or submits answers.",TextWrapping=TextWrapping.Wrap,Foreground=Brush("#AFA0C7"),Margin=new Thickness(0,0,0,10)});
  foreach(var entry in model.VisualSnapshot.RecentEvents)
   list.Children.Add(new TextBlock{Text=entry.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+"  "+entry.Phase+(entry.Confidence is { } confidence?"  confidence "+confidence.ToString("F2"):"")+"  "+entry.Detail,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)});
  if(model.VisualSnapshot.RecentEvents.Count==0)list.Children.Add(new TextBlock{Text="No detection events recorded yet."});
 }
 private static SolidColorBrush Brush(string hex)=>(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
