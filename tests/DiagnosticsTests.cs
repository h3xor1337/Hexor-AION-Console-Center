using System.IO.Compression;
using System.IO;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Hexor;

internal static class DiagnosticsTests
{
 public static void Run(string root,Action<bool,string> assert)
 {
  var expectedRoot=Path.GetFullPath(Path.Combine(root,"logs"));
  assert(Path.GetFullPath(Diagnostics.Root)==expectedRoot&&Path.GetFullPath(Diagnostics.Root)!=Path.GetFullPath(Diagnostics.ProductionRoot),"Diagnostics tests use only their unique temporary logs directory, never the production log directory");
  Diagnostics.Initialize();Diagnostics.Log("test","license=secret-value token=abc123 normal-checkpoint");
  var logs=new[]{Diagnostics.ActiveLogPath};assert(logs.Length==1&&File.ReadAllText(Diagnostics.ActiveLogPath).Contains(Diagnostics.SessionId),"Diagnostic logging appends to the single hexor.log active file");
  assert(logs.All(p=>!File.ReadAllText(p).Contains("secret-value")&&!File.ReadAllText(p).Contains("abc123")),"Diagnostic logging redacts secret-like values");
  var archive=Diagnostics.ExportSupportLogs();assert(File.Exists(archive),"Support log export creates a customer-reviewable ZIP");using(var zip=ZipFile.OpenRead(archive))assert(zip.Entries.Any(e=>e.Name=="diagnostic-summary.json")&&zip.Entries.All(e=>!e.FullName.Contains("license",StringComparison.OrdinalIgnoreCase)),"Support ZIP includes sanitized summary and excludes license artifacts");
  assert(SupportLogExportActions.ExplorerSelectArgument(archive).StartsWith("/select,\"")&&SupportLogExportActions.ExplorerSelectArgument(archive).Contains(Path.GetFileName(archive)),"Support export builds a safely quoted Explorer ZIP-selection argument");
  var calls=0;var fallback=SupportLogExportActions.OpenFolder(archive,_=>{calls++;if(calls==1)throw new IOException();return null;});assert(!fallback&&calls==2,"Explorer ZIP selection falls back to opening the containing folder");
  var prefix="concurrent-diagnostic-fixture-"+Guid.NewGuid().ToString("N")+"-";
  const int writers=12,recordsPerWriter=16;
  var expected=Enumerable.Range(0,writers).SelectMany(writer=>Enumerable.Range(0,recordsPerWriter).Select(record=>prefix+writer+"-"+record)).ToHashSet(StringComparer.Ordinal);
  Parallel.For(0,writers,writer=>{for(var record=0;record<recordsPerWriter;record++)Diagnostics.Log("concurrency-test",prefix+writer+"-"+record);});
  var observed=new List<string>();var wellFormed=true;
  foreach(var file in new[]{Diagnostics.ActiveLogPath})
  foreach(var line in File.ReadLines(file).Where(line=>line.Contains(prefix,StringComparison.Ordinal)))
  {
   try{var item=DiagnosticsRecord.Parse(line);wellFormed&=item!=null&&item.SessionId==Diagnostics.SessionId&&item.ProcessId==Environment.ProcessId&&item.Severity=="INFO"&&item.Component=="concurrency-test";observed.Add(item?.Message??"");}
   catch(FormatException){wellFormed=false;}
  }
  assert(wellFormed&&observed.Count==expected.Count&&observed.Distinct(StringComparer.Ordinal).Count()==expected.Count,"Concurrent diagnostic writers retain every complete text record exactly once");
  assert(expected.SetEquals(observed),"Concurrent cancellation and worker-style diagnostic producers cannot silently drop lifecycle outcomes");

  var executable=Environment.ProcessPath!;var crossPrefix="cross-"+Guid.NewGuid().ToString("N")+"-";const int processWriters=4,processRecords=20;
  var children=Enumerable.Range(0,processWriters).Select(writer=>Process.Start(new ProcessStartInfo(executable){UseShellExecute=false,ArgumentList={"--diagnostic-child",Diagnostics.Root,crossPrefix+writer+"-",processRecords.ToString()}})!).ToArray();
  foreach(var child in children){child.WaitForExit();assert(child.ExitCode==0,"Concurrent diagnostic child process exits successfully");child.Dispose();}
  var crossLines=File.ReadLines(Diagnostics.ActiveLogPath).Where(x=>x.Contains(crossPrefix,StringComparison.Ordinal)).ToArray();var crossRecords=crossLines.Select(x=>DiagnosticsRecord.Parse(x)!.Message).ToArray();
  assert(crossRecords.Length==processWriters*processRecords&&crossRecords.Distinct(StringComparer.Ordinal).Count()==crossRecords.Length,"Cross-process writers preserve complete JSON records exactly once across application sessions");

  var crashMarker="crash-"+Guid.NewGuid().ToString("N");using(var crash=Process.Start(new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,ArgumentList={"--diagnostic-crash-child",Diagnostics.Root,crashMarker}})!){assert(crash.StandardOutput.ReadLine()=="READY","Crash fixture commits its diagnostic record before termination");crash.Kill(true);crash.WaitForExit();}assert(File.ReadLines(Diagnostics.ActiveLogPath).Count(x=>x.Contains(crashMarker,StringComparison.Ordinal))==1,"Committed diagnostics survive abrupt application termination exactly once");

