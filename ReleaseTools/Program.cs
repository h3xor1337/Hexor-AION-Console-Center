using System.Security.Cryptography;
using System.Text.Json;
using Hexor.Updates;

try
{
 if(args.Length==4&&args[0]=="verify-download")
 {
  var trust=JsonSerializer.Deserialize<Trust>(File.ReadAllText(args[2]))??throw new InvalidDataException("Missing public trust.");
  UpdateCore.ValidateManifestUrl(trust.ManifestUrl);
  var release=UpdateCore.Verify(File.ReadAllText(args[1]),trust.PublicKeyPem);
  await UpdateCore.Download(release.DownloadUrl,args[3],600_000_000);
  try{UpdateCore.VerifyPackage(args[3],release);}catch{File.Delete(args[3]);throw;}
  Console.WriteLine("Verified public GitHub package for Hexor "+release.Version);return 0;
 }
 if(args.Length==5&&args[0]=="verify-file")
 {
  UpdateCore.ValidateReleaseUrl(args[1]);var expected=Convert.FromHexString(args[2]);if(expected.Length!=32)throw new InvalidDataException("Expected SHA-256 digest.");
  await UpdateCore.Download(args[1],args[3],long.Parse(args[4]));
  try{using var file=File.OpenRead(args[3]);if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file),expected))throw new InvalidDataException("Public GitHub asset checksum is invalid.");}catch{File.Delete(args[3]);throw;}
  Console.WriteLine("Verified public GitHub release asset.");return 0;
 }
 if(args.Length==2&&args[0]=="manifest-url"){Console.WriteLine(UpdateCore.ManifestUrl(args[1]));return 0;}
 if(args.Length==4&&args[0]=="fetch-manifest")
 {
  UpdateCore.ValidateManifestUrl(args[1]);var trust=JsonSerializer.Deserialize<Trust>(File.ReadAllText(args[2]))??throw new InvalidDataException("Missing public trust.");
  if(trust.ManifestUrl!=args[1])throw new InvalidDataException("Manifest URL does not match configured public trust.");
  await UpdateCore.Download(args[1],args[3],131072);
  var release=UpdateCore.Verify(File.ReadAllText(args[3]),trust.PublicKeyPem);Console.WriteLine("Verified public manifest for Hexor "+release.Version);return 0;
 }
 Console.Error.WriteLine("Usage: verify-download <manifest> <public-trust> <destination> | verify-file <url> <sha256> <destination> <max-bytes> | manifest-url <owner/repo> | fetch-manifest <url> <public-trust> <destination>");return 2;
}
catch(Exception ex){Console.Error.WriteLine("Release verification failed: "+ex.Message);return 1;}
