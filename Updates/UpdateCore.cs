using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hexor.Updates;
public sealed record Trust(string ManifestUrl, string PublicKeyPem)
{
 public static Trust Load()
 {
  var trust=JsonSerializer.Deserialize<Trust>(typeof(Trust).Assembly.GetManifestResourceStream("Hexor.UpdateTrust")!)!;
  if(!string.IsNullOrEmpty(trust.ManifestUrl))UpdateCore.ValidateManifestUrl(trust.ManifestUrl);
  return trust;
 }
}
// Signature covers the exact UTF-8 payload bytes, avoiding ambiguous JSON canonicalization.
public sealed record Envelope(string Payload, string Signature);
public sealed record Manifest(int SchemaVersion, string Version, string ReleaseDate, string DownloadUrl, string Sha256, string ReleaseNotes, string MinimumUpdaterVersion, bool Mandatory, string? Aion2UpdaterUrl = null, string? Aion2UpdaterSha256 = null, string? LightDownloadUrl = null, string? LightSha256 = null, string? LightMinUpdaterVersion = null);
public sealed record DownloadProgress(long BytesReceived,long? TotalBytes)
{
 public double? Percentage=>TotalBytes is >0?100.0*BytesReceived/TotalBytes.Value:null;
}
public static class UpdateCore
{
 public const string UpdaterVersion="1.0.0";
 public const string Aion2UpdaterEntry="Aion2.Updater.exe";
 public const string Aion2UpdaterSubfolder="AION Auto Updater";
 public const string Aion2UpdaterStamp="Aion2.Updater.hexor-stamp";
 public static readonly string[] OwnedFiles=["Hexor-AION-Control-Center.exe","Hexor.Updater.exe"];
 public static string GateName(string install)=>"Local\\HexorUpdate-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(install)).ToUpperInvariant())));
 public static Version ParseVersion(string value)
 {
  if(!Regex.IsMatch(value,@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))throw new InvalidDataException("Only stable major.minor.patch releases are supported.");
  return Version.Parse(value);
 }
 public static Uri Https(string value){if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Fragment))throw new InvalidDataException("Update URL must use HTTPS.");return uri;}
 public static Manifest Verify(string json,string key)
 {
  if(json.Length>131072)throw new InvalidDataException("Manifest too large.");
  var envelope=JsonSerializer.Deserialize<Envelope>(json)??throw new InvalidDataException("Missing signed envelope.");
  var bytes=Convert.FromBase64String(envelope.Payload);
  using var rsa=RSA.Create();rsa.ImportFromPem(key);
  if(rsa.KeySize<3072||!rsa.VerifyData(bytes,Convert.FromBase64String(envelope.Signature),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new InvalidDataException("Release signature is invalid.");
  var m=JsonSerializer.Deserialize<Manifest>(bytes)??throw new InvalidDataException("Missing release metadata.");
  if(m.SchemaVersion!=1||!Regex.IsMatch(m.Sha256,@"^[a-fA-F0-9]{64}$")||m.ReleaseNotes.Length>24000||!DateTimeOffset.TryParse(m.ReleaseDate,out _))throw new InvalidDataException("Unsupported or malformed release metadata.");
  if(m.Aion2UpdaterSha256 is {Length:>0} companion && !Regex.IsMatch(companion,@"^[a-fA-F0-9]{64}$"))throw new InvalidDataException("Unsupported companion updater metadata.");
  if(!string.IsNullOrEmpty(m.Aion2UpdaterUrl))ValidateReleaseUrl(m.Aion2UpdaterUrl);
  if((m.Aion2UpdaterUrl is null)!=(string.IsNullOrEmpty(m.Aion2UpdaterSha256)))throw new InvalidDataException("Companion updater metadata is incomplete.");
  if(!string.IsNullOrEmpty(m.LightDownloadUrl))ValidateReleaseUrl(m.LightDownloadUrl);
  if((m.LightDownloadUrl is null)!=(string.IsNullOrEmpty(m.LightSha256)))throw new InvalidDataException("Light package metadata is incomplete.");
  if(!string.IsNullOrEmpty(m.LightMinUpdaterVersion))ParseVersion(m.LightMinUpdaterVersion);
  ParseVersion(m.Version);if(ParseVersion(m.MinimumUpdaterVersion)>ParseVersion(UpdaterVersion))throw new InvalidDataException("This release requires a newer AIO installer.");ValidateReleaseUrl(m.DownloadUrl);return m;
 }
 public const int MaximumRedirects=5;
 private static readonly string[] DeliveryHosts=["release-assets.githubusercontent.com","objects.githubusercontent.com","github-releases.githubusercontent.com"];
 public static string ManifestUrl(string repository)
 {
  if(!Regex.IsMatch(repository,@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$"))throw new InvalidDataException("Specify the actual GitHub owner/repository.");
  var parts=repository.Split('/');
  if(new[]{"OWNER","YOUR-OWNER","YOUR_OWNER","YOUR_ORG","YOUR-ORG","YOUR-USERNAME","USERNAME","ORG","REPLACE-ME","PLACEHOLDER"}.Contains(parts[0],StringComparer.OrdinalIgnoreCase)||new[]{"REPO","YOUR-REPO","PLACEHOLDER"}.Contains(parts[1],StringComparer.OrdinalIgnoreCase)||parts[1].EndsWith(".git",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Placeholder GitHub configuration is not allowed.");
  return "https://raw.githubusercontent.com/"+repository+"/main/latest.json";
 }
 public static Uri ValidateManifestUrl(string value)
 {
  var uri=ValidateGitHubUrl(value,false);var match=Regex.Match(uri.AbsolutePath,@"^/([^/]+/[^/]+)/main/latest\.json$");
  if(uri.Host!="raw.githubusercontent.com"||!match.Success||uri.Query.Length!=0||!string.Equals(ManifestUrl(match.Groups[1].Value),uri.AbsoluteUri,StringComparison.Ordinal))throw new InvalidDataException("Manifest URL must be the actual public GitHub repository's raw main/latest.json URL.");
  return uri;
 }
 public static Uri ValidateReleaseUrl(string value)
 {
  var uri=ValidateGitHubUrl(value,false);
  if(uri.Host!="github.com"||!Regex.IsMatch(uri.AbsolutePath,@"^/[^/]+/[^/]+/releases/download/[^/]+/[^/]+$")||uri.Query.Length!=0)throw new InvalidDataException("Update packages must use a versioned GitHub Releases asset URL.");
  return uri;
 }
 internal static Uri ValidateGitHubUrl(string value,bool redirect)
 {
  var uri=Https(value);
  if(uri.Port!=443||uri.HostNameType!=UriHostNameType.Dns)throw new InvalidDataException("Only public GitHub HTTPS endpoints on port 443 are allowed.");
  var approved=DeliveryHosts.Contains(uri.IdnHost,StringComparer.OrdinalIgnoreCase)||uri.IdnHost=="github.com"||(!redirect&&uri.IdnHost=="raw.githubusercontent.com");
  if(!approved)throw new InvalidDataException("Download host is not an approved GitHub delivery domain.");
  // Initial package URLs are stable github.com URLs; CDN URLs are accepted only as redirects.
  if(!redirect&&uri.IdnHost!="github.com"&&uri.IdnHost!="raw.githubusercontent.com")throw new InvalidDataException("Start downloads at GitHub Releases or raw GitHub content.");
  return uri;
 }
 internal static bool IsPublicAddress(IPAddress address)
 {
  if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();var bytes=address.GetAddressBytes();
  if(address.AddressFamily==AddressFamily.InterNetwork)
  {
   return !(bytes[0]==0||bytes[0]==10||bytes[0]==127||bytes[0]>=224||
    bytes[0]==100&&(bytes[1]&192)==64||bytes[0]==169&&bytes[1]==254||bytes[0]==172&&bytes[1]>=16&&bytes[1]<=31||
    bytes[0]==192&&(bytes[1]==168||bytes[1]==0&&(bytes[2]==0||bytes[2]==2)||bytes[1]==88&&bytes[2]==99)||
    bytes[0]==198&&(bytes[1]==18||bytes[1]==19||bytes[1]==51&&bytes[2]==100)||bytes[0]==203&&bytes[1]==0&&bytes[2]==113);
  }
  if(address.AddressFamily!=AddressFamily.InterNetworkV6||address.ScopeId!=0||(bytes[0]&224)!=32)return false;
  // Exclude special-use/transition ranges as well as IPv6 local, multicast and mapped private addresses.
  return !(bytes[0]==32&&bytes[1]==1&&bytes[2]<=1||bytes[0]==32&&bytes[1]==1&&bytes[2]==13&&bytes[3]==184||
   bytes[0]==32&&bytes[1]==2||bytes[0]==63&&bytes[1]==255&&(bytes[2]&240)==0);
 }
 internal static void ValidateAddresses(IPAddress[] addresses)
 {
  if(addresses.Length==0||addresses.Any(a=>!IsPublicAddress(a)))throw new HttpRequestException("GitHub hostname resolved to a local, private, or reserved network destination.");
 }
 private static async ValueTask<Stream> ConnectPublic(SocketsHttpConnectionContext context,CancellationToken token)
 {
  var host=context.DnsEndPoint.Host;
  if(context.DnsEndPoint.Port!=443||!(host=="github.com"||host=="raw.githubusercontent.com"||DeliveryHosts.Contains(host,StringComparer.OrdinalIgnoreCase)))throw new HttpRequestException("Unapproved network endpoint.");
  var addresses=await Dns.GetHostAddressesAsync(host,token);ValidateAddresses(addresses);SocketException? last=null;
  foreach(var address in addresses)
  {
   var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp){NoDelay=true};
   try{await socket.ConnectAsync(new IPEndPoint(address,443),token);return new NetworkStream(socket,ownsSocket:true);}
   catch(SocketException ex){socket.Dispose();last=ex;}catch{socket.Dispose();throw;}
  }
  throw new HttpRequestException("Unable to connect to GitHub.",last);
 }
 public static Task Download(string url,string destination,long limit,IProgress<DownloadProgress>? progress=null,CancellationToken token=default)
 {
  var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,Credentials=null,PreAuthenticate=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(20),ConnectCallback=ConnectPublic};
  return DownloadWithHandler(url,destination,limit,handler,progress,token);
 }
 // Internal seam keeps deterministic transport tests off the network; production always uses ConnectPublic.
 internal static async Task DownloadWithHandler(string url,string destination,long limit,HttpMessageHandler handler,IProgress<DownloadProgress>? progress=null,CancellationToken token=default)
 {
  using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(60));var ct=deadline.Token;
  ct.ThrowIfCancellationRequested();if(limit<1)throw new ArgumentOutOfRangeException(nameof(limit));var uri=ValidateGitHubUrl(url,false);
  for(int redirects=0;;redirects++)
  {
   using var request=new HttpRequestMessage(HttpMethod.Get,uri){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};request.Headers.UserAgent.ParseAdd("Hexor-Updater/"+UpdaterVersion);
   using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
   if(response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
   {
    if(redirects>=MaximumRedirects)throw new HttpRequestException("GitHub download exceeded the redirect limit.");
    var location=response.Headers.Location??throw new HttpRequestException("GitHub redirect is missing its destination.");
    uri=ValidateGitHubUrl(new Uri(uri,location).AbsoluteUri,true);continue;
   }
   if(response.StatusCode!=HttpStatusCode.OK)throw new HttpRequestException("GitHub download returned HTTP "+(int)response.StatusCode+".",null,response.StatusCode);
   var size=response.Content.Headers.ContentLength;if(size>limit)throw new InvalidDataException("Download too large.");
   bool created=false;
   try
   {
    await using var source=await response.Content.ReadAsStreamAsync(ct);await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);created=true;byte[] buffer=new byte[81920];long total=0;int read;
    while((read=await source.ReadAsync(buffer,ct))>0){total+=read;if(total>limit)throw new InvalidDataException("Download exceeded size limit.");await output.WriteAsync(buffer.AsMemory(0,read),ct);progress?.Report(new(total,size));}
    if(size.HasValue&&total!=size.Value)throw new IOException("GitHub download was interrupted or truncated.");output.Flush(true);return;
   }catch{if(created)File.Delete(destination);throw;}
  }
 }
 public static void VerifyPackage(string package,Manifest manifest)=>VerifyPackage(package,manifest.Sha256);
 public static void VerifyPackage(string package,string sha256)
 {
  using var stream=File.OpenRead(package);if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Package checksum is invalid.");
 }
 // Light packages carry the console without the updater and are used only when the installed
 // updater understands them. Older consoles ignore these manifest fields entirely and keep
 // using the full package, so a light release never strands an old installation.
 public static bool UseLightPackage(Manifest manifest,string helperPath)
 {
  try
  {
   var helper=FileVersionInfo.GetVersionInfo(helperPath);
   return UseLightPackage(manifest,new Version(helper.FileMajorPart,helper.FileMinorPart,helper.FileBuildPart));
  }
  catch(Exception e) when (e is ArgumentException or System.ComponentModel.Win32Exception){return false;}
 }
 public static bool UseLightPackage(Manifest manifest,Version helperVersion)
 {
  try
  {
   if(string.IsNullOrWhiteSpace(manifest.LightDownloadUrl)||string.IsNullOrWhiteSpace(manifest.LightSha256)||string.IsNullOrWhiteSpace(manifest.LightMinUpdaterVersion))return false;
   ValidateReleaseUrl(manifest.LightDownloadUrl);
   return helperVersion>=ParseVersion(manifest.LightMinUpdaterVersion);
  }
  catch(Exception e) when (e is InvalidDataException or ArgumentException){return false;}
 }
 public static void Stage(string package,string stage)
 {
  Directory.CreateDirectory(stage);using var zip=ZipFile.OpenRead(package);
  // The package always carries the console; the companion updater travels in it only when the
  // updater itself changed, so routine releases download half the bytes.
  if(zip.Entries.Count<1||zip.Entries.Count>OwnedFiles.Length)throw new InvalidDataException("Package must contain the Hexor executables.");
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var hasConsole=false;
  foreach(var entry in zip.Entries){if(!OwnedFiles.Contains(entry.FullName,StringComparer.Ordinal)||!seen.Add(entry.FullName)||entry.Length<2||entry.Length>600_000_000||(entry.ExternalAttributes>>16&0xF000)==0xA000)throw new InvalidDataException("Unsafe archive entry.");if(string.Equals(entry.FullName,OwnedFiles[0],StringComparison.Ordinal))hasConsole=true;entry.ExtractToFile(Path.Combine(stage,entry.FullName));using var file=File.OpenRead(Path.Combine(stage,entry.FullName));if(file.ReadByte()!=77||file.ReadByte()!=90)throw new InvalidDataException("Invalid executable.");}
  if(!hasConsole)throw new InvalidDataException("Package must contain the Hexor console.");
 }
 public static string Aion2UpdaterTarget(string installDirectory)
 {
  var root=Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)))?.FullName??throw new InvalidOperationException("Cannot locate the companion updater folder.");
  return Path.Combine(root,Aion2UpdaterSubfolder,Aion2UpdaterEntry);
 }
 // The companion updater is delivered as a separate single-entry archive next to the main release.
 public static void StageAion2Updater(string package,string stage)
 {
  Directory.CreateDirectory(stage);using var zip=ZipFile.OpenRead(package);
  if(zip.Entries.Count!=1||!string.Equals(zip.Entries[0].FullName,Aion2UpdaterEntry,StringComparison.Ordinal)||zip.Entries[0].Length<2||zip.Entries[0].Length>600_000_000||(zip.Entries[0].ExternalAttributes>>16&0xF000)==0xA000)throw new InvalidDataException("Companion updater package is malformed.");
  zip.Entries[0].ExtractToFile(Path.Combine(stage,Aion2UpdaterEntry),true);
  using var file=File.OpenRead(Path.Combine(stage,Aion2UpdaterEntry));if(file.ReadByte()!=77||file.ReadByte()!=90)throw new InvalidDataException("Invalid executable.");
 }
 public static void InstallAion2Updater(string target,string staged)
 {
  var directory=Path.GetDirectoryName(target)!;Directory.CreateDirectory(directory);
  var temporary=Path.Combine(directory,Guid.NewGuid().ToString("N")+".new");File.Copy(staged,temporary,true);
  try{if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);}
  finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
 public static void NoLinks(string path)
 {
  for(var dir=new DirectoryInfo(Path.GetFullPath(path));dir!=null;dir=dir.Parent)if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked installation paths are unsupported.");
  foreach(var name in OwnedFiles){var file=Path.Combine(path,name);if(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked application files are unsupported.");}
 }
 // Backups and journal remain adjacent to the installation, on the same volume.
 public static void Recover(string install)
 {
  NoLinks(install);var txn=Path.Combine(install,".hexor-update");NoLinks(txn);
  var pending=Path.Combine(txn,"pending");if(!File.Exists(pending))return;
  foreach(var name in ReadPending(pending)){var backup=Path.Combine(txn,name);if(!File.Exists(backup))throw new IOException("Recovery backup is missing; retain the transaction folder.");AtomicCopy(backup,Path.Combine(install,name),txn);}
  File.Delete(pending);
 }
 // The pending journal lists the files this transaction replaced, one per line. Journals from
 // earlier releases hold a single status byte and covered every owned file.
 static string[] ReadPending(string pending)
 {
  var names=File.ReadLines(pending).Where(n=>OwnedFiles.Contains(n,StringComparer.Ordinal)).ToArray();
  return names.Length>0?names:OwnedFiles;
 }
 public static void Install(string install,string stage,Action<int>? afterReplace=null)
 {
  NoLinks(install);NoLinks(stage);Recover(install);var txn=Path.Combine(install,".hexor-update");Directory.CreateDirectory(txn);NoLinks(txn);
  // Only the files the staged package carries are replaced; a package without the updater
  // leaves the installed helper untouched and does not need a backup for it.
  var affected=OwnedFiles.Where(n=>File.Exists(Path.Combine(stage,n))).ToArray();
  foreach(var name in affected)if(!File.Exists(Path.Combine(install,name)))throw new IOException("Installation is missing "+name+"; a full package is required.");
  foreach(var name in affected){var backup=Path.Combine(txn,name);if(File.Exists(backup)&&(File.GetAttributes(backup)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked backup rejected.");using var source=File.OpenRead(Path.Combine(install,name));using var target=new FileStream(backup,FileMode.Create,FileAccess.Write,FileShare.None);source.CopyTo(target);target.Flush(true);}
  using(var journal=new FileStream(Path.Combine(txn,"pending"),FileMode.Create,FileAccess.Write,FileShare.None)){using var writer=new StreamWriter(journal);foreach(var name in affected)writer.WriteLine(name);journal.Flush(true);}
  try{for(int i=0;i<affected.Length;i++){var name=affected[i];AtomicCopy(Path.Combine(stage,name),Path.Combine(install,name),txn);afterReplace?.Invoke(i);}File.Delete(Path.Combine(txn,"pending"));}
  catch{Recover(install);throw;}
 }
 static void AtomicCopy(string source,string destination,string txn)
 {
  var temporary=Path.Combine(txn,Guid.NewGuid().ToString("N")+".new");
  try{using(var input=File.OpenRead(source))using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}File.Replace(temporary,destination,null);}
  finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
}