  using(var locked=new FileStream(Diagnostics.ActiveLogPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))assert(!Diagnostics.TryLog("locked-test","must-not-be-partial"),"Locked active log is handled without corrupting or partially writing a record");
  assert(Diagnostics.TryLog("locked-test","recovered")&&File.ReadLines(Diagnostics.ActiveLogPath).Any(x=>x.Contains("recovered",StringComparison.Ordinal)),"Logging recovers after a locked active file becomes available");

  for(var rotation=0;rotation<5;rotation++){using(var stream=new FileStream(Diagnostics.ActiveLogPath,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read))stream.SetLength(Diagnostics.ActiveLogMaxBytes);assert(Diagnostics.TryLog("rotation-test","record="+rotation),"Rotation completes before appending the next record");}
  var archives=Directory.EnumerateFiles(Diagnostics.Root,"hexor-*.log").ToArray();assert(archives.Length==3&&File.Exists(Diagnostics.ActiveLogPath),"Ten MB rotation retains the active log and newest three archives only");
  assert(DiagnosticsRecord.Parse(File.ReadLines(Diagnostics.ActiveLogPath).Last())!.Message=="record=4","Record integrity is preserved after rotation");

  using(var stream=new FileStream(Diagnostics.ActiveLogPath,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read))stream.SetLength(Diagnostics.ActiveLogMaxBytes);
  var rotatePrefix="rotate-"+Guid.NewGuid().ToString("N")+"-";var rotating=Enumerable.Range(0,processWriters).Select(writer=>Process.Start(new ProcessStartInfo(executable){UseShellExecute=false,ArgumentList={"--diagnostic-child",Diagnostics.Root,rotatePrefix+writer+"-","5"}})!).ToArray();foreach(var child in rotating){child.WaitForExit();assert(child.ExitCode==0,"Concurrent rotation writer exits successfully");child.Dispose();}
  var allLogs=new[]{Diagnostics.ActiveLogPath}.Concat(Directory.EnumerateFiles(Diagnostics.Root,"hexor-*.log"));var rotatedRecords=allLogs.SelectMany(File.ReadLines).Where(x=>x.Contains(rotatePrefix,StringComparison.Ordinal)).Select(x=>DiagnosticsRecord.Parse(x)!.Message).ToArray();assert(rotatedRecords.Length==20&&rotatedRecords.Distinct(StringComparer.Ordinal).Count()==20,"Rotation under concurrent process writers retains every JSON record exactly once");

  Diagnostics.Log("redaction-test","password=hunter2 authorization=BearerToken license=ABC key=XYZ normal=retained");var finalArchive=Diagnostics.ExportSupportLogs();assert(!string.IsNullOrEmpty(finalArchive),"Support export succeeds after rotation: "+Diagnostics.LastExportError);using(var finalZip=ZipFile.OpenRead(finalArchive)){var exported=string.Join("\n",finalZip.Entries.Where(e=>e.Name.EndsWith(".log",StringComparison.OrdinalIgnoreCase)).Select(e=>{using var reader=new StreamReader(e.Open());return reader.ReadToEnd();}));assert(!exported.Contains("hunter2")&&!exported.Contains("BearerToken")&&!exported.Contains("license=ABC")&&!exported.Contains("key=XYZ")&&exported.Contains("normal=retained"),"Support export redacts sensitive values while preserving useful diagnostics");}
 }
}

// Parser for Hexor's plain-text diagnostic record format:
// 2026-11-19 17:32:41.123 +08:00 [INFO] [sessionId] [pid 1234] component: message
internal sealed record DiagnosticsRecord(string Timestamp,string Severity,string SessionId,int ProcessId,string Component,string Message)
{
 private static readonly Regex Pattern=new(
  @"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<severity>INFO|WARN|ERROR)\] \[(?<sessionId>[0-9a-f]{32})\] \[pid (?<processId>\d+)\] (?<component>[^:]+): (?<message>.*)$",
  RegexOptions.CultureInvariant);
 public static DiagnosticsRecord? Parse(string line)
 {
  var match=Pattern.Match(line);
  return match.Success ? new DiagnosticsRecord(
   match.Groups["timestamp"].Value,
   match.Groups["severity"].Value,
   match.Groups["sessionId"].Value,
   int.Parse(match.Groups["processId"].Value),
   match.Groups["component"].Value,
   match.Groups["message"].Value) : null;
 }
}
