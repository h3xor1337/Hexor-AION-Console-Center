using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace Hexor;
public static class SupportLogExportActions
{public static string ExplorerSelectArgument(string path)=>"/select,\""+Path.GetFullPath(path)+"\"";public static bool OpenFolder(string path,Func<ProcessStartInfo,Process?>? start=null){try{(start??Process.Start)(new ProcessStartInfo("explorer.exe",ExplorerSelectArgument(path)){UseShellExecute=true});return true;}catch{try{(start??Process.Start)(new ProcessStartInfo(Path.GetDirectoryName(Path.GetFullPath(path))!){UseShellExecute=true});return false;}catch{return false;}}}}
public sealed class SupportLogExportDialog : Window
{public SupportLogExportDialog(string path){Title="Support logs exported";Width=510;Height=230;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;var p=new StackPanel{Margin=new Thickness(22)};p.Children.Add(new TextBlock{Text="Support logs exported successfully",FontSize=18,FontWeight=FontWeights.SemiBold});p.Children.Add(new TextBlock{Text="ZIP: "+Path.GetFileName(path),Margin=new Thickness(0,14,0,2)});p.Children.Add(new TextBlock{Text="Support ID: "+Diagnostics.SessionId});var b=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,20,0,0)};var open=new Button{Content="Open Folder",Margin=new Thickness(0,0,8,0)};open.Click+=(_,_)=>SupportLogExportActions.OpenFolder(path);var copy=new Button{Content="Copy Path",Margin=new Thickness(0,0,8,0)};copy.Click+=(_,_)=>Clipboard.SetText(path);var close=new Button{Content="Close"};close.Click+=(_,_)=>Close();b.Children.Add(open);b.Children.Add(copy);b.Children.Add(close);p.Children.Add(b);Content=p;}}
